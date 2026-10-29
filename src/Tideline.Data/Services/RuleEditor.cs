using System.Data;
using System.Text.Json;
using Dapper;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data.Repositories;
using Tideline.Data.Security;
using Rule = Tideline.Core.Rules.Rule;

namespace Tideline.Data.Services;

public sealed record RuleConditionInput(string? Field, string? Op, JsonElement Value);

/// <summary>The whole definition of a rule. Saving replaces what was there, conditions and tags included.</summary>
public sealed record RuleInput(
    string? Name,
    int? Priority,
    bool? IsActive,
    long? SetCategoryId,
    string? SetType,
    string? SetPayee,
    long? SetRecurringId,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<RuleConditionInput>? Conditions);

public sealed record RuleView(
    long Id, string Name, int Priority, bool IsActive, long? SetCategoryId, string? SetCategoryName, string? SetType, string? SetPayee,
    long? SetRecurringId, IReadOnlyList<string> Tags, IReadOnlyList<RuleConditionView> Conditions, long LineCount);

public sealed record RuleConditionView(string Field, string Op, JsonElement Value);

public sealed record Meaning(long? CategoryId, string? CategoryName, string Type, string? Payee, long? RuleId, string? RuleName, IReadOnlyList<string> Tags);

public sealed record RuleChange(long TransactionId, string PostedOn, string Description, long AmountCents, string AccountName, Meaning Before, Meaning After);

public sealed record RuleTestResult(int LinesChecked, int WouldChange, IReadOnlyList<RuleChange> Changes);

/// <summary>
/// Rules and what they do to past lines (docs/ARCHITECTURE.md): create, change, reorder, delete, try a draft over a period before
/// saving it, and apply to past lines. Lines you changed by hand are never touched.
/// </summary>
public sealed class RuleEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null)
{
    public const int SampleLimit = 200;

    private static EditException Bad(string message) => new(EditError.Invalid, message);

