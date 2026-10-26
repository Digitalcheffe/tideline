using System.Data;
using System.Text.Json;
using Dapper;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

public enum EditError { NotFound, Conflict, Unprocessable, Invalid }

public sealed class EditException(EditError kind, string message) : Exception(message)
{
    public EditError Kind { get; } = kind;
}

/// <summary>
/// Partial edit: a null field is left alone. To clear a value send an empty string (payee, notes) or 0 (recurring, plan).
/// </summary>
public sealed record EditRequest(
    string? Version,
    long? CategoryId = null,
    string? Type = null,
    string? Payee = null,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null,
    long? RecurringId = null,
    long? PlanId = null,
    long? AssetId = null);

/// <summary>One change applied to many lines at once (docs/ARCHITECTURE.md, POST /transactions/bulk). Null parts are left alone; an asset of 0 unlinks.</summary>
public sealed record BulkChange(long? CategoryId = null, string? Type = null, IReadOnlyList<string>? AddTags = null, IReadOnlyList<string>? RemoveTags = null, long? AssetId = null);

public sealed record BulkResult(int Updated, int SkippedTransfers, int NotFound);

/// <summary>Lines left out in one go. Skipped ones are in a transfer or were already left out; a transfer has to be unpaired first.</summary>
public sealed record LeaveOutResult(int LeftOut, int Skipped, int NotFound);

public sealed record UndoImportResult(int LeftOut, int Unpaired);

public sealed record SplitInput(long AmountCents, long CategoryId, string Type, IReadOnlyList<string>? Tags = null, string? Note = null);

public sealed record ManualEntry(
    long AccountId,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    long? CategoryId = null,
    string? Type = null,
    string? Payee = null,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null);

/// <summary>
/// Edits to a ledger line. The bank fields are never touched (the triggers would refuse); everything a person
/// changes is audited in the same database transaction (docs/ARCHITECTURE.md).
/// </summary>
public sealed class TransactionEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null)
{
    private sealed class Current
    {
        public long Id { get; set; }
        public long AccountId { get; set; }
        public long AmountCents { get; set; }
        public string OriginalDescription { get; set; } = "";
        public string Source { get; set; } = "";
        public string? RawJson { get; set; }
        public long? CategoryId { get; set; }
        public string? Type { get; set; }
        public string? Payee { get; set; }
        public string? Notes { get; set; }
        public long? RecurringId { get; set; }
        public long? PlanId { get; set; }
        public long? AssetId { get; set; }
        public long? RuleId { get; set; }
        public long? MoveId { get; set; }
        public bool IsManualOverride { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string? LeftOutAt { get; set; }
    }

    private Current Load(long id, IDbTransaction tx) =>
        conn.QuerySingleOrDefault<Current>("""
            SELECT id AS Id, account_id AS AccountId, amount_cents AS AmountCents, original_description AS OriginalDescription,
                   source AS Source, raw_json AS RawJson, category_id AS CategoryId, type AS Type, payee AS Payee, notes AS Notes,
                   recurring_id AS RecurringId, plan_id AS PlanId, asset_account_id AS AssetId, rule_id AS RuleId, move_id AS MoveId,
                   is_manual_override AS IsManualOverride, updated_at AS UpdatedAt, left_out_at AS LeftOutAt
            FROM transactions WHERE id = @id
            """, new { id }, tx)
        ?? throw new EditException(EditError.NotFound, $"There is no transaction {id}.");

    private static void RequireNotLeftOut(Current c)
    {
        if (c.LeftOutAt is not null)
            throw new EditException(EditError.Unprocessable, "This line is left out of the totals. Bring it back first to change it.");
    }

    private static void RequireNotMove(Current c)
    {
        if (c.MoveId is not null)
            throw new EditException(EditError.Unprocessable, "This line is a transfer between your accounts. Transfers have no category, type or splits.");
    }

    private void CheckVersion(Current c, string? version, IDbTransaction tx)
    {
        if (string.IsNullOrEmpty(version)) throw new EditException(EditError.Invalid, "Send the version you were looking at, so edits cannot overwrite each other.");
        if (version != TransactionVersion.Of(conn, c.Id, tx)) throw new EditException(EditError.Conflict, "This line changed since you opened it. Reload it and try again.");
    }

