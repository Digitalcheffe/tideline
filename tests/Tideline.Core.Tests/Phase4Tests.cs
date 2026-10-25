using FluentAssertions;
using Tideline.Core;

namespace Tideline.Core.Tests;

public class DebtCountdownTests
{
    [Fact]
    public void Test_vector_one_twenty_thousand_at_five_percent_paying_569()
    {
        var p = DebtCountdown.Compute(2_000_000, 500, 56_900, "2026-10");

        p.PaysOff.Should().BeTrue();
        Math.Round(p.MonthsLeft, 2).Should().Be(38.09);
        p.InterestLeftCents.Should().Be(167_038);
        p.PayoffMonth.Should().Be("2030-01", "this month plus 39, the whole months it takes");
    }

    [Fact]
    public void Test_vector_two_with_669_a_month()
    {
        var p = DebtCountdown.Compute(2_000_000, 500, 66_900, "2026-10");

        Math.Round(p.MonthsLeft, 2).Should().Be(31.99);
        p.InterestLeftCents.Should().Be(140_429);
    }

    [Fact]
    public void An_extra_hundred_a_month_saves_months_and_interest()
    {
        var effect = DebtCountdown.WithExtra(2_000_000, 500, 56_900, 10_000, "2026-10");

        Math.Round(effect.MonthsSaved, 2).Should().Be(6.09);
        effect.InterestSavedCents.Should().Be(167_038 - 140_429);
    }

    [Fact]
    public void A_zero_rate_loan_is_just_balance_over_payment()
    {
        var p = DebtCountdown.Compute(120_000, 0, 10_000, "2026-12");

        p.MonthsLeft.Should().Be(12);
        p.InterestLeftCents.Should().Be(0);
        p.PayoffMonth.Should().Be("2027-12");
    }

    [Fact]
    public void A_payment_that_does_not_cover_the_interest_never_pays_off()
    {
        var p = DebtCountdown.Compute(10_000_000, 600, 40_000, "2026-10"); // interest alone is $500 a month

        p.PaysOff.Should().BeFalse();
        p.PayoffMonth.Should().BeNull();
    }

    [Fact]
    public void The_next_balance_is_balance_times_one_plus_rate_minus_payment()
    {
        // 20,000.00 at 5%: interest 83.33, payment 569.00
        DebtCountdown.NextBalance(2_000_000, 500, 56_900).Should().Be(1_951_433);
        DebtCountdown.NextBalance(10_000, 500, 56_900).Should().Be(0, "a loan cannot go below nothing owed");
    }

    [Theory]
    [InlineData("2026-10", 3, "2027-01")]
    [InlineData("2026-12", 1, "2027-01")]
    [InlineData("2026-01", 24, "2028-01")]
    [InlineData("2026-04", -6, "2025-10")]
    [InlineData("2026-01", -1, "2025-12")]
    [InlineData("2026-03", -36, "2023-03")]
    public void Months_add_across_years(string start, int add, string expected) => DebtCountdown.AddMonths(start, add).Should().Be(expected);
}

public class RetirementModelTests
{
    private static readonly RetirementAssumptions Example1 = new(8_000_000, 0, 600, 400, 300, 400, 95);
    private static readonly RetirementAccountInput[] Accounts = [new("401k", 10_000_000, 150_000, true)];

    [Fact]
    public void Example1_matches_the_spec_to_the_cent()
    {
        var r = RetirementModel.Compute(312, Accounts, 4_000_000, Example1);

        r.ProjectedCents.Should().Be(159_614_375);
        r.InflationFactor.Should().BeApproximately(2.156591, 0.000001);
        r.GapAtRetirementCents.Should().Be(8_626_365);
        r.RequiredCents.Should().Be(215_659_127);
        r.DeficitCents.Should().Be(56_044_752);
        r.DeficitTodayCents.Should().Be(25_987_656);
        r.ExtraMonthlyNeededCents.Should().Be(74_919);
        Math.Round(r.MoneyLastsYears!.Value, 2).Should().Be(20.49);
        r.LastsPastPlan.Should().BeFalse();
    }

