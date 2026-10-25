namespace Tideline.Core;

public enum Frequency { Monthly, Yearly }

public sealed record RecurringItem(
    long Id,
    string Name,
    long CategoryId,
    long ExpectedMinCents,
    long ExpectedMaxCents,
    Frequency Frequency,
    int EveryN,
    int DueDay,
    DateOnly FirstDueOn,
    int GraceDays = 5,
    bool IsActive = true,
    DateOnly? EndedOn = null);

public enum CellStatus { NotExpected, Paid, PaidUnusual, PaidExtra, Due, Missing, Projected }

/// <param name="PaidCents">Spend in the month linked to the item (positive = paid out).</param>
/// <param name="ProjectedCents">Only for <see cref="CellStatus.Projected"/>.</param>
public sealed record RecurringCell(CellStatus Status, long PaidCents, long ProjectedCents)
{
    /// <summary>What the grid adds up for this cell: paid, plus projected for future months.</summary>
    public long GridCents => PaidCents + ProjectedCents;
}

/// <summary>Bills grid and subscriptions (docs/04-domain-rules.md, "Recurring status").</summary>
public static class RecurringStatus
{
    private static int Ym(int year, int month) => year * 12 + month;

    public static bool IsExpected(RecurringItem r, int year, int month)
    {
        if (!r.IsActive) return false;
        if (r.EndedOn is { } ended && Ym(year, month) > Ym(ended.Year, ended.Month)) return false;

        var monthsSinceFirst = (year - r.FirstDueOn.Year) * 12 + (month - r.FirstDueOn.Month);
        if (monthsSinceFirst < 0) return false;

        return r.Frequency == Frequency.Yearly
            ? month == r.FirstDueOn.Month
            : monthsSinceFirst % Math.Max(1, r.EveryN) == 0;
    }

    /// <summary><c>due_day</c> clipped to the month's last day.</summary>
    public static DateOnly DueDate(RecurringItem r, int year, int month) =>
        new(year, month, Math.Min(r.DueDay, DateTime.DaysInMonth(year, month)));

    /// <param name="paidCents">Positive spend linked to the item in this month.</param>
    /// <param name="lastPaidMonths">The most recent paid months (up to 3), used for a projection.</param>
    public static RecurringCell Evaluate(RecurringItem r, int year, int month, long paidCents, DateOnly today, IReadOnlyList<long> lastPaidMonths)
    {
        var expected = IsExpected(r, year, month);

        if (paidCents > 0)
        {
            if (!expected) return new RecurringCell(CellStatus.PaidExtra, paidCents, 0);
            var inRange = paidCents >= r.ExpectedMinCents && paidCents <= r.ExpectedMaxCents;
            return new RecurringCell(inRange ? CellStatus.Paid : CellStatus.PaidUnusual, paidCents, 0);
        }

        if (!expected) return new RecurringCell(CellStatus.NotExpected, 0, 0);

        if (Ym(year, month) > Ym(today.Year, today.Month))
        {
            var recent = lastPaidMonths.Where(c => c > 0).TakeLast(3).ToList();
            var projected = recent.Count > 0
                ? (long)Math.Round(recent.Average(), MidpointRounding.AwayFromZero)
                : (r.ExpectedMinCents + r.ExpectedMaxCents) / 2;
            return new RecurringCell(CellStatus.Projected, 0, projected);
        }

        var lastOkDay = DueDate(r, year, month).AddDays(r.GraceDays);
        return new RecurringCell(today <= lastOkDay ? CellStatus.Due : CellStatus.Missing, 0, 0);
    }
}
