using System.Data;
using System.Globalization;
using Dapper;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;
using Tideline.Data.Services;
using Tideline.Import.Truist;

namespace Tideline.Import.SimpleFin;

public sealed record RemoteAccount(string Id, string Name, string Organization, long BalanceCents, long? MappedAccountId, string? MappedAccountName, long? SuggestedAccountId);

public sealed record AccountSyncResult(
    long AccountId, string AccountName, int Fetched, int PendingSkipped, int BeforeCutover, int Inserted, int Existing, int ToReview, bool? BalanceMatches, string? Error);

public sealed record SyncReport(IReadOnlyList<AccountSyncResult> Accounts, IReadOnlyList<string> Errors, int Requests);

public sealed record DryRunLine(string PostedOn, string Description, long AmountCents, string? LedgerDescription, string Difference);

public sealed record DryRunReport(
    string AccountName, string From, string To, int SimpleFinRows, int Matched, int SameMeaning,
    IReadOnlyList<DryRunLine> Different, IReadOnlyList<DryRunLine> OnlyInSimpleFin, IReadOnlyList<DryRunLine> OnlyInLedger);

public sealed record ConnectionStatus(bool Connected, DateTimeOffset? LastSyncAt, string? LastError, int RequestsToday);

/// <summary>
/// Pulls transactions from SimpleFIN into the same pipeline the CSV files use (docs/05-import.md, "SimpleFIN"). Windows of at most 90 days overlapping
/// by 5; pending lines wait until they post; a cutover date per account keeps the two doors from covering the same days; more than 24 requests
/// in a day is treated as a bug and stops. The access address is sealed and never logged.
/// </summary>
public sealed class SimpleFinSync(SqliteConnectionFactory factory, ImportPipeline pipeline, SimpleFinClient client, ISecretProtector protector, TimeProvider clock, Func<DateTimeOffset, DateOnly> localDate)
{
    public const int MaxRequestsPerDay = 24;
    public const int LookBackDays = 95;

    private static string RequestsKey(DateTimeOffset now) => $"simplefin_requests:{now.UtcDateTime:yyyy-MM-dd}";

    private sealed class Mapped
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? BankNumber { get; set; }
        public string Kind { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public DateOnly? FromOn { get; set; }
    }

    // ---- the connection ----------------------------------------------------------------------------------------------

    public async Task<long> ConnectAsync(string setupToken, Actor? actor, CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        if (string.IsNullOrWhiteSpace(setupToken)) throw new EditException(EditError.Invalid, "Paste the setup token from SimpleFIN Bridge.");
        string access;
        try { access = await client.ClaimAsync(setupToken, ct); }
        catch (SimpleFinException e) { throw new EditException(EditError.Invalid, e.Message); }

        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("UPDATE simplefin_connections SET is_active = 0 WHERE is_active = 1", transaction: tx);
        conn.Execute("INSERT INTO simplefin_connections (access_url_enc, created_at, is_active) VALUES (@sealedText, @now, 1)", new { sealedText = protector.Protect(access), now = clock.GetUtcNow() }, tx);
        var id = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        new AuditWriter(conn, clock, tx, actor).Write("simplefin.connected", "simplefin", id, null, new { connected = true });
        tx.Commit();
        return id;
    }

    public void Disconnect(Actor? actor)
    {
        TypeHandlers.EnsureRegistered();
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var id = conn.ExecuteScalar<long?>("SELECT id FROM simplefin_connections WHERE is_active = 1", transaction: tx) ?? throw new EditException(EditError.NotFound, "SimpleFIN is not connected.");
        conn.Execute("UPDATE simplefin_connections SET is_active = 0, access_url_enc = @empty WHERE id = @id", new { id, empty = protector.Protect("") }, tx);
        new AuditWriter(conn, clock, tx, actor).Write("simplefin.disconnected", "simplefin", id, new { connected = true }, new { connected = false });
        tx.Commit();
    }

    public ConnectionStatus Status()
    {
        TypeHandlers.EnsureRegistered();
        using var conn = factory.Open();
        var c = conn.QueryFirstOrDefault<(DateTimeOffset? Last, string? Error)?>("SELECT last_sync_at, last_error FROM simplefin_connections WHERE is_active = 1 ORDER BY id DESC");
        var used = int.TryParse(new SettingsRepository(conn, clock).Get(RequestsKey(clock.GetUtcNow())), out var n) ? n : 0;
        return new ConnectionStatus(c is not null, c?.Last, c?.Error, used);
    }

