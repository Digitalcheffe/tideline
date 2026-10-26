namespace Tideline.Core;

public enum PlanStatus { OnTrack, Wavered, Behind, Done }

/// <summary>Plans (docs/ARCHITECTURE.md): where a plan should be by a month, and whether it is there.</summary>
public static class PlanTracker
{
    /// <summary>Linear from the start amount to the target. <paramref name="elapsedMonths"/> and <paramref name="totalMonths"/> are whole months.</summary>
    public static long Expected(long startCents, long targetCents, int elapsedMonths, int totalMonths)
    {
        if (totalMonths <= 0 || elapsedMonths >= totalMonths) return targetCents;
        if (elapsedMonths <= 0) return startCents;
        return startCents + (long)Math.Round((decimal)(targetCents - startCents) * elapsedMonths / totalMonths, MidpointRounding.AwayFromZero);
    }

    /// <summary>Done at the target; on track at or above expected; wavered within 5% of expected; otherwise behind.</summary>
    public static PlanStatus Status(long actualCents, long targetCents, long expectedCents)
    {
        if (actualCents >= targetCents) return PlanStatus.Done;
        if (actualCents >= expectedCents) return PlanStatus.OnTrack;
        return actualCents * 100 >= expectedCents * 95 ? PlanStatus.Wavered : PlanStatus.Behind;
    }

    /// <summary>What has to go in each remaining month to finish on time, with no interest assumed.</summary>
    public static long NeededPerMonth(long targetCents, long actualCents, int monthsLeft)
    {
        var remaining = Math.Max(0, targetCents - actualCents);
        return monthsLeft <= 0 ? remaining : (long)Math.Ceiling(remaining / (double)monthsLeft);
    }

    /// <summary>The same, when the account earns <paramref name="rateBps"/> a year: the payment that grows the balance to the target.</summary>
    public static long NeededPerMonthWithInterest(long targetCents, long actualCents, int monthsLeft, int rateBps)
    {
        if (monthsLeft <= 0) return Math.Max(0, targetCents - actualCents);
        var r = rateBps / 10000.0 / 12.0;
        if (r == 0) return NeededPerMonth(targetCents, actualCents, monthsLeft);
        var growth = Math.Pow(1 + r, monthsLeft);
        var needed = (targetCents - actualCents * growth) * r / (growth - 1);
        return Math.Max(0, (long)Math.Ceiling(needed));
    }

    /// <summary>Whole months from the first of one 'YYYY-MM' to the first of another; negative when it is earlier.</summary>
    public static int MonthsBetween(string from, string to) =>
        (int.Parse(to[..4]) - int.Parse(from[..4])) * 12 + int.Parse(to[5..7]) - int.Parse(from[5..7]);

    public static string ToDb(this PlanStatus s) => s switch
    {
        PlanStatus.OnTrack => "on_track",
        PlanStatus.Wavered => "wavered",
        PlanStatus.Behind => "behind",
        _ => "done",
    };
}

/// <summary>Budget guidelines (docs/ARCHITECTURE.md).</summary>
public static class BudgetRules
{
    /// <summary>A monthly guideline pops when month-to-date spend is over it by the percent AND by the minimum amount.</summary>
    public static bool MonthlyPopped(long spentCents, long monthlyCents, int popPercent, long popMinCents) =>
        monthlyCents > 0 && spentCents * 100 > monthlyCents * (100 + popPercent) && spentCents > monthlyCents + popMinCents;

    public static bool AnnualPopped(long spentYearToDateCents, long annualCents) => annualCents > 0 && spentYearToDateCents > annualCents;

    /// <summary>After June, actual income under 90% of what the year's expected amount would give so far.</summary>
    public static bool IncomeShort(long actualYtdCents, long expectedAnnualCents, int elapsedMonths) =>
        elapsedMonths > 6 && expectedAnnualCents > 0 && actualYtdCents * 12 * 10 < expectedAnnualCents * elapsedMonths * 9;

    /// <summary>The average month over twelve, rounded up to a whole ten dollars.</summary>
    public static long ProposedMonthly(long twelveMonthSpendCents)
    {
        if (twelveMonthSpendCents <= 0) return 0;
        var average = (twelveMonthSpendCents + 11) / 12; // never rounds down a cent
        return (average + 999) / 1000 * 1000;
    }
}
