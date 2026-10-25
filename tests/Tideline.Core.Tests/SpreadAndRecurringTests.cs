using FluentAssertions;
using Tideline.Core;

namespace Tideline.Core.Tests;

public class SpreadTests
{
    [Fact]
    public void Report_sign_is_income_as_is_and_everything_else_as_spend()
    {
        Reporting.ReportCents(LineType.Income, 200000).Should().Be(200000);
        Reporting.ReportCents(LineType.Bill, -12000).Should().Be(12000);
        Reporting.ReportCents(LineType.Extra, 450).Should().Be(-450, "a refund reduces the type's total");
        Reporting.ReportSign(LineType.Income).Should().Be(1);
        Reporting.ReportSign(LineType.OneTime).Should().Be(-1);
    }

    [Fact]
    public void In_equals_every_bucket_plus_left_over_to_the_cent()
    {
        var spread = Spread.From([
            (LineType.Income, 500000), (LineType.Income, 12345),
            (LineType.Bill, 130099), (LineType.Required, 58540), (LineType.Extra, 2325),
            (LineType.OneTime, 9999), (LineType.Savings, 30000), (LineType.Extra, -500),
        ]);

        spread.InCents.Should().Be(512345);
        spread.ExtraCents.Should().Be(1825);
        spread.OutCents.Should().Be(130099 + 58540 + 1825 + 9999 + 30000);
        (spread.BillsCents + spread.RequiredCents + spread.ExtraCents + spread.OneTimeCents + spread.SavingsCents + spread.LeftOverCents)
            .Should().Be(spread.InCents);
        spread.For(LineType.Required).Should().Be(58540);
    }

    [Fact]
    public void Spending_more_than_comes_in_gives_a_negative_left_over() =>
        Spread.From([(LineType.Income, 1000), (LineType.Extra, 1500)]).LeftOverCents.Should().Be(-500);
}

public class RecurringStatusTests
{
    private static RecurringItem Item(Frequency f = Frequency.Monthly, int every = 1, string first = "2026-01-05", string? ended = null,
        bool active = true, long min = 10000, long max = 12000, int due = 5, int grace = 5) =>
        new(1, "City Power", 1, min, max, f, every, due, DateOnly.Parse(first), grace, active, ended is null ? null : DateOnly.Parse(ended));

    private static readonly DateOnly Today = new(2026, 5, 10);

    [Theory]
    [InlineData(2026, 1, true)]
    [InlineData(2026, 4, true)]
    [InlineData(2025, 12, false)]   // before the first due date
    public void Monthly_items_are_expected_every_month_from_the_first_due_date(int y, int m, bool expected) =>
        RecurringStatus.IsExpected(Item(), y, m).Should().Be(expected);

    [Theory]
    [InlineData(2026, 1, true)]
    [InlineData(2026, 2, false)]
    [InlineData(2026, 3, true)]
    [InlineData(2026, 5, true)]
    [InlineData(2026, 6, false)]
    public void Every_other_month_items_skip_the_off_months(int y, int m, bool expected) =>
        RecurringStatus.IsExpected(Item(every: 2), y, m).Should().Be(expected);

    [Theory]
    [InlineData(2026, 1, true)]
    [InlineData(2026, 7, false)]
    [InlineData(2027, 1, true)]
    [InlineData(2025, 1, false)]
    public void Yearly_items_land_in_their_calendar_month(int y, int m, bool expected) =>
        RecurringStatus.IsExpected(Item(Frequency.Yearly), y, m).Should().Be(expected);

    [Fact]
    public void Ended_and_inactive_items_are_not_expected()
    {
        RecurringStatus.IsExpected(Item(ended: "2026-03-15"), 2026, 3).Should().BeTrue("the month it ended in still counts");
        RecurringStatus.IsExpected(Item(ended: "2026-03-15"), 2026, 4).Should().BeFalse();
        RecurringStatus.IsExpected(Item(active: false), 2026, 3).Should().BeFalse();
    }

    [Fact]
    public void Due_date_is_clipped_to_the_end_of_a_short_month() =>
        RecurringStatus.DueDate(Item(due: 31), 2026, 2).Should().Be(new DateOnly(2026, 2, 28));