    private static LineType ParseType(string text)
    {
        try { return TypeHandlers.FromDb<LineType>(text.Trim().ToLowerInvariant()); }
        catch (InvalidDataException) { throw new EditException(EditError.Invalid, $"'{text}' is not a type. Use income, bill, required, extra, one_time or savings."); }
    }

    private void RequireCategory(long id, IDbTransaction tx)
    {
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM categories WHERE id = @id", new { id }, tx) == 0)
            throw new EditException(EditError.Invalid, $"There is no category {id}.");
    }

    private static string NormalizeTag(string raw)
    {
        var name = string.Join('-', raw.Trim().ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new EditException(EditError.Invalid, "A tag cannot be empty.");
        if (name.Length > 50) throw new EditException(EditError.Invalid, $"The tag '{name[..20]}…' is too long (50 characters at most).");
        return name;
    }

    private IReadOnlyList<long> TagIds(IEnumerable<string> names, IDbTransaction tx)
    {
        var tags = new TagRepository(conn, tx);
        return names.Select(NormalizeTag).Distinct().Select(tags.GetOrCreate).ToList();
    }

    private object Snapshot(long id, IDbTransaction tx)
    {
        var c = Load(id, tx);
        var tags = new TagRepository(conn, tx).NamesFor(id);
        return new { c.CategoryId, c.Type, c.Payee, c.Notes, c.RecurringId, c.PlanId, c.AssetId, c.RuleId, c.IsManualOverride, Tags = tags };
    }

    /// <summary>Only an asset account (a house, a car) can be linked to a line.</summary>
    private void RequireAsset(long id, IDbTransaction tx)
    {
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE id = @id AND kind = 'asset'", new { id }, tx) == 0)
            throw new EditException(EditError.Invalid, $"Account {id} is not an asset. Add the house or car as an asset account in Settings first.");
    }

    private string Now() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private void Touch(long id, IDbTransaction tx) =>
        conn.Execute("UPDATE transactions SET updated_at = @now WHERE id = @id", new { id, now = clock.GetUtcNow() }, tx);

    private string NewVersion(long id, IDbTransaction tx) => TransactionVersion.Of(conn, id, tx);

    // ---- PATCH ---------------------------------------------------------------------------------------------

    /// <returns>The line's new version.</returns>
    public string Update(long id, EditRequest r)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var c = Load(id, tx);
        RequireNotMove(c);
        RequireNotLeftOut(c);
        CheckVersion(c, r.Version, tx);

        LineType? type = r.Type is null ? null : ParseType(r.Type);
        if (r.CategoryId is { } cat and > 0) RequireCategory(cat, tx);
        if (r.RecurringId is { } rec and > 0 && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM recurring WHERE id = @rec", new { rec }, tx) == 0)
            throw new EditException(EditError.Invalid, $"There is no recurring item {rec}.");
        if (r.AssetId is { } asset and > 0) RequireAsset(asset, tx);
        var tagIds = r.Tags is null ? null : TagIds(r.Tags, tx);

        var before = Snapshot(id, tx);
        conn.Execute("""
            UPDATE transactions SET
                category_id = @category, type = @type, payee = @payee, notes = @notes, recurring_id = @recurring, plan_id = @plan,
                asset_account_id = @asset, is_manual_override = 1, updated_at = @now
            WHERE id = @id
            """, new
        {
            asset = r.AssetId is { } aa ? (aa == 0 ? null : (long?)aa) : c.AssetId,
            id,
            category = r.CategoryId is { } k ? (k == 0 ? null : (long?)k) : c.CategoryId,
            type = type?.ToDb() ?? c.Type,
            payee = r.Payee is null ? c.Payee : (r.Payee.Length == 0 ? null : r.Payee.Trim()),
            notes = r.Notes is null ? c.Notes : (r.Notes.Length == 0 ? null : r.Notes),
            recurring = r.RecurringId is { } rr ? (rr == 0 ? null : (long?)rr) : c.RecurringId,
            plan = r.PlanId is { } pp ? (pp == 0 ? null : (long?)pp) : c.PlanId,
            now = clock.GetUtcNow(),
        }, tx);

        if (tagIds is not null)
        {
            conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id", new { id }, tx);
            foreach (var t in tagIds) conn.Execute("INSERT INTO transaction_tags (transaction_id, tag_id) VALUES (@id, @t)", new { id, t }, tx);
        }

        new AuditWriter(conn, clock, tx, actor).Write("transaction.update", "transaction", id, before, Snapshot(id, tx));
        var version = NewVersion(id, tx);
        tx.Commit();
        return version;
    }

    // ---- bulk ----------------------------------------------------------------------------------------------

    public const int MaxBulk = 500;

    /// <summary>
    /// Apply one change to many lines in one database transaction: either every line changes or none does. Transfers have no
    /// category or type, so they are skipped and counted. Each line gets its own audit entry, and is marked as set by hand.
    /// </summary>
    public BulkResult BulkUpdate(IReadOnlyList<long> ids, BulkChange change)
    {
        TypeHandlers.EnsureRegistered();
        if (ids.Count == 0) throw new EditException(EditError.Invalid, "Pick at least one line.");
        if (ids.Count > MaxBulk) throw new EditException(EditError.Invalid, $"Change {MaxBulk} lines or fewer at a time.");
        if (change.CategoryId is null && change.Type is null && change.AssetId is null && (change.AddTags?.Count ?? 0) == 0 && (change.RemoveTags?.Count ?? 0) == 0)
            throw new EditException(EditError.Invalid, "Say what to change: a category, a type, an asset, or tags to add or remove.");

        using var tx = conn.BeginTransaction();
        if (change.AssetId is { } linkTo and > 0) RequireAsset(linkTo, tx);

        LineType? type = change.Type is null ? null : ParseType(change.Type);
        if (change.CategoryId is { } cat and > 0) RequireCategory(cat, tx);
        var add = change.AddTags is null ? [] : TagIds(change.AddTags, tx);
        var remove = (change.RemoveTags ?? []).Select(NormalizeTag).Distinct().ToList();
        var audit = new AuditWriter(conn, clock, tx, actor);

        int updated = 0, skipped = 0, missing = 0;
        foreach (var id in ids.Distinct())
        {
            var c = conn.QuerySingleOrDefault<Current>("SELECT id AS Id, move_id AS MoveId, left_out_at AS LeftOutAt FROM transactions WHERE id = @id", new { id }, tx);
            if (c is null) { missing++; continue; }
            if (c.MoveId is not null || c.LeftOutAt is not null) { skipped++; continue; }

            var before = Snapshot(id, tx);
            conn.Execute("""
                UPDATE transactions SET category_id = COALESCE(@category, category_id), type = COALESCE(@type, type),
                    asset_account_id = CASE WHEN @setAsset = 1 THEN @asset ELSE asset_account_id END,
                    is_manual_override = 1, updated_at = @now
                WHERE id = @id
                """, new
            {
                id, category = change.CategoryId is { } k and > 0 ? k : (long?)null, type = type?.ToDb(), now = clock.GetUtcNow(),
                setAsset = change.AssetId is null ? 0 : 1, asset = change.AssetId is { } av and > 0 ? av : (long?)null,
            }, tx);
            foreach (var t in add)
            {
                if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM transaction_tags WHERE transaction_id = @id AND tag_id = @t", new { id, t }, tx) == 0)
                    conn.Execute("INSERT INTO transaction_tags (transaction_id, tag_id) VALUES (@id, @t)", new { id, t }, tx);
            }
            foreach (var name in remove)
                conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id AND tag_id IN (SELECT id FROM tags WHERE name = @name)", new { id, name }, tx);

            audit.Write("transaction.update", "transaction", id, before, Snapshot(id, tx));
            updated++;
        }

        tx.Commit();
        return new BulkResult(updated, skipped, missing);
    }

    // ---- reset to rules --------------------------------------------------------------------------------------

    public string ResetToRules(long id)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var c = Load(id, tx);
        RequireNotMove(c);
        RequireNotLeftOut(c);

        var before = Snapshot(id, tx);
        RunRules(c, LineClassifier.Load(conn, clock, tx), tx);

        new AuditWriter(conn, clock, tx, actor).Write("transaction.reset", "transaction", id, before, Snapshot(id, tx));
        var version = NewVersion(id, tx);
        tx.Commit();
        return version;
    }

