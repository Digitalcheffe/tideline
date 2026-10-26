using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Reports;

public sealed record DepositMonth(string Month, long Cents);

public sealed record SavingsAccountView(
    long AccountId, string Name, long BalanceCents, int? RateBps, long AverageMonthlyDepositCents, IReadOnlyList<DepositMonth> Deposits, long InterestPerMonthCents);

public sealed record SavingsGoalView(PlanView Plan, long? ProjectedAtTargetCents, bool? OnPace);

public sealed record SavingsView(
    long TotalBalanceCents, long TotalAverageMonthlyDepositCents, IReadOnlyList<SavingsAccountView> Accounts, IReadOnlyList<SavingsGoalView> Goals);

/// <summary>Money market balances, what goes in each month, and the plans they fund (docs/ARCHITECTURE.md).</summary>
public sealed class SavingsReader(IDbConnection conn)
{
    /// <summary>Balance after <paramref name="months"/> of the same monthly deposit at the account's rate (or none).</summary>
    public static long ProjectedBalance(long balanceCents, long monthlyDepositCents, int months, int? rateBps)
    {
        var r = (rateBps ?? 0) / 10000.0 / 12.0;
        if (months <= 0) return balanceCents;
        var growth = Math.Pow(1 + r, months);
        var value = r == 0 ? balanceCents + monthlyDepositCents * (double)months : balanceCents * growth + monthlyDepositCents * (growth - 1) / r;
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public SavingsView Read(DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var ledger = new LedgerRepository(conn, TimeProvider.System);
        var thisMonth = today.ToString("yyyy-MM");
        var firstMonth = DebtCountdown.AddMonths(thisMonth, -11);
        var from = new DateOnly(int.Parse(firstMonth[..4]), int.Parse(firstMonth[5..7]), 1);

        var accounts = new List<SavingsAccountView>();
        foreach (var a in conn.Query<(long Id, string Name, int? Rate)>("SELECT id, name, interest_rate_bps FROM accounts WHERE kind = 'savings' AND is_closed = 0 ORDER BY sort_order, id"))
        {
            var byMonth = conn.Query<(string Month, long Cents)>("""
                SELECT SUBSTR(posted_on, 1, 7), SUM(amount_cents) FROM transactions
                WHERE account_id = @id AND amount_cents > 0 AND posted_on >= @from AND left_out_at IS NULL GROUP BY 1
                """, new { id = a.Id, from }).ToDictionary(x => x.Month, x => x.Cents);
            var deposits = Enumerable.Range(0, 12).Select(i => DebtCountdown.AddMonths(firstMonth, i)).Select(m => new DepositMonth(m, byMonth.GetValueOrDefault(m))).ToList();

            // the average is over the last six whole months, so one odd month does not set the pace
            var recent = deposits.Where(d => d.Month != thisMonth).TakeLast(6).ToList();
            var average = recent.Count == 0 ? 0 : (long)Math.Round(recent.Sum(d => d.Cents) / (double)recent.Count, MidpointRounding.AwayFromZero);
            var balance = ledger.BalanceAsOf(a.Id, today);
            var interest = a.Rate is { } r ? (long)Math.Round(balance * r / 10000.0 / 12.0, MidpointRounding.AwayFromZero) : 0;
            accounts.Add(new SavingsAccountView(a.Id, a.Name, balance, a.Rate, average, deposits, interest));
        }

        var byId = accounts.ToDictionary(a => a.AccountId);
        var goals = new List<SavingsGoalView>();
        foreach (var plan in new PlanReader(conn).Read(today).Where(p => p.Lifecycle == "active"))
        {
            long? projected = null;
            bool? onPace = null;
            if (plan.FundingAccountId is { } id && byId.TryGetValue(id, out var account))
            {
                var months = Math.Max(0, PlanTracker.MonthsBetween(thisMonth, plan.TargetOn[..7]));
                projected = ProjectedBalance(account.BalanceCents, account.AverageMonthlyDepositCents, months, account.RateBps);
                onPace = projected >= plan.TargetCents;
            }
            goals.Add(new SavingsGoalView(plan, projected, onPace));
        }

        return new SavingsView(accounts.Sum(a => a.BalanceCents), accounts.Sum(a => a.AverageMonthlyDepositCents), accounts, goals);
    }
}
