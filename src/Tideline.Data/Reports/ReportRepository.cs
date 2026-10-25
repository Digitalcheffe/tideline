using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Reports;

public sealed record MonthTypeTotal(string Month, LineType Type, long ReportCents);
public sealed record CategoryTotal(long CategoryId, string Name, long Cents, long Lines);
public sealed record CategoryMonthTotal(long CategoryId, string Month, long Cents);
public sealed record PayeeTotal(string Payee, long Cents, long Lines);
public sealed record AccountMonthFlow(long AccountId, string Month, long InCents, long OutCents, long NetCents);
public sealed record RecurringMonthPaid(long RecurringId, string Month, long Cents);
public sealed record UnlinkedBillMonth(long CategoryId, string CategoryName, string Month, long Cents);

/// <summary>Aggregates behind the screens. Every figure here has a matching /lines drill (see LineFilter).</summary>
public sealed class ReportRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    // the spread counts effective lines on in-spread accounts, plus savings-move legs typed savings
    private static string SpreadLines => $"""
        WITH {EffectiveSql.Ctes},
        counted AS (
            SELECT {EffectiveSql.Columns} FROM effective
            UNION ALL
            SELECT {EffectiveSql.Columns} FROM savings_legs
        )
        """;

    private sealed class MonthTypeRow
    {
        public string Month { get; set; } = "";
        public string Type { get; set; } = "";
        public long Cents { get; set; }
    }

    public IReadOnlyList<MonthTypeTotal> SpreadByMonth(DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<MonthTypeRow>($"""
            {SpreadLines}
            SELECT SUBSTR(e.posted_on, 1, 7) AS Month, e.type AS Type,
                   SUM(CASE WHEN e.type = 'income' THEN e.amount_cents ELSE -e.amount_cents END) AS Cents
            FROM counted e JOIN accounts a ON a.id = e.account_id
            WHERE a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY SUBSTR(e.posted_on, 1, 7), e.type
            ORDER BY 1, 2
            """, new { start, end }, tx)
            .Select(r => new MonthTypeTotal(r.Month, TypeHandlers.FromDb<LineType>(r.Type), r.Cents)).ToList();
    }

    public Spread SpreadFor(DateOnly start, DateOnly end) =>
        Spread.From(SpreadByMonth(start, end).Select(t => (t.Type, t.ReportCents)));

    public IReadOnlyList<CategoryTotal> CategoryTotals(LineType type, DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Name, long Cents, long Lines)>($"""
            {SpreadLines}
            SELECT COALESCE(c.id, 0), COALESCE(c.name, 'No category'), SUM({(type == LineType.Income ? "e.amount_cents" : "-e.amount_cents")}) AS cents, COUNT(*) AS lines
            FROM effective e LEFT JOIN categories c ON c.id = e.category_id JOIN accounts a ON a.id = e.account_id
            WHERE e.type = @type AND a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY COALESCE(c.id, 0), COALESCE(c.name, 'No category')
            ORDER BY cents DESC, 2
            """, new { type = type.ToDb(), start, end }, tx)
            .Select(r => new CategoryTotal(r.Id, r.Name, r.Cents, r.Lines)).ToList();
    }

    public IReadOnlyList<CategoryMonthTotal> CategoryByMonth(LineType type, DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Month, long Cents)>($"""
            {SpreadLines}
            SELECT COALESCE(e.category_id, 0), SUBSTR(e.posted_on, 1, 7), SUM({(type == LineType.Income ? "e.amount_cents" : "-e.amount_cents")})
            FROM effective e JOIN accounts a ON a.id = e.account_id
            WHERE e.type = @type AND a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY COALESCE(e.category_id, 0), SUBSTR(e.posted_on, 1, 7)
            ORDER BY 1, 2
            """, new { type = type.ToDb(), start, end }, tx)
            .Select(r => new CategoryMonthTotal(r.Id, r.Month, r.Cents)).ToList();
    }

    /// <summary>What was spent per category per month, across bills, required, extra and one-time: what a budget guideline is measured against.</summary>
    public IReadOnlyList<CategoryMonthTotal> SpendByCategoryMonth(DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Month, long Cents)>($"""
            {SpreadLines}
            SELECT COALESCE(e.category_id, 0), SUBSTR(e.posted_on, 1, 7), SUM(-e.amount_cents)
            FROM effective e JOIN accounts a ON a.id = e.account_id
            WHERE e.type IN ('bill', 'required', 'extra', 'one_time') AND a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY COALESCE(e.category_id, 0), SUBSTR(e.posted_on, 1, 7)
            ORDER BY 1, 2
            """, new { start, end }, tx)
            .Select(r => new CategoryMonthTotal(r.Id, r.Month, r.Cents)).ToList();
    }

    public sealed record PlanSaved(long PlanId, string Month, long Cents);

    /// <summary>Savings lines and savings-move legs linked to a plan, per plan per month, as money put aside (positive).</summary>
    public IReadOnlyList<PlanSaved> SavedToPlansByMonth(DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long PlanId, string Month, long Cents)>($"""
            {SpreadLines}
            SELECT e.plan_id, SUBSTR(e.posted_on, 1, 7), SUM(-e.amount_cents)
            FROM counted e
            WHERE e.type = 'savings' AND e.plan_id IS NOT NULL AND e.posted_on <= @end
            GROUP BY e.plan_id, SUBSTR(e.posted_on, 1, 7)
            ORDER BY 1, 2
            """, new { end }, tx)
            .Select(r => new PlanSaved(r.PlanId, r.Month, r.Cents)).ToList();
    }

    public IReadOnlyList<PayeeTotal> TopPayees(LineType type, DateOnly start, DateOnly end, int limit = 10)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(string Payee, long Cents, long Lines)>($"""
            {SpreadLines}
            SELECT COALESCE(e.payee, e.description) AS payee, SUM({(type == LineType.Income ? "e.amount_cents" : "-e.amount_cents")}) AS cents, COUNT(*) AS lines
            FROM effective e JOIN accounts a ON a.id = e.account_id
            WHERE e.type = @type AND a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY COALESCE(e.payee, e.description)
            ORDER BY cents DESC, 1
            LIMIT @limit
            """, new { type = type.ToDb(), start, end, limit }, tx)
            .Select(r => new PayeeTotal(r.Payee, r.Cents, r.Lines)).ToList();
    }

    /// <summary>In, out and net per account per month over every row, including move legs, so end balances reconcile.</summary>
    public IReadOnlyList<AccountMonthFlow> AccountFlows(DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long AccountId, string Month, long In, long Out, long Net)>("""
            SELECT t.account_id, SUBSTR(t.posted_on, 1, 7),
                   SUM(CASE WHEN t.amount_cents > 0 THEN t.amount_cents ELSE 0 END),
                   SUM(CASE WHEN t.amount_cents < 0 THEN -t.amount_cents ELSE 0 END),
                   SUM(t.amount_cents)
            FROM transactions t
            WHERE t.posted_on BETWEEN @start AND @end
            GROUP BY t.account_id, SUBSTR(t.posted_on, 1, 7)
            ORDER BY 1, 2
            """, new { start, end }, tx)
            .Select(r => new AccountMonthFlow(r.AccountId, r.Month, r.In, r.Out, r.Net)).ToList();
    }

    /// <summary>Sum of every row of an account strictly before a date (add the opening balance for a balance).</summary>
    public long NetBefore(long accountId, DateOnly date)
    {
        TypeHandlers.EnsureRegistered();
        return conn.ExecuteScalar<long>("SELECT COALESCE(SUM(amount_cents), 0) FROM transactions WHERE account_id = @accountId AND posted_on < @date",
            new { accountId, date }, tx);
    }

    /// <summary>Spend (positive) per recurring item per month: effective lines linked to the item, any account.</summary>
    public IReadOnlyList<RecurringMonthPaid> RecurringPaid(DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Month, long Cents)>($"""
            {SpreadLines}
            SELECT e.recurring_id, SUBSTR(e.posted_on, 1, 7), SUM(-e.amount_cents)
            FROM effective e
            WHERE e.recurring_id IS NOT NULL AND e.posted_on BETWEEN @start AND @end
            GROUP BY e.recurring_id, SUBSTR(e.posted_on, 1, 7)
            ORDER BY 1, 2
            """, new { start, end }, tx)
            .Select(r => new RecurringMonthPaid(r.Id, r.Month, r.Cents)).ToList();
    }

    /// <summary>Bill lines the grid has no row for (no recurring item, or an item that is not a bill), per category per month ("Other: Insurance").</summary>
    public IReadOnlyList<UnlinkedBillMonth> UnlinkedBills(DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Name, string Month, long Cents)>($"""
            {SpreadLines}
            SELECT c.id, c.name, SUBSTR(e.posted_on, 1, 7), SUM(-e.amount_cents)
            FROM effective e JOIN categories c ON c.id = e.category_id JOIN accounts a ON a.id = e.account_id
            WHERE e.type = 'bill' AND ({EffectiveSql.OutsideBillRows}) AND a.is_in_spread = 1 AND e.posted_on BETWEEN @start AND @end
            GROUP BY c.id, c.name, SUBSTR(e.posted_on, 1, 7)
            ORDER BY 2, 3
            """, new { start, end }, tx)
            .Select(r => new UnlinkedBillMonth(r.Id, r.Name, r.Month, r.Cents)).ToList();
    }

    /// <summary>Spend per category on one account (cards), over effective lines of any type; refunds reduce it.</summary>
    public IReadOnlyList<CategoryTotal> AccountCategoryTotals(long accountId, DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Name, long Cents, long Lines)>($"""
            {SpreadLines}
            SELECT c.id, c.name, SUM(-e.amount_cents) AS cents, COUNT(*) AS lines
            FROM effective e JOIN categories c ON c.id = e.category_id
            WHERE e.account_id = @accountId AND e.posted_on BETWEEN @start AND @end
            GROUP BY c.id, c.name
            ORDER BY cents DESC, c.name
            """, new { accountId, start, end }, tx)
            .Select(r => new CategoryTotal(r.Id, r.Name, r.Cents, r.Lines)).ToList();
    }

    /// <summary>Card-side payment legs: positive move legs on a credit card account.</summary>
    public long CardPayments(long accountId, DateOnly start, DateOnly end)
    {
        TypeHandlers.EnsureRegistered();
        return conn.ExecuteScalar<long>("""
            SELECT COALESCE(SUM(amount_cents), 0) FROM transactions
            WHERE account_id = @accountId AND move_id IS NOT NULL AND amount_cents > 0 AND posted_on BETWEEN @start AND @end
            """, new { accountId, start, end }, tx);
    }
}