    /// <summary>Gives a line the meaning the rules would give it now, and clears the "set by hand" mark.</summary>
    private void RunRules(Current c, LineClassifier classifier, IDbTransaction tx)
    {
        var account = new AccountRepository(conn, clock, tx).Get(c.AccountId)!;
        var merchant = "";
        if (!string.IsNullOrEmpty(c.RawJson))
        {
            try { merchant = JsonSerializer.Deserialize<Dictionary<string, string>>(c.RawJson)?.GetValueOrDefault("Merchant name") ?? ""; }
            catch (JsonException) { /* no merchant name to use */ }
        }

        var k = classifier.Classify(new RuleLine(c.OriginalDescription, c.AmountCents, account.BankNumberHash ?? "", account.Kind), merchant);
        var id = c.Id;
        conn.Execute("""
            UPDATE transactions SET category_id = @category, type = @type, payee = @payee, recurring_id = @recurring, plan_id = @plan,
                rule_id = @rule, is_manual_override = 0, updated_at = @now
            WHERE id = @id
            """, new { id, category = k.CategoryId, type = k.Type.ToDb(), payee = k.Payee, recurring = k.RecurringId, plan = k.PlanId, rule = k.RuleId, now = clock.GetUtcNow() }, tx);
        conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id", new { id }, tx);
        foreach (var t in k.TagIds.Distinct()) conn.Execute("INSERT INTO transaction_tags (transaction_id, tag_id) VALUES (@id, @t)", new { id, t }, tx);
    }

