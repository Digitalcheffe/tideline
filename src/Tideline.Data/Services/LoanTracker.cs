using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Services;

/// <summary>
/// Keeps a loan's balance moving between statements (docs/04-domain-rules.md, "Debt countdown"). Each payment in the loan's payment
/// category writes a <c>computed</c> snapshot of <c>B * (1 + r) - P</c> on the day it posted. A figure you enter from a statement or
/// as an estimate always wins: computing starts again from the latest one of those.
/// </summary>
public static class LoanTracker
{
    /// <summary>Which snapshot wins when two share a day.</summary>
    public const string PriorityOrderSql = "CASE source WHEN 'statement' THEN 0 WHEN 'bank' THEN 1 WHEN 'estimate' THEN 2 ELSE 3 END";

    private sealed class Loan
    {
        public long AccountId { get; set; }
        public int RateBps { get; set; }
        public long PaymentCents { get; set; }
        public long CategoryId { get; set; }
    }

    /// <summary>
    /// What is owed on a loan on a day: the latest figure you entered, moved on by every payment since in the loan's payment category.
    /// Worked out from the ledger each time, so editing a line is reflected at once. Source is <c>computed</c> when payments were applied.
    /// </summary>
    public static (DateOnly AsOn, long Cents, string Source)? BalanceOn(IDbConnection conn, long accountId, DateOnly date)
    {
        TypeHandlers.EnsureRegistered();
        var anchor = conn.QueryFirstOrDefault<(DateOnly AsOn, long Cents, string Source)?>($"""
            SELECT as_of_on, balance_cents, source FROM balance_snapshots
            WHERE account_id = @accountId AND source <> 'computed' AND as_of_on <= @date ORDER BY as_of_on DESC, {PriorityOrderSql} LIMIT 1
            """, new { accountId, date });
        if (anchor is not { } a) return null;

        var loan = conn.QueryFirstOrDefault<Loan>("""
            SELECT account_id AS AccountId, rate_bps AS RateBps, payment_cents AS PaymentCents, payment_category_id AS CategoryId
            FROM loan_terms WHERE account_id = @accountId AND rate_bps IS NOT NULL AND payment_cents > 0 AND payment_category_id IS NOT NULL
            """, new { accountId });
        if (loan is null) return a;

        var payments = conn.Query<DateOnly>("""
            SELECT posted_on FROM transactions
            WHERE category_id = @category AND amount_cents < 0 AND move_id IS NULL AND posted_on > @since AND posted_on <= @date
            ORDER BY posted_on, id
            """, new { category = loan.CategoryId, since = a.AsOn, date }).ToList();
        if (payments.Count == 0) return a;

        var balance = a.Cents;
        foreach (var _ in payments) balance = DebtCountdown.NextBalance(balance, loan.RateBps, loan.PaymentCents);
        return (payments[^1], balance, "computed");
    }

    /// <summary>Recomputes every loan's computed snapshots. Safe to run any time: the same ledger gives the same snapshots.</summary>
    public static int Refresh(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
    {
        TypeHandlers.EnsureRegistered();
        var written = 0;
        var loans = conn.Query<Loan>("""
            SELECT account_id AS AccountId, rate_bps AS RateBps, payment_cents AS PaymentCents, payment_category_id AS CategoryId
            FROM loan_terms WHERE rate_bps IS NOT NULL AND payment_cents IS NOT NULL AND payment_cents > 0 AND payment_category_id IS NOT NULL
            """, transaction: tx).ToList();

        foreach (var loan in loans)
        {
            var anchor = conn.QueryFirstOrDefault<(DateOnly AsOn, long Balance)?>($"""
                SELECT as_of_on, balance_cents FROM balance_snapshots
                WHERE account_id = @id AND source <> 'computed' ORDER BY as_of_on DESC, {PriorityOrderSql} LIMIT 1
                """, new { id = loan.AccountId }, tx);
            if (anchor is null) continue;
            var (since, balance) = anchor.Value;

            conn.Execute("DELETE FROM balance_snapshots WHERE account_id = @id AND source = 'computed' AND as_of_on > @since", new { id = loan.AccountId, since }, tx);

            var payments = conn.Query<DateOnly>("""
                SELECT posted_on FROM transactions
                WHERE category_id = @category AND amount_cents < 0 AND move_id IS NULL AND posted_on > @since
                ORDER BY posted_on, id
                """, new { category = loan.CategoryId, since }, tx).ToList();

            foreach (var day in payments)
            {
                balance = DebtCountdown.NextBalance(balance, loan.RateBps, loan.PaymentCents);
                conn.Execute("DELETE FROM balance_snapshots WHERE account_id = @id AND as_of_on = @day AND source = 'computed'", new { id = loan.AccountId, day }, tx);
                conn.Execute("""
                    INSERT INTO balance_snapshots (account_id, as_of_on, balance_cents, source, note, created_at)
                    VALUES (@id, @day, @balance, 'computed', 'after a payment', @now)
                    """, new { id = loan.AccountId, day, balance, now = clock.GetUtcNow() }, tx);
                written++;
            }
        }
        return written;
    }
}
