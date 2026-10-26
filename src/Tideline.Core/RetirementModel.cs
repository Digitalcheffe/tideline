namespace Tideline.Core;

public sealed record RetirementPerson(string Name, DateOnly? BirthOn, int? RetireAge, long SocialSecurityAnnualTodayCents);

public sealed record RetirementAccountInput(string Name, long BalanceCents, long MonthlyContributionCents, bool IsIncluded, long PensionAnnualAtRetirementCents = 0);

public sealed record RetirementAssumptions(
    long GoalIncomeAnnualTodayCents,
    long OtherIncomeAnnualTodayCents,
    int ReturnBeforeBps,
    int ReturnDuringBps,
    int InflationBps,
    int WithdrawalRateBps,
    int PlanToAge);

/// <param name="MoneyLastsYears">Null when it lasts past the plan-to age (see <see cref="LastsPastPlan"/>).</param>
public sealed record RetirementResult(
    int MonthsToRetirement,
    long ProjectedCents,
    double InflationFactor,
    long GapTodayCents,
    long GapAtRetirementCents,
    long RequiredCents,
    long DeficitCents,
    long DeficitTodayCents,
    long ExtraMonthlyNeededCents,
    double? MoneyLastsYears,
    bool LastsPastPlan);

/// <summary>
/// The retirement projection (docs/ARCHITECTURE.md). Money in cents; the growth maths is done in double and
/// rounded to the cent at the end. It is a projection, not advice, and says so wherever it is shown.
/// </summary>
public static class RetirementModel
{
    /// <summary>Whole months from <paramref name="today"/> to <paramref name="date"/>, never below zero.</summary>
    public static int WholeMonths(DateOnly today, DateOnly date)
    {
        var months = (date.Year - today.Year) * 12 + date.Month - today.Month - (date.Day < today.Day ? 1 : 0);
        return Math.Max(0, months);
    }

    /// <summary>The household retires on the latest of the people's dates. Null while nobody has a birth date and retire age.</summary>
    public static DateOnly? RetirementDate(IEnumerable<RetirementPerson> people)
    {
        DateOnly? latest = null;
        foreach (var p in people)
        {
            if (p.BirthOn is not { } born || p.RetireAge is not { } age) continue;
            var date = born.AddYears(age);
            if (latest is null || date > latest) latest = date;
        }
        return latest;
    }

    public static RetirementResult Compute(
        int monthsToRetirement,
        IReadOnlyList<RetirementAccountInput> accounts,
        long socialSecurityTotalAnnualTodayCents,
        RetirementAssumptions a)
    {
        var m = Math.Max(0, monthsToRetirement);
        var years = m / 12.0;
        var rm = a.ReturnBeforeBps / 10000.0 / 12.0;
        var growth = Math.Pow(1 + rm, m);

        double projected = 0;
        foreach (var acct in accounts.Where(x => x.IsIncluded))
            projected += acct.BalanceCents * growth + acct.MonthlyContributionCents * (rm == 0 ? m : (growth - 1) / rm);

        var pensions = accounts.Where(x => x.IsIncluded).Sum(x => x.PensionAnnualAtRetirementCents);
        var inflation = a.InflationBps / 10000.0;
        var inflationFactor = Math.Pow(1 + inflation, years);

        var gapToday = Math.Max(0, a.GoalIncomeAnnualTodayCents - socialSecurityTotalAnnualTodayCents - a.OtherIncomeAnnualTodayCents);
        var gapAtRetirement = gapToday * inflationFactor - pensions;
        var withdrawal = a.WithdrawalRateBps / 10000.0;
        var required = withdrawal > 0 ? gapAtRetirement / withdrawal : 0;
        var deficit = required - projected;
        var deficitToday = deficit / inflationFactor;
        var extra = deficit > 0 && m > 0 ? (rm == 0 ? deficit / m : deficit * rm / (growth - 1)) : 0;

        double? lasts = null;
        var lastsPast = true;
        if (gapAtRetirement > 0)
        {
            var rr = (1 + a.ReturnDuringBps / 10000.0) / (1 + inflation) - 1;
            if (rr == 0)
            {
                lasts = projected / gapAtRetirement;
                lastsPast = false;
            }
            else if (projected * rr < gapAtRetirement)
            {
                lasts = -Math.Log(1 - projected * rr / gapAtRetirement) / Math.Log(1 + rr);
                lastsPast = false;
            }
        }

        long Cents(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
        return new RetirementResult(m, Cents(projected), inflationFactor, gapToday, Cents(gapAtRetirement), Cents(required), Cents(deficit), Cents(deficitToday),
            Cents(extra), lasts, lastsPast);
    }

    /// <summary>
    /// The earliest age (up to 75) at which the last person to retire could stop and have projected savings cover what is required,
    /// with everything else left as it is. Null when there is no age up to 75 that does it, or when the inputs are not complete.
    /// </summary>
    public static int? RetireAtAge(
        DateOnly today,
        IReadOnlyList<RetirementPerson> people,
        IReadOnlyList<RetirementAccountInput> accounts,
        RetirementAssumptions a)
    {
        var withDates = people.Where(p => p.BirthOn is not null && p.RetireAge is not null).ToList();
        if (withDates.Count == 0) return null;
        var last = withDates.OrderByDescending(p => p.BirthOn!.Value.AddYears(p.RetireAge!.Value)).First();
        var ss = people.Sum(p => p.SocialSecurityAnnualTodayCents);

        for (var age = Math.Max(50, (today.Year - last.BirthOn!.Value.Year)); age <= 75; age++)
        {
            var moved = people.Select(p => p == last ? p with { RetireAge = age } : p).ToList();
            var date = RetirementDate(moved)!.Value;
            var result = Compute(WholeMonths(today, date), accounts, ss, a);
            if (result.ProjectedCents >= result.RequiredCents) return age;
        }
        return null;
    }

    /// <summary>The projected balance at the end of each year until retirement, for the chart. Year 0 is today.</summary>
    public static IReadOnlyList<(int Year, long BalanceCents)> ByYear(int monthsToRetirement, IReadOnlyList<RetirementAccountInput> accounts, int returnBeforeBps)
    {
        var rm = returnBeforeBps / 10000.0 / 12.0;
        var points = new List<(int, long)>();
        var included = accounts.Where(x => x.IsIncluded).ToList();
        for (var months = 0; ; months += 12)
        {
            var at = Math.Min(months, monthsToRetirement);
            var growth = Math.Pow(1 + rm, at);
            var total = included.Sum(x => x.BalanceCents * growth + x.MonthlyContributionCents * (rm == 0 ? at : (growth - 1) / rm));
            points.Add((months / 12, (long)Math.Round(total, MidpointRounding.AwayFromZero)));
            if (months >= monthsToRetirement) break;
        }
        return points;
    }
}