    private (long Id, string Access)? Active(IDbConnection conn)
    {
        var row = conn.QueryFirstOrDefault<(long Id, string Sealed)?>("SELECT id, access_url_enc FROM simplefin_connections WHERE is_active = 1 ORDER BY id DESC");
        return row is { } r ? (r.Id, protector.Unprotect(r.Sealed)) : null;
    }

    private void CountRequests(IDbConnection conn, int n)
    {
        try { CountRequestsOrThrow(conn, n); }
        catch (SimpleFinException e) { throw new EditException(EditError.Unprocessable, e.Message); }
    }

    private void CountRequestsOrThrow(IDbConnection conn, int n)
    {
        var settings = new SettingsRepository(conn, clock);
        var key = RequestsKey(clock.GetUtcNow());
        var used = int.TryParse(settings.Get(key), out var u) ? u : 0;
        if (used + n > MaxRequestsPerDay) throw new SimpleFinException($"Stopped: {used} requests have been made to SimpleFIN today and {MaxRequestsPerDay} is the limit. This should not happen; it is a bug.");
        settings.Set(key, (used + n).ToString(CultureInfo.InvariantCulture));
    }

    // ---- what SimpleFIN has, and which of our accounts it is --------------------------------------------------------------

    public async Task<IReadOnlyList<RemoteAccount>> RemoteAccountsAsync(CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        string access;
        using (var conn = factory.Open())
        {
            access = (Active(conn) ?? throw new EditException(EditError.NotFound, "SimpleFIN is not connected.")).Access;
            CountRequests(conn, 1);
        }
        var response = await Fetch(() => client.GetAccountsAsync(access, null, null, balancesOnly: true, ct));

        using var db = factory.Open();
        var ours = db.Query<(long Id, string Name, string? Bank, string? Remote)>("SELECT id, name, bank_number_hash, simplefin_account_id FROM accounts WHERE is_closed = 0").ToList();
        return response.Accounts.Select(a =>
        {
            var mapped = ours.FirstOrDefault(o => o.Remote == a.Id);
            var suggestion = mapped.Id != 0 ? (long?)null : ours.FirstOrDefault(o => o.Remote is null && (
                (o.Bank is { Length: > 0 } && DigitRuns(a.Name).Any(d => AccountNumbers.Keys.Hash(d) == o.Bank)) ||
                a.Name.Contains(o.Name, StringComparison.OrdinalIgnoreCase))).Id;
            return new RemoteAccount(a.Id, a.Name, a.Organization, a.BalanceCents, mapped.Id != 0 ? mapped.Id : null, mapped.Id != 0 ? mapped.Name : null, suggestion is 0 ? null : suggestion);
        }).ToList();
    }

    /// <summary>Every run of exactly four digits in a name such as "Everyday Checking 1111". Account numbers are only ever compared as keyed hashes.</summary>
    private static IEnumerable<string> DigitRuns(string name) =>
        System.Text.RegularExpressions.Regex.Matches(name, @"(?<!d)d{4}(?!d)").Select(m => m.Value);

    private static async Task<SfResponse> Fetch(Func<Task<SfResponse>> call)
    {
        try { return await call(); }
        catch (SimpleFinException e) { throw new EditException(EditError.Unprocessable, e.Message); }
    }

    // ---- syncing --------------------------------------------------------------------------------------------------------

    private List<Mapped> MappedAccounts(IDbConnection conn) => conn.Query<Mapped>("""
        SELECT id AS Id, name AS Name, bank_number_hash AS BankNumber, kind AS Kind, simplefin_account_id AS RemoteId, simplefin_from_on AS FromOn
        FROM accounts WHERE simplefin_account_id IS NOT NULL AND is_closed = 0
        """).ToList();

    private static TruistRow ToRow(SfTransaction t, string bankNumber, DateOnly postedOn, string connectionId, string remoteId) =>
        new(bankNumber, postedOn, t.Description, t.AmountCents, "", "", null, "", "simplefin", 0, t.RawJson, ExternalIds.SimpleFin(connectionId, remoteId, t.Id));

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        var now = clock.GetUtcNow();
        long connectionId;
        string access;
        List<Mapped> mapped;
        var windows = SimpleFinClient.Windows(now.AddDays(-LookBackDays), now);
        using (var conn = factory.Open())
        {
            var active = Active(conn) ?? throw new EditException(EditError.NotFound, "SimpleFIN is not connected.");
            (connectionId, access) = active;
            mapped = MappedAccounts(conn);
            if (mapped.Count == 0) throw new EditException(EditError.Unprocessable, "No account is matched to a SimpleFIN account yet. Match them in Settings, Imports.");
            CountRequests(conn, windows.Count);
        }