    [Fact]
    public void A_surplus_is_a_negative_deficit_and_needs_nothing_extra()
    {
        var rich = new[] { new RetirementAccountInput("401k", 500_000_000, 150_000, true) };

        var r = RetirementModel.Compute(312, rich, 4_000_000, Example1);

        r.DeficitCents.Should().BeNegative();
        r.ExtraMonthlyNeededCents.Should().Be(0);
        r.LastsPastPlan.Should().BeTrue();
        r.MoneyLastsYears.Should().BeNull();
    }

    [Fact]
    public void Accounts_left_out_of_the_goal_are_not_counted()
    {
        var accounts = new[] { new RetirementAccountInput("401k", 10_000_000, 150_000, true), new RetirementAccountInput("HSA", 99_999_999, 0, false) };

        RetirementModel.Compute(312, accounts, 4_000_000, Example1).ProjectedCents.Should().Be(159_614_375);
    }

    [Fact]
    public void A_pension_lowers_what_has_to_come_from_savings()
    {
        var withPension = new[] { new RetirementAccountInput("401k", 10_000_000, 150_000, true), new RetirementAccountInput("Pension", 0, 0, true, 1_000_000) };

        var r = RetirementModel.Compute(312, withPension, 4_000_000, Example1);

        r.GapAtRetirementCents.Should().Be(8_626_365 - 1_000_000);
    }

    [Fact]
    public void Retirement_date_is_the_latest_of_the_people()
    {
        var people = new[]
        {
            new RetirementPerson("A", new DateOnly(1980, 5, 1), 65, 0),
            new RetirementPerson("B", new DateOnly(1982, 3, 1), 65, 0),
            new RetirementPerson("C", null, null, 0),
        };

        RetirementModel.RetirementDate(people).Should().Be(new DateOnly(2047, 3, 1));
        RetirementModel.RetirementDate([people[2]]).Should().BeNull();
    }

    [Fact]
    public void Whole_months_stop_at_the_day()
    {
        RetirementModel.WholeMonths(new DateOnly(2026, 10, 25), new DateOnly(2027, 10, 25)).Should().Be(12);
        RetirementModel.WholeMonths(new DateOnly(2026, 10, 25), new DateOnly(2027, 10, 24)).Should().Be(11);
        RetirementModel.WholeMonths(new DateOnly(2026, 10, 25), new DateOnly(2020, 1, 1)).Should().Be(0);
    }

    [Fact]
    public void Working_longer_closes_the_gap_and_the_first_age_that_does_is_found()
    {
        var today = new DateOnly(2026, 10, 25);
        var people = new[] { new RetirementPerson("Alex", today.AddYears(-40), 62, 4_000_000) };

        var age = RetirementModel.RetireAtAge(today, people, Accounts, Example1);

        age.Should().NotBeNull().And.BeGreaterThan(62).And.BeLessThanOrEqualTo(75);
        // one year younger than the answer is not enough; the answer is
        Check(age!.Value).Should().BeTrue();
        Check(age.Value - 1).Should().BeFalse();

        bool Check(int a)
        {
            var moved = new[] { people[0] with { RetireAge = a } };
            var r = RetirementModel.Compute(RetirementModel.WholeMonths(today, RetirementModel.RetirementDate(moved)!.Value), Accounts, 4_000_000, Example1);
            return r.ProjectedCents >= r.RequiredCents;
        }
    }

    [Fact]
    public void The_chart_starts_with_what_you_have_and_ends_at_the_projection()
    {
        var points = RetirementModel.ByYear(312, Accounts, 600);

        points[0].BalanceCents.Should().Be(10_000_000);
        points[^1].BalanceCents.Should().Be(159_614_375);
        points.Select(p => p.BalanceCents).Should().BeInAscendingOrder();
    }
}

public class PlanTrackerTests
{
    [Fact]
    public void Expected_is_a_straight_line_from_start_to_target()
    {
        PlanTracker.Expected(0, 1_200_000, 0, 12).Should().Be(0);
        PlanTracker.Expected(0, 1_200_000, 3, 12).Should().Be(300_000);
        PlanTracker.Expected(200_000, 1_200_000, 6, 12).Should().Be(700_000);
        PlanTracker.Expected(0, 1_200_000, 12, 12).Should().Be(1_200_000);
        PlanTracker.Expected(0, 1_200_000, 20, 12).Should().Be(1_200_000);
    }

