using System.Data;
using System.Security.Cryptography;
using System.Text;
using Tideline.Core;
using Tideline.Core.Moves;
using Tideline.Core.Rules;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Services;
using Tideline.Import.Truist;

namespace Tideline.Import;

public enum ImportStatus { Done, Failed }

/// <summary>Ledger vs bank after an import: the bank's last daily balance in the file against what the ledger adds up to.</summary>
public sealed record Reconciliation(long AccountId, DateOnly AsOf, long BankCents, long LedgerCents)
{
    public bool Matches => BankCents == LedgerCents;
}

public sealed record ImportResult(
    string FileName,
    ImportStatus Status,
    int RowsRead,
    int RowsInserted,
    int RowsExisting,
    int RowsReview,
    int MovesPaired,
    int MovesPending,
    string? Error = null,
    long? BatchId = null,
    Reconciliation? Reconciliation = null)
{
    public bool Succeeded => Status == ImportStatus.Done;
}

/// <summary>
/// One door in, one pipeline (docs/05-import.md): parse, file check, drop rows already seen, cutover check,
/// pair moves, rules, write, bank balance snapshot and reconcile. One database transaction per file.
/// </summary>
public sealed class ImportPipeline(SqliteConnectionFactory factory, WriteGate gate, TimeProvider clock)
{
    private const string UncategorizedName = "Uncategorized";
    private const string ReviewTagName = "review";