    [Theory]
    [InlineData(11000, CellStatus.Paid)]
    [InlineData(10000, CellStatus.Paid)]
    [InlineData(12000, CellStatus.Paid)]
    [InlineData(9999, CellStatus.PaidUnusual)]
    [InlineData(12001, CellStatus.PaidUnusual)]
    public void Expected_and_paid_is_paid_or_unusual_by_the_range(long paid, CellStatus status) =>
        RecurringStatus.Evaluate(Item(), 2026, 3, paid, Today, []).Status.Should().Be(status);

    [Fact]
    public void Not_expected_but_paid_is_paid_extra_and_not_expected_unpaid_is_blank()
    {
        RecurringStatus.Evaluate(Item(every: 2), 2026, 2, 5000, Today, []).Status.Should().Be(CellStatus.PaidExtra);
        RecurringStatus.Evaluate(Item(every: 2), 2026, 2, 0, Today, []).Status.Should().Be(CellStatus.NotExpected);
    }

    [Theory]
    [InlineData("2026-05-10", CellStatus.Due)]       // due 5th + 5 grace days = the 10th
    [InlineData("2026-05-11", CellStatus.Missing)]
    [InlineData("2026-05-01", CellStatus.Due)]
    public void Unpaid_in_the_current_month_is_due_until_the_grace_runs_out(string today, CellStatus status) =>
        RecurringStatus.Evaluate(Item(), 2026, 5, 0, DateOnly.Parse(today), []).Status.Should().Be(status);

    [Fact]
    public void Unpaid_in_a_past_month_is_missing() =>
        RecurringStatus.Evaluate(Item(), 2026, 3, 0, Today, []).Status.Should().Be(CellStatus.Missing);

    [Fact]
    public void Future_months_project_the_average_of_the_last_three_paid_or_the_range_midpoint()
    {
        var withHistory = RecurringStatus.Evaluate(Item(), 2026, 8, 0, Today, [9000, 11000, 12000, 10000]);
        withHistory.Status.Should().Be(CellStatus.Projected);
        withHistory.ProjectedCents.Should().Be(11000, "the last three: 11000, 12000, 10000");
        withHistory.GridCents.Should().Be(11000);

        RecurringStatus.Evaluate(Item(), 2026, 8, 0, Today, []).ProjectedCents.Should().Be(11000, "midpoint of 10000 and 12000");
    }
}

public class CardCheckTests
{
    [Theory]
    [InlineData(-50000, 50000, true)]
    [InlineData(-50000, 49999, false)]
    [InlineData(-50000, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(2500, 0, true)]    // a credit balance owes nothing
    public void Paid_in_full_means_the_payments_cover_what_was_owed(long balance, long payments, bool expected) =>
        CardCheck.PaidInFull(balance, payments).Should().Be(expected);

    [Theory]
    [InlineData(2026, 1, 15, "2026-02-15")]
    [InlineData(2026, 1, 31, "2026-02-28")]
    [InlineData(2026, 12, 20, "2027-01-20")]
    public void Payments_count_until_the_next_months_due_day(int y, int m, int due, string expected) =>
        CardCheck.PaymentDeadline(y, m, due).Should().Be(DateOnly.Parse(expected));
}

public class PriceHistoryTests
{
    [Theory]
    [InlineData(new long[] { 1549, 1549, 1799 }, 1549L)]
    [InlineData(new long[] { 1549, 1799, 1799, 1799 }, 1549L)]       // three charges ago is still recent
    [InlineData(new long[] { 1549, 1799, 1799, 1799, 1799 }, null)]  // older than that is not news
    [InlineData(new long[] { 1099, 1099, 1099 }, null)]
    [InlineData(new long[] { 1099 }, null)]
    [InlineData(new long[] { }, null)]
    [InlineData(new long[] { 999, 1099, 999 }, 1099L)]               // the most recent different amount
    public void Flags_only_a_recent_change(long[] charges, long? previous) =>
        PriceHistory.RecentChange(charges).Should().Be(previous);
}
