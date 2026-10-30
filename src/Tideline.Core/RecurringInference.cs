namespace Tideline.Core;

/// <summary>What a run of payments to one payee looks like as a recurring item: how often, what range, what day.</summary>
public sealed record RecurringProposal(string Frequency, int EveryN, long MinCents, long MaxCents, int DueDay, DateOnly FirstDueOn, int Occurrences, DateOnly LastPaid)
{
    /// <summary>What it costs a month, about: the middle of the range, spread over the months between payments (a yearly one over twelve).</summary>
    public long MonthlyCents => (MinCents + MaxCents) / 2 / (Frequency == "yearly" ? 12 : Math.Max(1, EveryN));
}

/// <summary>
/// Works out whether payments to one payee are a bill or a subscription, from the dates and amounts alone: monthly, every 2, 3 or 6 months, or yearly.
/// It is deliberately careful. It needs at least three paid months (two for a yearly one a year apart), an even rhythm, amounts that stay close
/// to each other, and a payment recent enough that it is still going. Weekly or irregular spending never qualifies. Pure: no database.
/// </summary>
public static class RecurringInference
{
    public const int WindowMonths = 26; // wide enough for two yearly payments
    public const int RecentPayments = 6;
    public const double MaxSpread = 1.5;
    public const double Margin = 0.10;

    private static int Month(DateOnly d) => d.Year * 12 + d.Month - 1;

    public static RecurringProposal? Infer(IReadOnlyList<(DateOnly On, long Cents)> payments, DateOnly today)
    {
        var since = today.AddMonths(-WindowMonths);
        var months = payments.Where(p => p.On >= since && p.Cents > 0)
            .GroupBy(p => Month(p.On))
            .OrderBy(g => g.Key)
            .Select(g => (Month: g.Key, Cents: g.Sum(p => p.Cents), First: g.OrderBy(p => p.On).First().On))
            .ToList();
        if (months.Count < 2) return null;

        var gaps = months.Zip(months.Skip(1), (a, b) => b.Month - a.Month).ToList();
        var gap = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2); // the usual number of months between payments
        if (gap is not (1 or 2 or 3 or 6 or 12)) return null;
        if (gap == 12 ? months.Count < 2 : months.Count < 3) return null;

        // an even rhythm: most gaps are the usual one (a monthly bill may skip a month)
        var even = gaps.Count(g => g == gap || (gap == 1 && g == 2));
        if (even < Math.Ceiling(gaps.Count * 0.7)) return null;

        // still going: the last payment is not much further back than the usual gap
        if (Month(today) - months[^1].Month > gap + 1) return null;

        // amounts stay close to each other: most of the recent months are within half again of the middle one (a double payment is left out)
        var recent = months.TakeLast(RecentPayments).Select(m => m.Cents).ToList();
        var middle = recent.OrderBy(c => c).ElementAt(recent.Count / 2);
        var usual = recent.Where(c => c * MaxSpread >= middle && c <= middle * MaxSpread).ToList();
        if (middle <= 0 || usual.Count < Math.Ceiling(recent.Count * 0.7)) return null;
        var low = usual.Min();
        var high = usual.Max();

        var dueDay = months.Select(m => m.First.Day).OrderBy(d => d).ElementAt(months.Count / 2);
        var first = months[0].First;
        var firstDue = new DateOnly(first.Year, first.Month, Math.Min(dueDay, DateTime.DaysInMonth(first.Year, first.Month)));

        return new RecurringProposal(
            gap == 12 ? "yearly" : "monthly", gap == 12 ? 1 : gap,
            (long)Math.Floor(low * (1 - Margin)), (long)Math.Ceiling(high * (1 + Margin)),
            dueDay, firstDue, months.Count, months[^1].First);
    }
}