    public async Task<ImportResult> ImportTruistAsync(string fileName, string csvText, CancellationToken ct = default)
    {
        var name = Path.GetFileName(fileName);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csvText)));

        TruistFile file;
        try
        {
            file = TruistParser.Parse(name, csvText);
        }
        catch (ImportFileException e)
        {
            return await RecordFailureAsync(name, sha, null, 0, e.Message, ct);
        }

        if (file.Rows.Count == 0)
            return await RecordFailureAsync(name, sha, null, 0, $"{name} has no rows.", ct);

        using var _ = await gate.EnterAsync(ct);
        using var conn = factory.Open();

        var accounts = new AccountRepository(conn, clock);
        var matches = accounts.ByBankNumber(file.BankNumber);
        if (matches.Count != 1)
        {
            var why = matches.Count == 0
                ? $"No account has the bank number {file.BankNumber}. Add it in Settings first."
                : $"More than one account has the bank number {file.BankNumber}.";
            return RecordFailure(conn, name, sha, null, file.Rows.Count, why);
        }

        var account = matches[0];
        var expectsCard = file.Format == TruistFormat.Card;
        if (expectsCard != (account.Kind == AccountKind.CreditCard) || account.Kind is AccountKind.Loan or AccountKind.Asset or AccountKind.Retirement)
            return RecordFailure(conn, name, sha, account.Id, file.Rows.Count,
                $"{name} is a {(expectsCard ? "credit card" : "deposit account")} export but account '{account.Name}' is a {account.Kind.ToDb().Replace('_', ' ')}.");

        var check = FileCheck.Run(file.Rows);
        if (check is { Passed: false })
            return RecordFailure(conn, name, sha, account.Id, file.Rows.Count, check.FailureMessage);

        using var tx = conn.BeginTransaction();
        try
        {
            var result = Run(conn, tx, file, account, check, name, sha);
            if (result.Succeeded)
            {
                tx.Commit();
                return result;
            }
            tx.Rollback();
            return RecordFailure(conn, name, sha, account.Id, result.RowsRead, result.Error!);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private ImportResult Run(IDbConnection conn, IDbTransaction tx, TruistFile file, Account account, FileCheckResult? check, string name, string sha)
    {
        var accounts = new AccountRepository(conn, clock, tx);
        var categories = new CategoryRepository(conn, tx);
        var tags = new TagRepository(conn, tx);
        var ledger = new LedgerRepository(conn, clock, tx);
        var batches = new ImportBatchRepository(conn, clock, tx);

        var batchId = batches.Start(ImportSource.Csv, name, sha, account.Id);

        // A failure rolls everything back (including this batch row); the caller then records the failure on its own.
        ImportResult Fail(string why) => new(name, ImportStatus.Failed, file.Rows.Count, 0, 0, 0, 0, 0, why);

        // 1. drop rows already in the ledger
        var existing = ledger.ExistingExternalIds(file.Rows.Select(r => r.ExternalId));
        var fresh = file.Rows.Where(r => !existing.Contains(r.ExternalId)).ToList();

        // 2. cutover: CSV rows after csv_until_on belong to SimpleFIN
        if (account.CsvUntilOn is { } until && fresh.FirstOrDefault(r => r.PostedOn > until) is not null)
        {
            var late = fresh.Count(r => r.PostedOn > until);
            return Fail($"{late} row(s) are dated after the CSV cutover on {until:yyyy-MM-dd}; SimpleFIN covers those days.");
        }

        // 3. first deposit file for an account with no opening balance: the file implies it
        if (check is not null && account.OpeningBalanceCents is null && ledger.ForAccount(account.Id).Count == 0 && file.Rows.Count > 0)
        {
            var firstDay = file.Rows.Min(r => r.PostedOn);
            accounts.SetOpening(account.Id, check.StartCents, firstDay.AddDays(-1));
        }

        // 4. pair moves: new rows against each other and against legs still waiting
        var kinds = accounts.All().ToDictionary(a => a.Id, a => a.Kind);
        var patterns = new MovePatternRepository(conn, tx).All();
        var pending = ledger.PendingMoveLegs();
        var pendingById = pending.ToDictionary(p => p.TransactionId);

        // new rows get negative temporary ids so they cannot collide with stored ones
        var candidates = fresh.Select((r, i) => new MoveCandidate(-(i + 1), account.Id, r.PostedOn, r.AmountCents, r.Description))
            .Concat(pending.Select(p => new MoveCandidate(p.TransactionId, p.AccountId, p.PostedOn, p.AmountCents, p.Description)))
            .ToList();
        var pairing = MovePairer.Pair(candidates, patterns);

        var moveForNew = new Dictionary<int, long>();   // fresh index -> move id
        var paired = 0;
        foreach (var p in pairing.Pairs)
        {
            var outPending = pendingById.TryGetValue(p.OutTransactionId, out var o);
            var inPending = pendingById.TryGetValue(p.InTransactionId, out var i);
            if (outPending && inPending) continue; // two stored legs: nothing new to write

            long moveId;
            if (outPending) moveId = o!.MoveId;
            else if (inPending) moveId = i!.MoveId;
            else
            {
                var pattern = patterns.Single(x => x.Id == p.PatternId);
                moveId = ledger.CreateMove(MovePairer.CountsAs(kinds[pattern.FromAccountId], kinds[pattern.ToAccountId]));
            }

            foreach (var id in new[] { p.OutTransactionId, p.InTransactionId })
                if (id < 0) moveForNew[-(int)id - 1] = moveId;
            paired++;
        }

        var stillPending = 0;
        foreach (var id in pairing.Pending.Where(id => id < 0))
        {
            var idx = -(int)id - 1;
            var row = fresh[idx];
            var pattern = patterns.First(x => LegMatches(x, account.Id, row));
            moveForNew[idx] = ledger.CreateMove(MovePairer.CountsAs(kinds[pattern.FromAccountId], kinds[pattern.ToAccountId]));
            stillPending++;
        }

        // 5. rules on the new non-move rows, then write
        var classifier = LineClassifier.Load(conn, clock, tx);

        var inserted = 0;
        var review = 0;
        for (var idx = 0; idx < fresh.Count; idx++)
        {
            var r = fresh[idx];
            var line = new NewTransaction(account.Id, r.PostedOn, r.AmountCents, r.Description, r.ExternalId, ImportSource.Csv,
                r.BankBalanceCents, r.CheckNumber.Length > 0 ? r.CheckNumber : null, batchId, r.RawJson);

            if (moveForNew.TryGetValue(idx, out var moveId))
            {
                line = line with { MoveId = moveId };
            }
            else
            {
                var c = classifier.Classify(new RuleLine(r.Description, r.AmountCents, file.BankNumber, account.Kind), r.Merchant);
                line = line with
                {
                    Payee = c.Payee,
                    CategoryId = c.CategoryId,
                    Type = c.Type,
                    RecurringId = c.RecurringId,
                    PlanId = c.PlanId,
                    RuleId = c.RuleId,
                    TagIds = c.TagIds,
                };
                if (c.TagIds.Contains(classifier.ReviewTagId)) review++;
            }

            ledger.Insert(line);
            inserted++;
        }

        // 6. bank balance snapshot and reconcile (deposit accounts)
        Reconciliation? reconciliation = null;
        if (check is not null)
        {
            var lastDay = file.Rows.Max(r => r.PostedOn);
            var bank = file.Rows.Last(r => r.PostedOn == lastDay).BankBalanceCents!.Value;
            new BalanceSnapshotRepository(conn, clock, tx).UpsertBank(account.Id, lastDay, bank);
            reconciliation = new Reconciliation(account.Id, lastDay, bank, ledger.BalanceAsOf(account.Id, lastDay));
        }

        batches.Finish(batchId, "done", file.Rows.Count, inserted, file.Rows.Count - fresh.Count, review);
        return new ImportResult(name, ImportStatus.Done, file.Rows.Count, inserted, file.Rows.Count - fresh.Count, review,
            paired, stillPending, null, batchId, reconciliation);
    }

    private static bool LegMatches(MovePattern p, long accountId, TruistRow row) =>
        (p.FromAccountId == accountId && row.AmountCents < 0 && row.Description.Contains(p.FromContains, StringComparison.OrdinalIgnoreCase)) ||
        (p.ToAccountId == accountId && row.AmountCents > 0 && row.Description.Contains(p.ToContains, StringComparison.OrdinalIgnoreCase));

    private async Task<ImportResult> RecordFailureAsync(string name, string sha, long? accountId, int rowsRead, string error, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        using var conn = factory.Open();
        return RecordFailure(conn, name, sha, accountId, rowsRead, error);
    }

    private ImportResult RecordFailure(IDbConnection conn, string name, string sha, long? accountId, int rowsRead, string error)
    {
        var batches = new ImportBatchRepository(conn, clock);
        var id = batches.Start(ImportSource.Csv, name, sha, accountId);
        batches.Finish(id, "failed", rowsRead, 0, 0, 0, error);
        return new ImportResult(name, ImportStatus.Failed, rowsRead, 0, 0, 0, 0, 0, error, id);
    }
}