    private T InTransaction<T>(Func<IDbTransaction, AuditWriter, T> work)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        try
        {
            var result = work(tx, new AuditWriter(conn, clock, tx, actor));
            tx.Commit();
            return result;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            throw new EditException(EditError.Conflict, "Another rule already has that name.");
        }
    }

    // ---- reading -------------------------------------------------------------------------------------------------

    private sealed class RuleRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int Priority { get; set; }
        public bool IsActive { get; set; }
        public long? SetCategoryId { get; set; }
        public string? SetCategoryName { get; set; }
        public string? SetType { get; set; }
        public string? SetPayee { get; set; }
        public long? SetRecurringId { get; set; }
        public long LineCount { get; set; }
    }

    /// <param name="display">True for a person to read: an account named in a rule shows as ••••1111. False for the audit log and tests, where it stays the hash.</param>
    public IReadOnlyList<RuleView> List(IDbTransaction? tx = null, bool display = true)
    {
        TypeHandlers.EnsureRegistered();
        var rules = conn.Query<RuleRow>("""
            SELECT r.id AS Id, r.name AS Name, r.priority AS Priority, r.is_active AS IsActive, r.set_category_id AS SetCategoryId, c.name AS SetCategoryName,
                   r.set_type AS SetType, r.set_payee AS SetPayee, r.set_recurring_id AS SetRecurringId,
                   (SELECT COUNT(*) FROM transactions t WHERE t.rule_id = r.id) AS LineCount
            FROM rules r LEFT JOIN categories c ON c.id = r.set_category_id
            ORDER BY r.priority, r.id
            """, transaction: tx).ToList();
        var conditions = conn.Query<(long RuleId, string Field, string Op, string Value)>("SELECT rule_id, field, op, value FROM rule_conditions ORDER BY id", transaction: tx)
            .ToLookup(c => c.RuleId);
        var tags = conn.Query<(long RuleId, string Name)>("SELECT rt.rule_id, g.name FROM rule_tags rt JOIN tags g ON g.id = rt.tag_id ORDER BY g.name", transaction: tx)
            .ToLookup(t => t.RuleId);

        return rules.Select(r => new RuleView(r.Id, r.Name, r.Priority, r.IsActive, r.SetCategoryId, r.SetCategoryName, r.SetType, r.SetPayee, r.SetRecurringId,
            tags[r.Id].Select(t => t.Name).ToList(),
            conditions[r.Id].Select(c => new RuleConditionView(c.Field, c.Op, display && c.Field == "account" ? AccountLabel(c.Value, tx) : JsonDocument.Parse(c.Value).RootElement.Clone())).ToList(),
            r.LineCount)).ToList();
    }

    /// <summary>An account in a rule is kept as a hash; people see it as ••••1111, found through the accounts that carry the same hash.</summary>
    private JsonElement AccountLabel(string storedJson, IDbTransaction? tx)
    {
        var hash = JsonDocument.Parse(storedJson).RootElement.GetString() ?? "";
        var enc = conn.ExecuteScalar<string?>("SELECT bank_number_enc FROM accounts WHERE bank_number_hash = @hash LIMIT 1", new { hash }, tx);
        return JsonSerializer.SerializeToElement(AccountNumbers.MaskFrom(enc) ?? "(an account that is not here)");
    }

    // ---- writing -------------------------------------------------------------------------------------------------

    private sealed record Checked(string Name, int Priority, bool IsActive, long? CategoryId, LineType? Type, string? Payee, long? RecurringId,
        IReadOnlyList<(string Field, string Op, string Json)> Conditions, IReadOnlyList<string> Tags);

    private Checked Check(RuleInput r, IDbTransaction tx, long? existingId)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length == 0) throw Bad("Give the rule a name.");
        if (name.Length > 150) throw Bad("The rule name is too long (150 characters at most).");

        if (r.SetCategoryId is { } c && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM categories WHERE id = @c", new { c }, tx) == 0) throw Bad("That category does not exist.");
        if (r.SetRecurringId is { } rec && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM recurring WHERE id = @rec", new { rec }, tx) == 0) throw Bad("That recurring item does not exist.");

        LineType? type = null;
        if (!string.IsNullOrWhiteSpace(r.SetType))
        {
            try { type = TypeHandlers.FromDb<LineType>(r.SetType.Trim().ToLowerInvariant()); }
            catch (InvalidDataException) { throw Bad($"'{r.SetType}' is not a type. Use income, bill, required, extra, one_time or savings."); }
        }
        if (r.SetCategoryId is null && type is null) throw Bad("A rule has to set a category or a type, or it does nothing.");
        if (r.SetPayee is { Length: > 100 }) throw Bad("The payee is too long (100 characters at most).");

        var conditions = new List<(string, string, string)>();
        foreach (var c2 in r.Conditions ?? [])
        {
            var raw = c2.Value.GetRawText();
            if (c2.Field == "account")
            {
                // an account is named by its last 4 digits (or what the page shows, ••••1111); it is kept as a keyed hash
                var hash = AccountNumbers.RuleValue(c2.Value.ValueKind == JsonValueKind.String ? c2.Value.GetString() : c2.Value.GetRawText());
                raw = hash is null ? throw Bad("Name an account in a rule by its last 4 digits.") : JsonSerializer.Serialize(hash);
            }
            try { RuleCondition.From(c2.Field ?? "", c2.Op ?? "", raw); }
            catch (Exception e) when (e is ArgumentException or JsonException or FormatException or InvalidOperationException)
            {
                throw Bad($"The condition '{c2.Field} {c2.Op}' is not valid: {e.Message}");
            }
            conditions.Add((c2.Field!, c2.Op!, raw));
        }

        var tags = (r.Tags ?? []).Select(t => string.Join('-', t.Trim().ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))).Where(t => t.Length > 0).Distinct().ToList();
        if (tags.Any(t => t.Length > 50)) throw Bad("A tag name is 50 characters at most.");

        // a new rule goes after the other rules a person made but before the starter set, so what someone teaches always wins over it
        var priority = r.Priority ?? ((conn.ExecuteScalar<int?>("SELECT MAX(priority) FROM rules WHERE name NOT LIKE @starter", new { starter = StarterPack.RuleNamePrefix + "%" }, tx) ?? 0) + 10);
        if (priority < 0) throw Bad("The order number cannot be negative.");
        _ = existingId;
        return new Checked(name, priority, r.IsActive ?? true, r.SetCategoryId, type, string.IsNullOrWhiteSpace(r.SetPayee) ? null : r.SetPayee.Trim(), r.SetRecurringId, conditions, tags);
    }

    private object? Snapshot(long id, IDbTransaction tx) => List(tx, display: false).FirstOrDefault(r => r.Id == id) is { } v
        ? new { v.Name, v.Priority, v.IsActive, v.SetCategoryId, v.SetType, v.SetPayee, v.SetRecurringId, v.Tags, conditions = v.Conditions.Select(c => new { c.Field, c.Op, value = c.Value.GetRawText() }) }
        : null;

    private void WriteParts(long id, Checked c, IDbTransaction tx)
    {
        conn.Execute("DELETE FROM rule_conditions WHERE rule_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM rule_tags WHERE rule_id = @id", new { id }, tx);
        foreach (var (field, op, json) in c.Conditions)
            conn.Execute("INSERT INTO rule_conditions (rule_id, field, op, value) VALUES (@id, @field, @op, @json)", new { id, field, op, json }, tx);
        var tags = new TagRepository(conn, tx);
        foreach (var tagId in c.Tags.Select(tags.GetOrCreate).Distinct())
            conn.Execute("INSERT INTO rule_tags (rule_id, tag_id) VALUES (@id, @tagId)", new { id, tagId }, tx);
    }

    public long Create(RuleInput r) => InTransaction((tx, audit) =>
    {
        var c = Check(r, tx, null);
        conn.Execute("""
            INSERT INTO rules (name, priority, is_active, set_category_id, set_type, set_payee, set_recurring_id, created_at, updated_at)
            VALUES (@Name, @Priority, @active, @CategoryId, @type, @Payee, @RecurringId, @now, @now)
            """, new { c.Name, c.Priority, active = c.IsActive ? 1 : 0, c.CategoryId, type = c.Type?.ToDb(), c.Payee, c.RecurringId, now = clock.GetUtcNow() }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        WriteParts(id, c, tx);
        audit.Write("rule.created", "rule", id, null, Snapshot(id, tx));
        return id;
    });

    public void Update(long id, RuleInput r) => InTransaction<int>((tx, audit) =>
    {
        var before = Snapshot(id, tx) ?? throw new EditException(EditError.NotFound, "There is no such rule.");
        var c = Check(r, tx, id);
        conn.Execute("""
            UPDATE rules SET name = @Name, priority = @Priority, is_active = @active, set_category_id = @CategoryId, set_type = @type, set_payee = @Payee,
                set_recurring_id = @RecurringId, updated_at = @now
            WHERE id = @id
            """, new { id, c.Name, c.Priority, active = c.IsActive ? 1 : 0, c.CategoryId, type = c.Type?.ToDb(), c.Payee, c.RecurringId, now = clock.GetUtcNow() }, tx);
        WriteParts(id, c, tx);
        audit.Write("rule.updated", "rule", id, before, Snapshot(id, tx));
        return 0;
    });

    /// <summary>Lines keep the rule id they were set by in their history; here it is cleared so nothing points at a rule that is gone.</summary>
    public void Delete(long id) => InTransaction<int>((tx, audit) =>
    {
        var before = Snapshot(id, tx) ?? throw new EditException(EditError.NotFound, "There is no such rule.");
        conn.Execute("UPDATE transactions SET rule_id = NULL WHERE rule_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM rule_conditions WHERE rule_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM rule_tags WHERE rule_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM rules WHERE id = @id", new { id }, tx);
        audit.Write("rule.deleted", "rule", id, before, null);
        return 0;
    });

    /// <summary>The order the rules are tried in: first in the list is tried first. Priorities are renumbered in steps of 10.</summary>
    public void Reorder(IReadOnlyList<long> ids) => InTransaction<int>((tx, audit) =>
    {
        var existing = conn.Query<long>("SELECT id FROM rules", transaction: tx).ToHashSet();
        if (ids.Distinct().Count() != ids.Count || !existing.SetEquals(ids)) throw Bad("Send every rule once, in the order you want them tried.");

        var before = conn.Query<(long Id, int Priority)>("SELECT id, priority FROM rules ORDER BY priority, id", transaction: tx).Select(r => new { r.Id, r.Priority }).ToList();
        for (var i = 0; i < ids.Count; i++)
            conn.Execute("UPDATE rules SET priority = @p, updated_at = @now WHERE id = @id", new { id = ids[i], p = (i + 1) * 10, now = clock.GetUtcNow() }, tx);
        audit.Write("rule.reordered", "rule", (string?)null, before, ids.Select((id, i) => new { Id = id, Priority = (i + 1) * 10 }));
        return 0;
    });

    // ---- trying a rule and applying it to the past ------------------------------------------------------------------

    private sealed class LineRow
    {
        public long Id { get; set; }
        public DateOnly PostedOn { get; set; }
        public string Description { get; set; } = "";
        public long AmountCents { get; set; }
        public string AccountName { get; set; } = "";
        public string? BankNumber { get; set; }
        public string AccountKind { get; set; } = "";
        public string? RawJson { get; set; }
        public long? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? Type { get; set; }
        public string? Payee { get; set; }
        public long? RuleId { get; set; }
        public long? RecurringId { get; set; }
        public long? PlanId { get; set; }
    }

    private List<LineRow> Lines(DateOnly from, DateOnly to, IDbTransaction? tx) => conn.Query<LineRow>("""
        SELECT t.id AS Id, t.posted_on AS PostedOn, t.original_description AS Description, t.amount_cents AS AmountCents, a.name AS AccountName,
               a.bank_number_hash AS BankNumber, a.kind AS AccountKind, t.raw_json AS RawJson, t.category_id AS CategoryId, c.name AS CategoryName,
               t.type AS Type, t.payee AS Payee, t.rule_id AS RuleId, t.recurring_id AS RecurringId, t.plan_id AS PlanId
        FROM transactions t JOIN accounts a ON a.id = t.account_id LEFT JOIN categories c ON c.id = t.category_id
        WHERE t.move_id IS NULL AND t.left_out_at IS NULL AND t.is_manual_override = 0 AND t.posted_on BETWEEN @from AND @to
        ORDER BY t.posted_on, t.id
        """, new { from, to }, tx).ToList();

    private static string Merchant(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(raw)?.GetValueOrDefault("Merchant name") ?? ""; }
        catch (JsonException) { return ""; }
    }

    private Rule ToRule(long id, Checked c) => new(id, c.Name, c.Priority, c.IsActive,
        c.Conditions.Select(x => RuleCondition.From(x.Field, x.Op, x.Json)).ToList(), c.CategoryId, c.Type, c.Payee, c.RecurringId, null, []);

    /// <summary>Which lines in the period would change if this rule were saved as written. Nothing is saved.</summary>
    public RuleTestResult Test(RuleInput draft, long? existingId, DateOnly from, DateOnly to)
    {
        TypeHandlers.EnsureRegistered();
        if (to < from) throw Bad("The end of the period is before its start.");
        using var tx = conn.BeginTransaction(); // read only: rolled back at the end
        var c = Check(draft, tx, existingId);
        var tagIds = new TagRepository(conn, tx);

        var current = new RuleRepository(conn, clock, tx).All();
        var categories = new CategoryRepository(conn, tx).DefaultTypes();
        var uncategorized = conn.ExecuteScalar<long?>("SELECT id FROM categories WHERE name = @n", new { n = LineClassifier.UncategorizedName }, tx) ?? 0;
        var reviewTag = conn.ExecuteScalar<long?>("SELECT id FROM tags WHERE name = @n", new { n = LineClassifier.ReviewTagName }, tx) ?? 0;

        // the draft stands in for the saved version of itself, or is added if it is new
        var draftId = existingId ?? -1;
        var draftRule = ToRule(draftId, c) with { TagIds = c.Tags.Select(t => conn.ExecuteScalar<long?>("SELECT id FROM tags WHERE name = @t", new { t }, tx) ?? -Math.Abs(t.GetHashCode())).ToList() };
        var withDraft = current.Where(r => r.Id != draftId).Append(draftRule).ToList();

        var before = new LineClassifier(current, categories, uncategorized, reviewTag);
        var after = new LineClassifier(withDraft, categories, uncategorized, reviewTag);
        var names = conn.Query<(long Id, string Name)>("SELECT id, name FROM categories", transaction: tx).ToDictionary(x => x.Id, x => x.Name);
        var ruleNames = current.ToDictionary(r => r.Id, r => r.Name);
        ruleNames[draftId] = c.Name;
        var tagNames = conn.Query<(long Id, string Name)>("SELECT id, name FROM tags", transaction: tx).ToDictionary(x => x.Id, x => x.Name);
        string TagName(long id) => tagNames.TryGetValue(id, out var n) ? n : c.Tags.FirstOrDefault(t => -Math.Abs(t.GetHashCode()) == id) ?? "new tag";

        Meaning Describe(Classification k) => new(k.CategoryId, k.CategoryId is { } cid ? names.GetValueOrDefault(cid) : null, k.Type.ToDb(), k.Payee, k.RuleId,
            k.RuleId is { } rid ? ruleNames.GetValueOrDefault(rid) : null, k.TagIds.Select(TagName).OrderBy(n => n).ToList());

        var lines = Lines(from, to, tx);
        var changes = new List<RuleChange>();
        var count = 0;
        foreach (var l in lines)
        {
            var kind = TypeHandlers.FromDb<AccountKind>(l.AccountKind);
            var rl = new RuleLine(l.Description, l.AmountCents, l.BankNumber ?? "", kind);
            var b = before.Classify(rl, Merchant(l.RawJson));
            var a = after.Classify(rl, Merchant(l.RawJson));
            if (Same(b, a)) continue;
            count++;
            if (changes.Count < SampleLimit)
                changes.Add(new RuleChange(l.Id, l.PostedOn.ToString("yyyy-MM-dd"), l.Description, l.AmountCents, l.AccountName, Describe(b), Describe(a)));
        }

        tx.Rollback();
        _ = tagIds;
        return new RuleTestResult(lines.Count, count, changes);
    }

    private static bool Same(Classification a, Classification b) =>
        a.CategoryId == b.CategoryId && a.Type == b.Type && a.Payee == b.Payee && a.RecurringId == b.RecurringId && a.PlanId == b.PlanId && a.RuleId == b.RuleId
        && a.TagIds.Order().SequenceEqual(b.TagIds.Order());

    /// <summary>
    /// Lines in the period that the rule now wins, and that were decided some other way, are changed to what the rule says. Lines you set
    /// by hand are skipped. One audit entry per changed line, all carrying the same apply id.
    /// </summary>
    public int ApplyToPast(long ruleId, DateOnly from, DateOnly to) => InTransaction((tx, audit) =>
    {
        if (to < from) throw Bad("The end of the period is before its start.");
        if (!conn.QuerySingle<bool>("SELECT COUNT(*) > 0 FROM rules WHERE id = @ruleId", new { ruleId }, tx)) throw new EditException(EditError.NotFound, "There is no such rule.");

        var classifier = LineClassifier.Load(conn, clock, tx);
        var applyId = Guid.NewGuid().ToString("N");
        var changed = 0;

        foreach (var l in Lines(from, to, tx))
        {
            var kind = TypeHandlers.FromDb<AccountKind>(l.AccountKind);
            var k = classifier.Classify(new RuleLine(l.Description, l.AmountCents, l.BankNumber ?? "", kind), Merchant(l.RawJson));
            if (k.RuleId != ruleId || l.RuleId == ruleId && l.CategoryId == k.CategoryId && l.Type == k.Type.ToDb()) continue;

            var before = new { l.CategoryId, l.Type, l.Payee, l.RuleId };
            conn.Execute("UPDATE transactions SET category_id = @cat, type = @type, payee = @payee, recurring_id = @rec, plan_id = @plan, rule_id = @rule, updated_at = @now WHERE id = @id",
                new { id = l.Id, cat = k.CategoryId, type = k.Type.ToDb(), payee = k.Payee, rec = k.RecurringId, plan = k.PlanId, rule = k.RuleId, now = clock.GetUtcNow() }, tx);
            conn.Execute("DELETE FROM transaction_tags WHERE transaction_id = @id", new { id = l.Id }, tx);
            foreach (var tagId in k.TagIds.Distinct())
                conn.Execute("INSERT INTO transaction_tags (transaction_id, tag_id) VALUES (@id, @tagId)", new { id = l.Id, tagId }, tx);
            audit.Write("rule.apply", "transaction", l.Id, before, new { k.CategoryId, Type = k.Type.ToDb(), k.Payee, appliedRuleId = ruleId, applyId });
            changed++;
        }
        if (changed > 0) new FlagRepository(conn, clock, tx).ResolveAnsweredChecks();
        return changed;
    });
}
