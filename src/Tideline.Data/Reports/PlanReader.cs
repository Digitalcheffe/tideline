using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Reports;

public sealed record PlanCheckpoint(string Month, long ExpectedCents, long ActualCents, string Status);

public sealed record PlanTrouble(string Month, string Status);

public sealed record PlanView(
    long Id, string Name, long TargetCents, string StartOn, string TargetOn, long StartAmountCents, long? FundingAccountId, string? FundingAccountName,
    string ProgressMode, bool IsTrackable, string Lifecycle, string? Notes,
    long ActualCents, long ExpectedCents, string Status, int MonthsLeft, long NeededPerMonthCents, long? NeededPerMonthWithInterestCents, int? RateBps,
    PlanTrouble? Trouble, IReadOnlyList<PlanCheckpoint> Checkpoints, int LinkedLines);

/// <summary>
/// Plans and where they stand (docs/ARCHITECTURE.md). Checkpoints are worked out here from the ledger each time they are
/// asked for; the evaluator in Phase 5 will write the same figures to plan_checkpoints at each month end.
/// </summary>
public sealed class PlanReader(IDbConnection conn)
{
    private const int LookBackMonths = 6;

    private sealed class PlanRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public long TargetCents { get; set; }
        public DateOnly StartOn { get; set; }
        public DateOnly TargetOn { get; set; }
        public long StartAmountCents { get; set; }
        public long? FundingAccountId { get; set; }
        public string? FundingAccountName { get; set; }
        public int? RateBps { get; set; }
        public string ProgressMode { get; set; } = "";
        public bool IsTrackable { get; set; }
        public string Status { get; set; } = "";
        public string? Notes { get; set; }
    }

    public IReadOnlyList<PlanView> Read(DateOnly today, bool includeCancelled = false)
    {
        TypeHandlers.EnsureRegistered();
        var rows = conn.Query<PlanRow>($"""
            SELECT p.id AS Id, p.name AS Name, p.target_cents AS TargetCents, p.start_on AS StartOn, p.target_on AS TargetOn, p.start_amount_cents AS StartAmountCents,
                   p.funding_account_id AS FundingAccountId, a.name AS FundingAccountName, a.interest_rate_bps AS RateBps, p.progress_mode AS ProgressMode,
                   p.is_trackable AS IsTrackable, p.status AS Status, p.notes AS Notes
            FROM plans p LEFT JOIN accounts a ON a.id = p.funding_account_id
            {(includeCancelled ? "" : "WHERE p.status <> 'cancelled'")}
            ORDER BY p.target_on, p.id
            """).ToList();
        if (rows.Count == 0) return [];

        var ledger = new LedgerRepository(conn, TimeProvider.System);
        var saved = new ReportRepository(conn).SavedToPlansByMonth(today).ToLookup(s => s.PlanId);
        var linked = conn.Query<(long PlanId, long Lines)>("SELECT plan_id, COUNT(*) FROM transactions WHERE plan_id IS NOT NULL GROUP BY plan_id").ToDictionary(x => x.PlanId, x => x.Lines);
        return rows.Select(p => Build(p, today, ledger, saved[p.Id].ToList(), (int)linked.GetValueOrDefault(p.Id))).ToList();
    }

    private static long Actual(PlanRow p, DateOnly end, LedgerRepository ledger, IReadOnlyList<ReportRepository.PlanSaved> saved)
    {
        if (p.ProgressMode == "account_balance" && p.FundingAccountId is { } account) return ledger.BalanceAsOf(account, end);
        var through = end.ToString("yyyy-MM");
        return p.StartAmountCents + saved.Where(s => string.CompareOrdinal(s.Month, through) <= 0).Sum(s => s.Cents);
    }

    private static PlanView Build(PlanRow p, DateOnly today, LedgerRepository ledger, IReadOnlyList<ReportRepository.PlanSaved> saved, int linkedLines)
    {
        var startMonth = p.StartOn.ToString("yyyy-MM");
        var targetMonth = p.TargetOn.ToString("yyyy-MM");
        var thisMonth = today.ToString("yyyy-MM");
        var total = PlanTracker.MonthsBetween(startMonth, targetMonth);

        var checkpoints = new List<PlanCheckpoint>();
        for (var back = LookBackMonths; back >= 0; back--)
        {
            var month = DebtCountdown.AddMonths(thisMonth, -back);
            if (string.CompareOrdinal(month, startMonth) < 0 || string.CompareOrdinal(month, targetMonth) > 0) continue;
            var end = back == 0 ? today : new DateOnly(int.Parse(month[..4]), int.Parse(month[5..7]), 1).AddMonths(1).AddDays(-1);
            var actual = Actual(p, end, ledger, saved);
            var expected = PlanTracker.Expected(p.StartAmountCents, p.TargetCents, PlanTracker.MonthsBetween(startMonth, month), total);
            checkpoints.Add(new PlanCheckpoint(month, expected, actual, PlanTracker.Status(actual, p.TargetCents, expected).ToDb()));
        }

        var now = checkpoints.LastOrDefault(c => c.Month == thisMonth);
        var actualNow = now?.ActualCents ?? Actual(p, today, ledger, saved);
        var expectedNow = now?.ExpectedCents ?? (string.CompareOrdinal(thisMonth, startMonth) < 0 ? p.StartAmountCents : p.TargetCents);
        var status = p.Status == "done" ? "done" : PlanTracker.Status(actualNow, p.TargetCents, expectedNow).ToDb();
        var trouble = checkpoints.Where(c => c.Status is "wavered" or "behind").LastOrDefault() is { } t ? new PlanTrouble(t.Month, t.Status) : null;

        var monthsLeft = Math.Max(0, PlanTracker.MonthsBetween(thisMonth, targetMonth));
        var needed = PlanTracker.NeededPerMonth(p.TargetCents, actualNow, monthsLeft);
        long? neededInterest = p.RateBps is { } rate && rate > 0 ? PlanTracker.NeededPerMonthWithInterest(p.TargetCents, actualNow, monthsLeft, rate) : null;

        return new PlanView(p.Id, p.Name, p.TargetCents, p.StartOn.ToString("yyyy-MM-dd"), p.TargetOn.ToString("yyyy-MM-dd"), p.StartAmountCents, p.FundingAccountId, p.FundingAccountName,
            p.ProgressMode, p.IsTrackable, p.Status, p.Notes, actualNow, expectedNow, status, monthsLeft, needed, neededInterest, p.RateBps, trouble, checkpoints, linkedLines);
    }
}