    // ---- moves by hand ---------------------------------------------------------------------------------------

    /// <summary>
    /// Pairs two lines as one move between our own accounts (docs/ARCHITECTURE.md, POST /moves): one out and one in, the same
    /// amount, on different accounts. A line may already be a waiting leg (a move with no partner yet); a line in a
    /// complete move has to be unpaired first. A paired line has no category, type, splits or tags.
    /// </summary>
    public long Pair(IReadOnlyList<long> ids)
    {
        TypeHandlers.EnsureRegistered();
        if (ids.Distinct().Count() != 2) throw new EditException(EditError.Invalid, "Pick exactly two different lines to pair.");

        using var tx = conn.BeginTransaction();
        var a = Load(ids[0], tx);
        var b = Load(ids[1], tx);
        var (outLeg, inLeg) = a.AmountCents < 0 ? (a, b) : (b, a);
        RequireNotLeftOut(a);
        RequireNotLeftOut(b);

        if (a.AccountId == b.AccountId) throw new EditException(EditError.Unprocessable, "A move goes from one account to another. These two lines are on the same account.");
        if (outLeg.AmountCents >= 0 || inLeg.AmountCents <= 0 || outLeg.AmountCents != -inLeg.AmountCents)
            throw new EditException(EditError.Unprocessable, "A move needs one line going out and one coming in, for the same amount.");
        foreach (var leg in new[] { a, b })
        {
            if (leg.MoveId is { } m && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM transactions WHERE move_id = @m", new { m }, tx) > 1)
                throw new EditException(EditError.Unprocessable, "One of these lines is already part of a move. Unpair it first.");
            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM split_lines WHERE transaction_id = @id", new { id = leg.Id }, tx) > 0)
                throw new EditException(EditError.Unprocessable, "A line that is split cannot be a move. Remove its splits first.");
        }

        var accounts = new AccountRepository(conn, clock, tx);
        var kinds = new[] { outLeg, inLeg }.Select(l => accounts.Get(l.AccountId)!.Kind).ToList();
        var countsAs = Tideline.Core.Moves.MovePairer.CountsAs(kinds[0], kinds[1]);

        // keep the move a waiting leg already has; a second waiting move is dropped
        var keep = outLeg.MoveId ?? inLeg.MoveId;
        var ledger = new LedgerRepository(conn, clock, tx);
        var moveId = keep ?? ledger.CreateMove(countsAs);
        if (keep is not null) conn.Execute("UPDATE moves SET counts_as = @countsAs WHERE id = @moveId", new { countsAs = countsAs.ToDb(), moveId }, tx);

        var before = new[] { outLeg, inLeg }.Select(l => new { l.Id, Snapshot = Snapshot(l.Id, tx), l.MoveId }).ToList();
        var drop = new[] { outLeg.MoveId, inLeg.MoveId }.Where(m => m is not null && m != moveId).Select(m => m!.Value).Distinct().ToList();
        foreach (var leg in new[] { outLeg, inLeg })
        {
            conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id", new { id = leg.Id }, tx);
            conn.Execute("""
                UPDATE transactions SET move_id = @moveId, category_id = NULL, type = NULL, recurring_id = NULL, plan_id = NULL, asset_account_id = NULL, rule_id = NULL,
                    is_manual_override = 1, updated_at = @now
                WHERE id = @id
                """, new { id = leg.Id, moveId, now = clock.GetUtcNow() }, tx);
        }
        foreach (var m in drop) conn.Execute("DELETE FROM moves WHERE id = @m", new { m }, tx);

        new AuditWriter(conn, clock, tx, actor).Write("move.pair", "move", moveId,
            new { legs = before }, new { legs = new[] { outLeg.Id, inLeg.Id }, countsAs = countsAs.ToDb() });
        tx.Commit();
        return moveId;
    }

    /// <summary>
    /// Takes a move apart, or releases a leg that is waiting for its partner. Each line goes back to the rules, which give
    /// it a category and type again. They are not paired again by later imports.
    /// </summary>
    public int Unpair(long moveId)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var count = UnpairIn(moveId, tx);
        tx.Commit();
        return count;
    }

