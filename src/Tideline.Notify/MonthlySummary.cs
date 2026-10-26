using System.Data;
using System.Globalization;
using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Reports;

namespace Tideline.Notify;

public sealed record SummaryMessage(string Month, string Title, string Body, string LinkPath, int NeedsYou);

/// <summary>The all-clear for a month that has just ended (docs/ARCHITECTURE.md). Plain words, no dashes.</summary>
public static class MonthlySummary
{
    public static SummaryMessage Build(IDbConnection conn, string month, DateOnly today, TimeProvider clock)
    {
        TypeHandlers.EnsureRegistered();
        var first = EvalContext.FirstOf(month);
        var last = EvalContext.LastOf(month);
        var label = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        var spread = new ReportRepository(conn).SpreadFor(first, last);

        // bills: how many that were expected were paid
        var paid = new ReportRepository(conn).RecurringPaid(first.AddMonths(-4), last).ToLookup(p => p.RecurringId);
        int expected = 0, paidCount = 0;
        foreach (var r in new RecurringRepository(conn).All().Where(r => r.CategoryType == LineType.Bill))
        {
            var byMonth = paid[r.Item.Id].ToDictionary(p => p.Month, p => p.Cents);
            var history = byMonth.Where(kv => string.CompareOrdinal(kv.Key, month) < 0).OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            var cell = RecurringStatus.Evaluate(r.Item, first.Year, first.Month, byMonth.GetValueOrDefault(month), today, history);
            if (cell.Status is CellStatus.Paid or CellStatus.PaidUnusual) { expected++; paidCount++; }
            else if (cell.Status is CellStatus.Missing or CellStatus.Due) expected++;
        }

        // cards that should be paid in full, once their deadline has passed
        var ledger = new LedgerRepository(conn, clock);
        var reports = new ReportRepository(conn);
        var terms = new CardTermsRepository(conn).All();
        int cardsChecked = 0, cardsLate = 0;
        foreach (var id in conn.Query<long>("SELECT id FROM accounts WHERE kind = 'credit_card' AND is_closed = 0"))
        {
            if (!terms.TryGetValue(id, out var t) || !t.IsPayInFull) continue;
            var deadline = CardCheck.PaymentDeadline(first.Year, first.Month, t.DueDay ?? 31);
            if (deadline > today) continue;
            cardsChecked++;
            if (!CardCheck.PaidInFull(ledger.BalanceAsOf(id, last), reports.CardPayments(id, last.AddDays(1), deadline))) cardsLate++;
        }

        var newDebt = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM flags WHERE kind = 'new_debt' AND period = @month", new { month });
        var plans = new PlanReader(conn).Read(today).Where(p => p.IsTrackable && p.Lifecycle == "active").ToList();
        var offTrack = plans.Count(p => p.Status is "wavered" or "behind");

        var open = new FlagRepository(conn, clock).Open();
        var pressing = open.Where(f => f.Severity is "alert" or "warn").ToList();
        var review = open.Where(f => f.Kind == FlagKinds.ReviewNeeded).Sum(f => ReviewCount(f));
        var oneTime = open.Count(f => f.Kind == FlagKinds.OneTimeReview);

        var title = pressing.Count == 0 ? $"{label}: all clear." : $"{label}: {pressing.Count} thing{(pressing.Count == 1 ? " needs" : "s need")} you";
        var lines = new List<string>();
        foreach (var f in pressing) lines.Add("- " + FlagText.Split(f.Message).Title);

        lines.Add($"In {Text.Dollars(spread.InCents)} · Bills {Text.Dollars(spread.BillsCents)} · Required {Text.Dollars(spread.RequiredCents)} · Extra {Text.Dollars(spread.ExtraCents)} · One-time {Text.Dollars(spread.OneTimeCents)} · Savings {Text.Dollars(spread.SavingsCents)}");

        var facts = new List<string> { expected == 0 ? "no bills expected" : $"{paidCount} of {expected} bills paid" };
        if (cardsChecked > 0) facts.Add(cardsLate == 0 ? (cardsChecked == 1 ? "card paid in full" : "cards paid in full") : $"{cardsLate} card{(cardsLate == 1 ? "" : "s")} not paid in full");
        facts.Add(newDebt == 0 ? "no new debt" : $"{newDebt} possible new debt");
        if (plans.Count > 0) facts.Add(offTrack == 0 ? "plans on track" : $"{offTrack} plan{(offTrack == 1 ? "" : "s")} off track");
        lines.Add(string.Join(" · ", facts));

        var look = new List<string>();
        if (review > 0) look.Add($"{review} line{(review == 1 ? "" : "s")} tagged review");
        if (oneTime > 0) look.Add($"{oneTime} one-time item{(oneTime == 1 ? "" : "s")}");
        if (look.Count > 0) lines.Add("Needs a look: " + string.Join(", ", look) + ".");

        return new SummaryMessage(month, title, string.Join("\n", lines), $"/?from={month}-01&to={month}-{last.Day:00}", pressing.Count);
    }

    private static long ReviewCount(FlagRow f) => long.TryParse(FlagText.Split(f.Message).Title.Split(' ')[0], out var n) ? n : 1;
}
