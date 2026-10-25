using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;
using Tideline.Data.Services;

namespace Tideline.Data.Reports;

public sealed record NetWorthLine(long AccountId, string Name, string Kind, long Cents, string? AsOf, string Source, bool Stale);

public sealed record LoanView(
    long AccountId, string Name, string LoanType, long? BalanceCents, string? AsOf, string? Source, bool Stale,
    int? RateBps, long? PaymentCents, long? EscrowCents, Payoff? Payoff, long? SecuredAssetId, string? SecuredAssetName, bool MissingTerms);

public sealed record EquityView(long AssetId, string AssetName, long AssetCents, string? AssetAsOf, bool AssetStale, long LoanId, string LoanName, long LoanCents, long EquityCents);

public sealed record NetWorthPoint(string Month, long AssetsCents, long LiabilitiesCents, long NetWorthCents);

public sealed record NetWorthView(
    string AsOf, long AssetsCents, long LiabilitiesCents, long NetWorthCents,
    IReadOnlyList<NetWorthLine> Assets, IReadOnlyList<NetWorthLine> Liabilities,
    IReadOnlyList<LoanView> Loans, IReadOnlyList<EquityView> Equity, IReadOnlyList<NetWorthPoint> Trend,
    IReadOnlyList<string> NeedsInput);

/// <summary>Net worth, equity and the debt countdown (docs/04-domain-rules.md). Every figure carries the day it is as of.</summary>
public sealed class NetWorthReader(IDbConnection conn)
{
    public const int StaleDays = 90;