    private int UnpairIn(long moveId, IDbTransaction tx)
    {
        var legs = conn.Query<long>("SELECT id FROM transactions WHERE move_id = @moveId ORDER BY id", new { moveId }, tx).ToList();
        if (legs.Count == 0 && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM moves WHERE id = @moveId", new { moveId }, tx) == 0)
            throw new EditException(EditError.NotFound, $"There is no move {moveId}.");

        var before = new { legs = legs.Select(l => new { Id = l, Snapshot = Snapshot(l, tx) }).ToList() };
        var classifier = LineClassifier.Load(conn, clock, tx);
        foreach (var id in legs)
        {
            var c = Load(id, tx);
            conn.Execute("UPDATE transactions SET move_id = NULL, type = 'extra' WHERE id = @id", new { id }, tx);   // a line needs a type until the rules give it one
            c.MoveId = null;
            RunRules(c, classifier, tx);
        }
        conn.Execute("DELETE FROM moves WHERE id = @moveId", new { moveId }, tx);

        new AuditWriter(conn, clock, tx, actor).Write("move.unpair", "move", moveId, before,
            new { legs = legs.Select(l => new { Id = l, Snapshot = Snapshot(l, tx) }).ToList() });
        return legs.Count;
    }

    // ---- splits ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces every split of a line (docs/ARCHITECTURE.md): same sign as the parent, a category and
    /// a type each, and together no more than the line. What is left over stays with the line's own category and type.
    /// </summary>
    public string ReplaceSplits(long id, IReadOnlyList<SplitInput> splits, string? version)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var c = Load(id, tx);
        RequireNotMove(c);
        RequireNotLeftOut(c);
        CheckVersion(c, version, tx);

        var parsed = new List<(SplitInput Input, LineType Type, IReadOnlyList<long> Tags)>();
        foreach (var s in splits)
        {
            if (s.AmountCents == 0) throw new EditException(EditError.Invalid, "A split cannot be $0.00.");
            if (Math.Sign(s.AmountCents) != Math.Sign(c.AmountCents))
                throw new EditException(EditError.Unprocessable, "Splits go the same way as the line: money out stays money out.");
            RequireCategory(s.CategoryId, tx);
            parsed.Add((s, ParseType(s.Type), TagIds(s.Tags ?? [], tx)));
        }

        var total = splits.Sum(s => Math.Abs(s.AmountCents));
        if (total > Math.Abs(c.AmountCents))
            throw new EditException(EditError.Unprocessable,
                $"The splits add up to {new Money(total)}, which is more than the {new Money(Math.Abs(c.AmountCents))} line.");

        var before = SplitSnapshot(id, tx);
        conn.Execute("DELETE FROM split_line_tags WHERE split_line_id IN (SELECT id FROM split_lines WHERE transaction_id = @id)", new { id }, tx);
        conn.Execute("DELETE FROM split_lines WHERE transaction_id = @id", new { id }, tx);

        var order = 0;
        foreach (var (input, type, tagIds) in parsed)
        {
            conn.Execute("""
                INSERT INTO split_lines (transaction_id, amount_cents, category_id, type, note, sort_order, created_at)
                VALUES (@id, @amount, @category, @type, @note, @order, @now)
                """, new { id, amount = input.AmountCents, category = input.CategoryId, type = type.ToDb(), note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), order = order++, now = clock.GetUtcNow() }, tx);
            var splitId = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
            foreach (var t in tagIds) conn.Execute("INSERT INTO split_line_tags (split_line_id, tag_id) VALUES (@splitId, @t)", new { splitId, t }, tx);
        }

