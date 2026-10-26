using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Core.Moves;

namespace Tideline.Data.Repositories;

/// <summary>
/// The ledger. There is deliberately no method that updates a bank field of an existing row; the database
/// triggers refuse it as well (docs/CLAUDE.md, non-negotiable 1).
/// </summary>
public sealed class LedgerRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public long Insert(NewTransaction t)
    {
        TypeHandlers.EnsureRegistered();
        var now = clock.GetUtcNow();
        conn.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, bank_balance_cents, check_number,
                                      external_id, source, import_batch_id, raw_json, payee, category_id, type, move_id,
                                      recurring_id, plan_id, rule_id, created_at, updated_at)
            VALUES (@AccountId, @PostedOn, @AmountCents, @OriginalDescription, @BankBalanceCents, @CheckNumber,
                    @ExternalId, @Source, @ImportBatchId, @RawJson, @Payee, @CategoryId, @Type, @MoveId,
                    @RecurringId, @PlanId, @RuleId, @now, @now)
            """, new
        {
            t.AccountId, t.PostedOn, t.AmountCents, t.OriginalDescription, t.BankBalanceCents, t.CheckNumber,
            t.ExternalId, Source = t.Source.ToDb(), t.ImportBatchId, t.RawJson, t.Payee, t.CategoryId,
            Type = t.Type?.ToDb(), t.MoveId, t.RecurringId, t.PlanId, t.RuleId, now,
        }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);

        foreach (var tagId in (t.TagIds ?? []).Distinct())
            conn.Execute("INSERT INTO transaction_tags (transaction_id, tag_id) VALUES (@id, @tagId)", new { id, tagId }, tx);
        return id;
    }

    /// <summary>Which of these external ids are already in the ledger.</summary>
    public HashSet<string> ExistingExternalIds(IEnumerable<string> externalIds)
    {
        var found = new HashSet<string>();
        foreach (var chunk in externalIds.Chunk(500))
            foreach (var id in conn.Query<string>("SELECT external_id FROM transactions WHERE external_id IN @chunk", new { chunk }, tx))
                found.Add(id);
        return found;
    }

    public long Count() => conn.ExecuteScalar<long>("SELECT COUNT(*) FROM transactions", transaction: tx);

    public TransactionRow? Get(long id) => Query("WHERE t.id = @id", new { id }).SingleOrDefault();

    public IReadOnlyList<TransactionRow> ForAccount(long accountId) =>
        Query("WHERE t.account_id = @accountId ORDER BY t.posted_on, t.id", new { accountId });

    private sealed class Row
    {
        public long Id { get; set; }
        public long AccountId { get; set; }
        public DateOnly PostedOn { get; set; }
        public long AmountCents { get; set; }
        public string OriginalDescription { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string? Payee { get; set; }
        public long? CategoryId { get; set; }
        public string? Type { get; set; }
        public long? MoveId { get; set; }
        public long? RuleId { get; set; }
        public bool IsManualOverride { get; set; }
    }

    private sealed class LegRow
    {
        public long TransactionId { get; set; }
        public long AccountId { get; set; }
        public DateOnly PostedOn { get; set; }
        public long AmountCents { get; set; }
        public string Description { get; set; } = "";
        public long MoveId { get; set; }
    }

    private List<TransactionRow> Query(string where, object args)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<Row>($"""
            SELECT t.id AS Id, t.account_id AS AccountId, t.posted_on AS PostedOn, t.amount_cents AS AmountCents,
                   t.original_description AS OriginalDescription, t.external_id AS ExternalId, t.payee AS Payee,
                   t.category_id AS CategoryId, t.type AS Type, t.move_id AS MoveId, t.rule_id AS RuleId,
                   t.is_manual_override AS IsManualOverride
            FROM transactions t {where}
            """, args, tx)
            .Select(r => new TransactionRow(r.Id, r.AccountId, r.PostedOn, r.AmountCents, r.OriginalDescription, r.ExternalId,
                r.Payee, r.CategoryId, r.Type is null ? null : TypeHandlers.FromDb<LineType>(r.Type), r.MoveId, r.RuleId, r.IsManualOverride))
            .ToList();
    }

    /// <summary>Rows in a move that has only this one leg so far, waiting for a partner.</summary>
    public IReadOnlyList<PendingLeg> PendingMoveLegs()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<LegRow>("""
            SELECT t.id AS TransactionId, t.account_id AS AccountId, t.posted_on AS PostedOn, t.amount_cents AS AmountCents,
                   t.original_description AS Description, t.move_id AS MoveId
            FROM transactions t
            WHERE t.move_id IS NOT NULL
              AND (SELECT COUNT(*) FROM transactions x WHERE x.move_id = t.move_id) = 1
            ORDER BY t.posted_on, t.id
            """, transaction: tx).Select(r => new PendingLeg(r.TransactionId, r.AccountId, r.PostedOn, r.AmountCents, r.Description, r.MoveId)).ToList();
    }

    public long CreateMove(MoveCountsAs countsAs)
    {
        conn.Execute("INSERT INTO moves (counts_as, created_at) VALUES (@countsAs, @now)",
            new { countsAs = countsAs.ToDb(), now = clock.GetUtcNow() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    /// <summary>Opening balance (0 when not known) plus every row up to and including <paramref name="date"/>.</summary>
    public long BalanceAsOf(long accountId, DateOnly date)
    {
        TypeHandlers.EnsureRegistered();
        return conn.ExecuteScalar<long>("""
            SELECT COALESCE((SELECT opening_balance_cents FROM accounts WHERE id = @accountId), 0)
                 + COALESCE((SELECT SUM(amount_cents) FROM transactions WHERE account_id = @accountId AND posted_on <= @date AND left_out_at IS NULL), 0)
            """, new { accountId, date }, tx);
    }
}

public sealed class BalanceSnapshotRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    /// <summary>Record what the bank says the balance was on a day; replaces an earlier bank snapshot for that day.</summary>
    public void UpsertBank(long accountId, DateOnly asOf, long balanceCents)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("DELETE FROM balance_snapshots WHERE account_id = @accountId AND as_of_on = @asOf AND source = 'bank'",
            new { accountId, asOf }, tx);
        conn.Execute("""
            INSERT INTO balance_snapshots (account_id, as_of_on, balance_cents, source, created_at)
            VALUES (@accountId, @asOf, @balanceCents, 'bank', @now)
            """, new { accountId, asOf, balanceCents, now = clock.GetUtcNow() }, tx);
    }

    public long? LatestBank(long accountId) =>
        conn.ExecuteScalar<long?>("""
            SELECT balance_cents FROM balance_snapshots WHERE account_id = @accountId AND source = 'bank'
            ORDER BY as_of_on DESC LIMIT 1
            """, new { accountId }, tx);
}

public sealed class ImportBatchRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public long Start(ImportSource source, string? fileName, string? fileSha256, long? accountId)
    {
        conn.Execute("""
            INSERT INTO import_batches (source, file_name, file_sha256, account_id, started_at, status)
            VALUES (@source, @fileName, @fileSha256, @accountId, @now, 'running')
            """, new { source = source.ToDb(), fileName, fileSha256, accountId, now = clock.GetUtcNow() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public void Finish(long id, string status, int rowsRead, int rowsInserted, int rowsExisting, int rowsReview, string? error = null) =>
        conn.Execute("""
            UPDATE import_batches
            SET finished_at = @now, status = @status, rows_read = @rowsRead, rows_inserted = @rowsInserted,
                rows_existing = @rowsExisting, rows_review = @rowsReview, error = @error
            WHERE id = @id
            """, new { id, status, rowsRead, rowsInserted, rowsExisting, rowsReview, error, now = clock.GetUtcNow() }, tx);

    private sealed class SummaryRow
    {
        public long Id { get; set; }
        public string Status { get; set; } = "";
        public int RowsRead { get; set; }
        public int RowsInserted { get; set; }
        public int RowsExisting { get; set; }
        public int RowsReview { get; set; }
        public string? Error { get; set; }
    }

    public ImportBatchSummary? Get(long id) =>
        conn.QuerySingleOrDefault<SummaryRow>("""
            SELECT id AS Id, status AS Status, rows_read AS RowsRead, rows_inserted AS RowsInserted,
                   rows_existing AS RowsExisting, rows_review AS RowsReview, error AS Error
            FROM import_batches WHERE id = @id
            """, new { id }, tx)
            is { } r ? new ImportBatchSummary(r.Id, r.Status, r.RowsRead, r.RowsInserted, r.RowsExisting, r.RowsReview, r.Error) : null;
}