    private sealed class Account
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool IsClosed { get; set; }
    }

    private sealed class Snapshot
    {
        public DateOnly AsOn { get; set; }
        public long Cents { get; set; }
        public string Source { get; set; } = "";
    }

    private Snapshot? Latest(long accountId, DateOnly date)
    {
        if (conn.ExecuteScalar<string?>("SELECT kind FROM accounts WHERE id = @accountId", new { accountId }) == "loan")
            return LoanTracker.BalanceOn(conn, accountId, date) is { } b ? new Snapshot { AsOn = b.AsOn, Cents = b.Cents, Source = b.Source } : null;
        return Stored(accountId, date);
    }

    private Snapshot? Stored(long accountId, DateOnly date) => conn.QueryFirstOrDefault<Snapshot>($"""
        SELECT as_of_on AS AsOn, balance_cents AS Cents, source AS Source FROM balance_snapshots
        WHERE account_id = @accountId AND as_of_on <= @date ORDER BY as_of_on DESC, {LoanTracker.PriorityOrderSql} LIMIT 1
        """, new { accountId, date });

    private static bool IsStale(Snapshot s, DateOnly today) => s.Source == "estimate" && today.DayNumber - s.AsOn.DayNumber > StaleDays;

    /// <summary>Assets and liabilities on one day. Money accounts come from the ledger; loans, assets and retirement from the latest figure on or before it.</summary>
    private (List<NetWorthLine> Assets, List<NetWorthLine> Liabilities) On(DateOnly date, DateOnly today, IReadOnlyList<Account> accounts, LedgerRepository ledger)
    {
        var assets = new List<NetWorthLine>();
        var liabilities = new List<NetWorthLine>();
        foreach (var a in accounts)
        {
            switch (a.Kind)
            {
                case "checking" or "savings":
                {
                    var balance = ledger.BalanceAsOf(a.Id, date);
                    if (balance > 0) assets.Add(new NetWorthLine(a.Id, a.Name, a.Kind, balance, date.ToString("yyyy-MM-dd"), "ledger", false));
                    break;
                }
                case "credit_card":
                {
                    var balance = ledger.BalanceAsOf(a.Id, date);
                    if (balance < 0) liabilities.Add(new NetWorthLine(a.Id, a.Name, a.Kind, -balance, date.ToString("yyyy-MM-dd"), "ledger", false));
                    break;
                }
                case "loan" or "asset" or "retirement":
                {
                    if (Latest(a.Id, date) is not { } s) break;
                    var line = new NetWorthLine(a.Id, a.Name, a.Kind, s.Cents, s.AsOn.ToString("yyyy-MM-dd"), s.Source, date == today && IsStale(s, today));
                    if (a.Kind == "loan") { if (s.Cents > 0) liabilities.Add(line); }
                    else assets.Add(line);
                    break;
                }
            }
        }
        return (assets, liabilities);
    }

    public NetWorthView Read(DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var ledger = new LedgerRepository(conn, TimeProvider.System);
        var accounts = conn.Query<Account>("SELECT id AS Id, name AS Name, kind AS Kind, is_closed AS IsClosed FROM accounts ORDER BY sort_order, id").ToList();
        var (assets, liabilities) = On(today, today, accounts, ledger);

        var needs = new List<string>();
        var loans = LoansView(today, accounts, needs);
        foreach (var a in accounts.Where(a => a.Kind is "asset" or "retirement" && Latest(a.Id, today) is null))
            needs.Add($"Enter what {a.Name} is worth.");

        return new NetWorthView(today.ToString("yyyy-MM-dd"), assets.Sum(x => x.Cents), liabilities.Sum(x => x.Cents), assets.Sum(x => x.Cents) - liabilities.Sum(x => x.Cents),
            assets, liabilities, loans, EquityFor(today, accounts, loans), Trend(today, accounts, ledger), needs);
    }

    private List<LoanView> LoansView(DateOnly today, IReadOnlyList<Account> accounts, List<string> needs)
    {
        var month = today.ToString("yyyy-MM");
        var result = new List<LoanView>();
        foreach (var a in accounts.Where(a => a.Kind == "loan"))
        {
            var terms = conn.QuerySingleOrDefault<(string Type, int? Rate, long? Payment, long? Escrow, long? Secured)?>(
                "SELECT loan_type, rate_bps, payment_cents, escrow_cents, secured_asset_id FROM loan_terms WHERE account_id = @id", new { id = a.Id });
            var snap = Latest(a.Id, today);
            var securedName = terms?.Secured is { } sid ? conn.ExecuteScalar<string?>("SELECT name FROM accounts WHERE id = @sid", new { sid }) : null;
            var missing = terms is null || terms.Value.Rate is null || terms.Value.Payment is null;
            if (snap is null) needs.Add($"Enter what is owed on {a.Name}.");
            if (missing) needs.Add($"Enter the rate and the monthly payment for {a.Name}.");

            Payoff? payoff = snap is not null && terms is { Rate: { } rate, Payment: { } payment } ? DebtCountdown.Compute(snap.Cents, rate, payment, month) : null;
            result.Add(new LoanView(a.Id, a.Name, terms?.Type ?? "other", snap?.Cents, snap?.AsOn.ToString("yyyy-MM-dd"), snap?.Source, snap is not null && IsStale(snap, today),
                terms?.Rate, terms?.Payment, terms?.Escrow, payoff, terms?.Secured, securedName, missing));
        }
        return result;
    }

    private List<EquityView> EquityFor(DateOnly today, IReadOnlyList<Account> accounts, IReadOnlyList<LoanView> loans)
    {
        var result = new List<EquityView>();
        foreach (var loan in loans.Where(l => l.SecuredAssetId is not null && l.BalanceCents is not null))
        {
            var asset = accounts.First(a => a.Id == loan.SecuredAssetId);
            var snap = Latest(asset.Id, today);
            if (snap is null) continue;
            result.Add(new EquityView(asset.Id, asset.Name, snap.Cents, snap.AsOn.ToString("yyyy-MM-dd"), IsStale(snap, today), loan.AccountId, loan.Name, loan.BalanceCents!.Value, snap.Cents - loan.BalanceCents.Value));
        }
        return result;
    }

    /// <summary>Net worth at each month end, from the ledger and from snapshots carried forward. At most the last 36 months.</summary>
    private List<NetWorthPoint> Trend(DateOnly today, IReadOnlyList<Account> accounts, LedgerRepository ledger)
    {
        var first = conn.ExecuteScalar<string?>("""
            SELECT MIN(d) FROM (SELECT MIN(posted_on) AS d FROM transactions UNION ALL SELECT MIN(as_of_on) FROM balance_snapshots)
            """);
        var points = new List<NetWorthPoint>();
        if (first is null) return points;

        var month = first[..7];
        var current = today.ToString("yyyy-MM");
        var from = DebtCountdown.AddMonths(current, -35);
        if (string.CompareOrdinal(month, from) < 0) month = from;

        for (; string.CompareOrdinal(month, current) <= 0; month = DebtCountdown.AddMonths(month, 1))
        {
            var y = int.Parse(month[..4]);
            var m = int.Parse(month[5..7]);
            var end = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
            if (end > today) end = today;
            var (assets, liabilities) = On(end, today, accounts, ledger);
            var a = assets.Sum(x => x.Cents);
            var l = liabilities.Sum(x => x.Cents);
            points.Add(new NetWorthPoint(month, a, l, a - l));
        }
        return points;
    }

    /// <summary>The countdown for one loan with an extra amount each month: months and interest saved.</summary>
    public ExtraPaymentEffect? Countdown(long loanAccountId, long extraCents, DateOnly today)
    {
        var snap = Latest(loanAccountId, today);
        var terms = conn.QuerySingleOrDefault<(int? Rate, long? Payment)?>("SELECT rate_bps, payment_cents FROM loan_terms WHERE account_id = @id", new { id = loanAccountId });
        if (snap is null || terms is not { Rate: { } rate, Payment: { } payment }) return null;
        return DebtCountdown.WithExtra(snap.Cents, rate, payment, Math.Max(0, extraCents), today.ToString("yyyy-MM"));
    }
}
