namespace Tideline.Core;

/// <summary>Credit card "paid in full" (docs/ARCHITECTURE.md).</summary>
public static class CardCheck
{
    /// <summary>
    /// Calendar-month form, used until a statement day is entered: the balance at month end (negative = owed)
    /// must be covered by the payments made from the next month's first day to its due day.
    /// </summary>
    public static bool PaidInFull(long monthEndBalanceCents, long paymentsAfterMonthEndCents) =>
        monthEndBalanceCents >= 0 || paymentsAfterMonthEndCents >= -monthEndBalanceCents;

    /// <summary>The last day payments count for a month: the next month's due day, clipped to its length.</summary>
    public static DateOnly PaymentDeadline(int year, int month, int dueDay)
    {
        var next = new DateOnly(year, month, 1).AddMonths(1);
        return new DateOnly(next.Year, next.Month, Math.Min(dueDay, DateTime.DaysInMonth(next.Year, next.Month)));
    }
}