        Touch(id, tx);
        new AuditWriter(conn, clock, tx, actor).Write("transaction.splits", "transaction", id, before, SplitSnapshot(id, tx));
        var newVersion = NewVersion(id, tx);
        tx.Commit();
        return newVersion;
    }

    private object SplitSnapshot(long id, IDbTransaction tx) =>
        conn.Query<(long AmountCents, long CategoryId, string Type, string? Note)>(
            "SELECT amount_cents, category_id, type, note FROM split_lines WHERE transaction_id = @id ORDER BY sort_order, id", new { id }, tx)
            .Select(s => new { s.AmountCents, s.CategoryId, s.Type, s.Note }).ToList();

    // ---- manual entry ----------------------------------------------------------------------------------------

    public long CreateManual(ManualEntry e)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();

        var account = new AccountRepository(conn, clock, tx).Get(e.AccountId)
            ?? throw new EditException(EditError.Invalid, $"There is no account {e.AccountId}.");
        if (account.Kind is AccountKind.Loan or AccountKind.Asset or AccountKind.Retirement)
            throw new EditException(EditError.Invalid, $"{account.Name} is tracked by balance, so it has no lines.");
        if (e.AmountCents == 0) throw new EditException(EditError.Invalid, "The amount cannot be $0.00.");
        if (string.IsNullOrWhiteSpace(e.Description)) throw new EditException(EditError.Invalid, "A description is needed.");
        if (e.Description.Length > 500) throw new EditException(EditError.Invalid, "The description is too long (500 characters at most).");

        LineType? type = e.Type is null ? null : ParseType(e.Type);
        if (e.CategoryId is { } cat)
        {
            RequireCategory(cat, tx);
            type ??= new CategoryRepository(conn, tx).DefaultTypes()[cat];
        }
        if (type is null) throw new EditException(EditError.Invalid, "Pick a category or a type.");

        var tagIds = TagIds(e.Tags ?? [], tx);
        var ledger = new LedgerRepository(conn, clock, tx);
        var id = ledger.Insert(new NewTransaction(e.AccountId, e.PostedOn, e.AmountCents, e.Description.Trim(), "manual:" + Guid.NewGuid().ToString("N"), ImportSource.Manual,
            Payee: string.IsNullOrWhiteSpace(e.Payee) ? null : e.Payee.Trim(), CategoryId: e.CategoryId, Type: type, TagIds: tagIds));
        conn.Execute("UPDATE transactions SET is_manual_override = 1, notes = @notes WHERE id = @id", new { id, notes = string.IsNullOrWhiteSpace(e.Notes) ? null : e.Notes }, tx);