        var errors = new List<string>();
        var byAccount = new Dictionary<string, Dictionary<string, SfTransaction>>();
        var balances = new Dictionary<string, (long Cents, DateTimeOffset? On)>();
        try
        {
            foreach (var (start, end) in windows)
            {
                var response = await client.GetAccountsAsync(access, start, end, balancesOnly: false, ct);
                errors.AddRange(response.Errors);
                foreach (var a in response.Accounts)
                {
                    if (!byAccount.TryGetValue(a.Id, out var set)) byAccount[a.Id] = set = [];
                    foreach (var t in a.Transactions) set[t.Id] = t;
                    balances[a.Id] = (a.BalanceCents, a.BalanceDate);
                }
            }
        }
        catch (SimpleFinException e)
        {
            Record(connectionId, e.Message, errors);
            throw new EditException(EditError.Unprocessable, e.Message);
        }

        var results = new List<AccountSyncResult>();
        foreach (var account in mapped)
        {
            if (!byAccount.TryGetValue(account.RemoteId, out var txs))
            {
                results.Add(new AccountSyncResult(account.Id, account.Name, 0, 0, 0, 0, 0, 0, null, "SimpleFIN did not send this account. Check that it is still linked at SimpleFIN."));
                continue;
            }
            results.Add(await ImportAccountAsync(connectionId, account, txs.Values, balances.GetValueOrDefault(account.RemoteId), ct));
        }