    [Theory]
    [InlineData(300_000, PlanStatus.OnTrack)]
    [InlineData(350_000, PlanStatus.OnTrack)]
    [InlineData(285_000, PlanStatus.Wavered)] // exactly 95% of expected
    [InlineData(284_999, PlanStatus.Behind)]
    [InlineData(1_200_000, PlanStatus.Done)]
    public void Status_is_on_track_wavered_behind_or_done(long actual, PlanStatus expected) =>
        PlanTracker.Status(actual, 1_200_000, 300_000).Should().Be(expected);

    [Fact]
    public void Needed_per_month_spreads_what_is_left_over_the_months_left()
    {
        PlanTracker.NeededPerMonth(1_200_000, 300_000, 9).Should().Be(100_000);
        PlanTracker.NeededPerMonth(1_200_000, 1_300_000, 9).Should().Be(0);
        PlanTracker.NeededPerMonth(1_200_000, 300_000, 0).Should().Be(900_000, "past the date it all is due now");
    }

    [Fact]
    public void Interest_lowers_what_has_to_go_in_each_month()
    {
        var plain = PlanTracker.NeededPerMonth(1_200_000, 300_000, 9);
        var withInterest = PlanTracker.NeededPerMonthWithInterest(1_200_000, 300_000, 9, 480);

        withInterest.Should().BeLessThan(plain).And.BeGreaterThan(95_000);
        PlanTracker.NeededPerMonthWithInterest(1_200_000, 300_000, 9, 0).Should().Be(plain);
    }

    [Fact]
    public void Months_between_counts_across_years() => PlanTracker.MonthsBetween("2026-10", "2028-01").Should().Be(15);
}

public class BudgetRulesTests
{
    [Theory]
    [InlineData(55_000, false)] // exactly 10% over is not past it
    [InlineData(55_001, true)] // past 10% and past the $25 minimum
    [InlineData(52_500, false)]
    public void A_monthly_guideline_of_500_dollars_pops_just_over_550(long spent, bool popped) =>
        BudgetRules.MonthlyPopped(spent, 50_000, 10, 2_500).Should().Be(popped);

    [Fact]
    public void A_small_guideline_does_not_pop_on_a_few_dollars()
    {
        // $20 guideline, 10%: $23 is over by more than 10% but not by $25
        BudgetRules.MonthlyPopped(2_300, 2_000, 10, 2_500).Should().BeFalse();
        BudgetRules.MonthlyPopped(5_000, 2_000, 10, 2_500).Should().BeTrue();
    }

    [Fact]
    public void An_annual_guideline_pops_once_year_to_date_is_over()
    {
        BudgetRules.AnnualPopped(120_000, 120_000).Should().BeFalse();
        BudgetRules.AnnualPopped(120_001, 120_000).Should().BeTrue();
        BudgetRules.AnnualPopped(5_000, 0).Should().BeFalse("no guideline, no flag");
    }

    [Fact]
    public void Income_is_short_only_after_june_and_when_under_ninety_percent_of_the_year_so_far()
    {
        // expected 120,000 a year; after 8 months the pace is 80,000; 90% of that is 72,000
        BudgetRules.IncomeShort(7_199_999, 12_000_000, 8).Should().BeTrue();
        BudgetRules.IncomeShort(7_200_000, 12_000_000, 8).Should().BeFalse();
        BudgetRules.IncomeShort(1, 12_000_000, 6).Should().BeFalse("not before July");
        BudgetRules.IncomeShort(0, 0, 9).Should().BeFalse("no expected income, no flag");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120_000, 10_000)] // exactly $100 a month: already on a ten
    [InlineData(120_001, 11_000)] // a cent over goes up to $110
    [InlineData(1_000_000, 84_000)] // $10,000 a year is $833.33 a month, up to $840
    public void Proposed_guidelines_round_up_to_ten_dollars(long twelveMonths, long expectedMonthly) =>
        BudgetRules.ProposedMonthly(twelveMonths).Should().Be(expectedMonthly);
}