        new AuditWriter(conn, clock, tx, actor).Write("transaction.create", "transaction", id, null,
            new { e.AccountId, e.PostedOn, e.AmountCents, e.Description, e.CategoryId, Type = type.Value.ToDb(), e.Payee, e.Notes });
        tx.Commit();
        return id;
    }

    public void DeleteManual(long id)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var c = Load(id, tx);
        if (c.Source != "manual")
            throw new EditException(EditError.Conflict, "Lines that came from the bank cannot be deleted. Leave them out of the totals instead.");
        if (c.MoveId is not null)
            throw new EditException(EditError.Unprocessable, "This line is part of a transfer between your accounts. Unpair it first, then delete it.");

        var before = new { Snapshot = Snapshot(id, tx), c.AccountId, c.AmountCents, Description = c.OriginalDescription };
        conn.Execute("DELETE FROM split_line_tags WHERE split_line_id IN (SELECT id FROM split_lines WHERE transaction_id = @id)", new { id }, tx);
        conn.Execute("DELETE FROM split_lines WHERE transaction_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM transactions WHERE id = @id", new { id }, tx);

        new AuditWriter(conn, clock, tx, actor).Write("transaction.delete", "transaction", id, before, null);
        tx.Commit();
    }

    // ---- leaving a line out of the totals --------------------------------------------------------------------

    public const int MaxReason = 200;

    private enum LeaveOutOutcome { Done, InTransfer, AlreadyLeftOut, Missing }

    private LeaveOutOutcome LeaveOutLine(long id, string? reason, long? importId, IDbTransaction tx, AuditWriter audit)
    {
        var row = conn.QuerySingleOrDefault<(long? MoveId, string? LeftOutAt)?>("SELECT move_id, left_out_at FROM transactions WHERE id = @id", new { id }, tx);
        if (row is null) return LeaveOutOutcome.Missing;
        if (row.Value.MoveId is not null) return LeaveOutOutcome.InTransfer;
        if (row.Value.LeftOutAt is not null) return LeaveOutOutcome.AlreadyLeftOut;

        conn.Execute("UPDATE transactions SET left_out_at = @now, left_out_reason = @reason, left_out_import_id = @importId, updated_at = @now WHERE id = @id",
            new { id, now = clock.GetUtcNow(), reason, importId }, tx);
        audit.Write("transaction.leave_out", "transaction", id, new { leftOut = false }, new { leftOut = true, reason, importId });
        return LeaveOutOutcome.Done;
    }

    /// <summary>
    /// Takes lines out of every total, count and alert without changing them: the bank fields stay as they were and the line stays
    /// listed (under "left out") so it can be brought back. For a duplicate, or a line that should never have been counted. A line in
    /// a transfer has to be unpaired first. One line asked for and not possible is an error; many are counted as skipped.
    /// </summary>
    public LeaveOutResult LeaveOut(IReadOnlyList<long> ids, string? reason)
    {
        TypeHandlers.EnsureRegistered();
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) throw new EditException(EditError.Invalid, "Pick at least one line.");
        if (distinct.Count > MaxBulk) throw new EditException(EditError.Invalid, $"Leave out {MaxBulk} lines or fewer at a time.");
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is { Length: > MaxReason }) throw new EditException(EditError.Invalid, $"The reason is too long ({MaxReason} characters at most).");

        using var tx = conn.BeginTransaction();
        var audit = new AuditWriter(conn, clock, tx, actor);
        int done = 0, skipped = 0, missing = 0;
        foreach (var id in distinct)
        {
            switch (LeaveOutLine(id, why, null, tx, audit))
            {
                case LeaveOutOutcome.Done: done++; break;
                case LeaveOutOutcome.Missing:
                    if (distinct.Count == 1) throw new EditException(EditError.NotFound, $"There is no transaction {id}.");
                    missing++;
                    break;
                case LeaveOutOutcome.InTransfer:
                    if (distinct.Count == 1) throw new EditException(EditError.Unprocessable, "This line is part of a transfer between your accounts. Unpair it first, then leave it out.");
                    skipped++;
                    break;
                default:
                    if (distinct.Count == 1) throw new EditException(EditError.Conflict, "This line is already left out.");
                    skipped++;
                    break;
            }
        }
        tx.Commit();
        return new LeaveOutResult(done, skipped, missing);
    }

    /// <summary>Counts the lines again. Returns how many were brought back.</summary>
    public int BringBack(IReadOnlyList<long> ids)
    {
        TypeHandlers.EnsureRegistered();
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) throw new EditException(EditError.Invalid, "Pick at least one line.");
        if (distinct.Count > MaxBulk) throw new EditException(EditError.Invalid, $"Bring back {MaxBulk} lines or fewer at a time.");

        using var tx = conn.BeginTransaction();
        var back = BringBackIn(distinct, tx);
        if (back == 0) throw new EditException(EditError.Conflict, distinct.Count == 1 ? "This line is not left out." : "None of those lines are left out.");
        tx.Commit();
        return back;
    }

    private int BringBackIn(IReadOnlyList<long> ids, IDbTransaction tx)
    {
        var audit = new AuditWriter(conn, clock, tx, actor);
        var back = 0;
        foreach (var id in ids)
        {
            var row = conn.QuerySingleOrDefault<(string? Reason, long? ImportId, string? LeftOutAt)?>(
                "SELECT left_out_reason, left_out_import_id, left_out_at FROM transactions WHERE id = @id", new { id }, tx);
            if (row is null || row.Value.LeftOutAt is null) continue;
            conn.Execute("UPDATE transactions SET left_out_at = NULL, left_out_reason = NULL, left_out_import_id = NULL, updated_at = @now WHERE id = @id",
                new { id, now = clock.GetUtcNow() }, tx);
            audit.Write("transaction.bring_back", "transaction", id, new { leftOut = true, reason = row.Value.Reason, importId = row.Value.ImportId }, new { leftOut = false });
            back++;
        }
        return back;
    }

    /// <summary>
    /// Takes back an import that should not have happened (the wrong account, the wrong file, a double upload): every line it added
    /// is left out of the totals, in one step. A line of it that was paired as a transfer is unpaired first (its partner on the other
    /// account stays, back with the rules). Nothing is deleted, so "restore" can put it all back.
    /// </summary>
    public UndoImportResult UndoImport(long batchId)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM import_batches WHERE id = @batchId", new { batchId }, tx) == 0)
            throw new EditException(EditError.NotFound, "There is no such import.");

        var ids = conn.Query<long>("SELECT id FROM transactions WHERE import_batch_id = @batchId AND left_out_at IS NULL ORDER BY id", new { batchId }, tx).ToList();
        if (ids.Count == 0) throw new EditException(EditError.Conflict, "Nothing from that import is counted, so there is nothing to take out.");

        var audit = new AuditWriter(conn, clock, tx, actor);
        var unpaired = 0;
        foreach (var id in ids)
        {
            if (conn.ExecuteScalar<long?>("SELECT move_id FROM transactions WHERE id = @id", new { id }, tx) is { } move)
            {
                UnpairIn(move, tx);
                unpaired++;
            }
            LeaveOutLine(id, "The import it came in with was undone", batchId, tx, audit);
        }
        audit.Write("import.undone", "import_batch", batchId, null, new { leftOut = ids.Count, unpaired });
        tx.Commit();
        return new UndoImportResult(ids.Count, unpaired);
    }

    /// <summary>Counts again the lines an undone import took out. Lines left out one by one stay out.</summary>
    public int RestoreImport(long batchId)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM import_batches WHERE id = @batchId", new { batchId }, tx) == 0)
            throw new EditException(EditError.NotFound, "There is no such import.");

        var ids = conn.Query<long>("SELECT id FROM transactions WHERE left_out_import_id = @batchId ORDER BY id", new { batchId }, tx).ToList();
        if (ids.Count == 0) throw new EditException(EditError.Conflict, "That import has not been undone.");

        var back = BringBackIn(ids, tx);
        new AuditWriter(conn, clock, tx, actor).Write("import.restored", "import_batch", batchId, null, new { restored = back });
        tx.Commit();
        return back;
    }
}
