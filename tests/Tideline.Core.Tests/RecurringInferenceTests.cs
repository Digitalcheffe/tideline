using FluentAssertions;
using Tideline.Core;

namespace Tideline.Core.Tests;

public class RecurringInferenceTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static List<(DateOnly, long)> On(long cents, params string[] days) => days.Select(d => (DateOnly.ParseExact(d, "yyyy-MM-dd"), cents)).ToList();

    [Fact]
    public void A_payment_on_about_the_same_day_each_month_is_a_monthly_bill_with_a_range_around_what_was_paid()
    {
        var p = RecurringInference.Infer(On(14_218, "2026-05-08", "2026-06-08", "2026-07-09", "2026-08-08", "2026-09-08"), Today)!;

        p.Frequency.Should().Be("monthly");
        p.EveryN.Should().Be(1);
        p.DueDay.Should().Be(8, "the middle day it was paid on");
        p.MinCents.Should().Be(12_796, "10% under $142.18, rounded down");
        p.MaxCents.Should().Be(15_640, "10% over, rounded up");
        p.Occurrences.Should().Be(5);
        p.FirstDueOn.Should().Be(new DateOnly(2026, 5, 8));
        p.LastPaid.Should().Be(new DateOnly(2026, 9, 8));
        p.MonthlyCents.Should().Be(14_218);
    }

    [Theory]
    [InlineData(2, new[] { "2026-01-10", "2026-03-10", "2026-05-10", "2026-07-10", "2026-09-10" })]
    [InlineData(3, new[] { "2025-10-15", "2026-01-15", "2026-04-15", "2026-07-15" })]
    [InlineData(6, new[] { "2025-01-05", "2025-07-05", "2026-01-05", "2026-07-05" })]
    public void Every_two_three_or_six_months_is_found_and_costs_a_share_of_each_month(int every, string[] days)
    {
        var p = RecurringInference.Infer(On(60_000, days), Today)!;

        p.Frequency.Should().Be("monthly");
        p.EveryN.Should().Be(every);
        p.MonthlyCents.Should().BeInRange(60_000 / every - 1_000, 60_000 / every + 1_000);
    }

    [Fact]
    public void Two_payments_a_year_apart_are_a_yearly_one_and_cost_a_twelfth_a_month()
    {
        var p = RecurringInference.Infer(On(120_000, "2025-04-15", "2026-04-15"), Today);

        p!.Frequency.Should().Be("yearly");
        p.EveryN.Should().Be(1);
        p.MonthlyCents.Should().Be(10_000);
    }

    [Fact]
    public void A_variable_bill_stays_a_bill_but_wild_amounts_are_not_one()
    {
        RecurringInference.Infer(new List<(DateOnly, long)>
        {
            (new DateOnly(2026, 7, 3), 13_300), (new DateOnly(2026, 8, 3), 17_100), (new DateOnly(2026, 9, 3), 15_500),
        }, Today).Should().NotBeNull("electric moves about a quarter between months");

        RecurringInference.Infer(new List<(DateOnly, long)>
        {
            (new DateOnly(2026, 7, 3), 3_000), (new DateOnly(2026, 8, 3), 21_000), (new DateOnly(2026, 9, 3), 9_000),
        }, Today).Should().BeNull("spending that swings from $30 to $210 is not a bill");
    }

    [Fact]
    public void Too_few_payments_a_ragged_rhythm_weekly_spending_and_a_payment_long_stopped_are_not_found()
    {
        RecurringInference.Infer(On(5_000, "2026-08-08", "2026-09-08"), Today).Should().BeNull("two monthly payments are not a pattern");
        RecurringInference.Infer(On(5_000, "2026-01-08", "2026-02-08", "2026-06-08", "2026-09-08"), Today).Should().BeNull("gaps of 1, 4 and 3 months");
        RecurringInference.Infer(On(5_000, "2026-09-01", "2026-09-08", "2026-09-15", "2026-09-22", "2026-09-29"), Today).Should().BeNull("weekly lands in one month");
        RecurringInference.Infer(On(5_000, "2026-01-08", "2026-02-08", "2026-03-08"), Today).Should().BeNull("last paid in March, so it has stopped");
    }

    [Fact]
    public void A_monthly_bill_may_skip_a_month_and_two_payments_in_one_month_count_as_one()
    {
        var p = RecurringInference.Infer(On(7_999, "2026-04-09", "2026-05-09", "2026-07-09", "2026-08-09", "2026-09-09", "2026-09-20"), Today);

        p.Should().NotBeNull();
        p!.Occurrences.Should().Be(5, "September's two payments are one month");
    }
}