        errors = errors.Distinct().ToList();
        Record(connectionId, null, errors);
        return new SyncReport(results, errors, windows.Count);
    }

    private void Record(long connectionId, string? failure, IReadOnlyList<string> errlist)
    {
        using var conn = factory.Open();
        var message = failure ?? (errlist.Count > 0 ? string.Join(" ", errlist) : null);
        if (message is { Length: > 400 }) message = message[..400];
        conn.Execute("UPDATE simplefin_connections SET last_sync_at = @now, last_error = @message WHERE id = @id", new { id = connectionId, now = clock.GetUtcNow(), message });
        var events = new SystemEventRepository(conn, clock);
        if (failure is not null) events.Add(EventLevel.Error, "simplefin", failure);
        foreach (var e in errlist) events.Add(EventLevel.Warn, "simplefin", e);
        if (failure is null) events.Add(EventLevel.Info, "simplefin", "Synced with SimpleFIN.");
    }

    private async Task<AccountSyncResult> ImportAccountAsync(long connectionId, Mapped account, IEnumerable<SfTransaction> all, (long Cents, DateTimeOffset? On) balance, CancellationToken ct)
    {
        int pending = 0, before = 0;
        var rows = new List<TruistRow>();
        foreach (var t in all.OrderBy(t => t.Posted).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            if (t.Pending) { pending++; continue; }
            var day = localDate(t.Posted);
            if (account.FromOn is { } from && day < from) { before++; continue; }
            rows.Add(ToRow(t, account.BankNumber ?? "", day, connectionId.ToString(CultureInfo.InvariantCulture), account.RemoteId));
        }

        (DateOnly On, long Cents)? bank = balance.On is { } on ? (localDate(on), balance.Cents) : null;

        HashSet<string> known;
        using (var conn = factory.Open()) known = new LedgerRepository(conn, clock).ExistingExternalIds(rows.Select(r => r.ExternalId));
        var fresh = rows.Count(r => !known.Contains(r.ExternalId));

        if (fresh == 0)
        {
            // nothing new: still keep the bank's balance and check the ledger against it
            bool? matches = null;
            if (bank is { } b)
            {
                using var conn = factory.Open();
                new BalanceSnapshotRepository(conn, clock).UpsertBank(account.Id, b.On, b.Cents);
                matches = new LedgerRepository(conn, clock).BalanceAsOf(account.Id, b.On) == b.Cents;
            }
            return new AccountSyncResult(account.Id, account.Name, rows.Count, pending, before, 0, rows.Count, 0, matches, null);
        }

        var result = await pipeline.ImportBankRowsAsync(ImportSource.Simplefin, $"SimpleFIN: {account.Name}", account.Id, rows, bank, ct);
        return result.Succeeded
            ? new AccountSyncResult(account.Id, account.Name, rows.Count, pending, before, result.RowsInserted, result.RowsExisting, result.RowsReview, result.Reconciliation?.Matches, null)
            : new AccountSyncResult(account.Id, account.Name, rows.Count, pending, before, 0, 0, 0, null, result.Error);
    }

    // ---- the dry run ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fetches a stretch of one account from SimpleFIN and sets it beside what the CSV files already put in the ledger for the same days, running
    /// SimpleFIN's descriptions through the rules to show where they would land differently. Nothing is written.
    /// </summary>
    public async Task<DryRunReport> DryRunAsync(long accountId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        if (to < from) throw new EditException(EditError.Invalid, "The end of the period is before its start.");
        if (to.DayNumber - from.DayNumber > 90) throw new EditException(EditError.Invalid, "A dry run covers 90 days at most.");

        string access;
        Mapped account;
        using (var conn = factory.Open())
        {
            access = (Active(conn) ?? throw new EditException(EditError.NotFound, "SimpleFIN is not connected.")).Access;
            account = MappedAccounts(conn).FirstOrDefault(m => m.Id == accountId) ?? throw new EditException(EditError.Invalid, "That account is not matched to a SimpleFIN account yet.");
            CountRequests(conn, 1);
        }

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);
        var response = await Fetch(() => client.GetAccountsAsync(access, start, end, balancesOnly: false, ct));
        var theirs = (response.Accounts.FirstOrDefault(a => a.Id == account.RemoteId)?.Transactions ?? [])
            .Where(t => !t.Pending).Select(t => (Tx: t, Day: localDate(t.Posted))).Where(x => x.Day >= from && x.Day <= to)
            .OrderBy(x => x.Day).ThenBy(x => x.Tx.Id, StringComparer.Ordinal).ToList();

        using var db = factory.Open();
        var classifier = LineClassifier.Load(db, clock);
        var kind = TypeHandlers.FromDb<AccountKind>(account.Kind);
        var ledgerRows = db.Query<(long Id, DateOnly On, long Cents, string Description, long? Category, string Type, string? Payee, bool Override)>("""
            SELECT id, posted_on, amount_cents, original_description, category_id, COALESCE(type, ''), payee, is_manual_override OR move_id IS NOT NULL FROM transactions
            WHERE account_id = @accountId AND posted_on BETWEEN @from AND @to AND source <> 'manual' ORDER BY posted_on, id
            """, new { accountId, from, to }).ToList();
        var categories = db.Query<(long Id, string Name)>("SELECT id, name FROM categories").ToDictionary(c => c.Id, c => c.Name);
        string CategoryName(long? id) => id is { } v && categories.TryGetValue(v, out var n) ? n : "no category";

        var used = new HashSet<long>();
        var different = new List<DryRunLine>();
        var onlyTheirs = new List<DryRunLine>();
        int matched = 0, same = 0;
        foreach (var (tx, day) in theirs)
        {
            var partner = ledgerRows.FirstOrDefault(l => !used.Contains(l.Id) && l.On == day && l.Cents == tx.AmountCents);
            if (partner.Id == 0)
            {
                onlyTheirs.Add(new DryRunLine(day.ToString("yyyy-MM-dd"), tx.Description, tx.AmountCents, null, "Not in the ledger yet."));
                continue;
            }
            used.Add(partner.Id);
            matched++;

            var c = classifier.Classify(new RuleLine(tx.Description, tx.AmountCents, account.BankNumber ?? "", kind), "");
            var diffs = new List<string>();
            if (c.CategoryId != partner.Category) diffs.Add($"category {CategoryName(partner.Category)} becomes {CategoryName(c.CategoryId)}");
            if (c.Type.ToDb() != partner.Type) diffs.Add($"type {partner.Type} becomes {c.Type.ToDb()}");
            if (!string.Equals(c.Payee ?? "", partner.Payee ?? "", StringComparison.OrdinalIgnoreCase)) diffs.Add($"payee \"{partner.Payee}\" becomes \"{c.Payee}\"");

            if (diffs.Count == 0 || partner.Override) same++;
            else different.Add(new DryRunLine(day.ToString("yyyy-MM-dd"), tx.Description, tx.AmountCents, partner.Description, string.Join("; ", diffs)));
        }

        var onlyOurs = ledgerRows.Where(l => !used.Contains(l.Id))
            .Select(l => new DryRunLine(l.On.ToString("yyyy-MM-dd"), l.Description, l.Cents, l.Description, "SimpleFIN did not send this one.")).ToList();

        return new DryRunReport(account.Name, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), theirs.Count, matched, same, different.Take(200).ToList(), onlyTheirs.Take(200).ToList(), onlyOurs.Take(200).ToList());
    }
}
