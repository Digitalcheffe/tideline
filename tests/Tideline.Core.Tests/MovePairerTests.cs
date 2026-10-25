using FluentAssertions;
using Tideline.Core;
using Tideline.Core.Moves;

namespace Tideline.Core.Tests;

public class MovePairerTests
{
    private const long Checking = 1, Savings = 2, Card = 3;

    private static readonly MovePattern ToSavings = new(1, Checking, "TRANSFER TO SAVINGS", Savings, "TRANSFER FROM CHECKING");
    private static readonly MovePattern CardPayment = new(2, Checking, "CARD PAYMENT", Card, "PAYMENT THANK YOU");

    private static MoveCandidate Row(long id, long account, int day, long cents, string description) =>
        new(id, account, new DateOnly(2026, 3, day), cents, description);

    [Fact]
    public void Pairs_an_out_leg_with_the_matching_in_leg()
    {
        var result = MovePairer.Pair(
            [Row(1, Checking, 5, -50000, "TRANSFER TO SAVINGS"), Row(2, Savings, 5, 50000, "Transfer from Checking"), Row(3, Checking, 5, -999, "COFFEE")],
            [ToSavings]);

        result.Pairs.Should().Equal(new MovePair(1, 2, 1));
        result.Pending.Should().BeEmpty("the coffee matches no pattern");
    }

    [Fact]
    public void Smallest_day_gap_wins_and_ties_go_to_the_earlier_in_row()
    {
        var rows = new[]
        {
            Row(1, Checking, 10, -10000, "TRANSFER TO SAVINGS"),
            Row(2, Savings, 13, 10000, "TRANSFER FROM CHECKING"),   // gap 3
            Row(3, Savings, 11, 10000, "TRANSFER FROM CHECKING"),   // gap 1  <- closest
        };
        MovePairer.Pair(rows, [ToSavings]).Pairs.Single().InTransactionId.Should().Be(3);

        var tie = new[]
        {
            Row(1, Checking, 10, -10000, "TRANSFER TO SAVINGS"),
            Row(5, Savings, 12, 10000, "TRANSFER FROM CHECKING"),   // gap 2, later
            Row(4, Savings, 8, 10000, "TRANSFER FROM CHECKING"),    // gap 2, earlier
        };
        MovePairer.Pair(tie, [ToSavings]).Pairs.Single().InTransactionId.Should().Be(4);
    }

    [Fact]
    public void Outside_the_window_or_a_different_amount_stays_pending()
    {
        var result = MovePairer.Pair(
            [Row(1, Checking, 1, -10000, "TRANSFER TO SAVINGS"), Row(2, Savings, 7, 10000, "TRANSFER FROM CHECKING"), // 6 days
             Row(3, Checking, 20, -10000, "TRANSFER TO SAVINGS"), Row(4, Savings, 20, 9999, "TRANSFER FROM CHECKING")],
            [ToSavings]);

        result.Pairs.Should().BeEmpty();
        result.Pending.Should().BeEquivalentTo(new long[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void Window_edge_is_inclusive_and_configurable()
    {
        var rows = new[] { Row(1, Checking, 1, -100, "TRANSFER TO SAVINGS"), Row(2, Savings, 6, 100, "TRANSFER FROM CHECKING") };

        MovePairer.Pair(rows, [ToSavings]).Pairs.Should().HaveCount(1);
        MovePairer.Pair(rows, [ToSavings with { MaxDaysApart = 4 }]).Pairs.Should().BeEmpty();
    }

    [Fact]
    public void Each_in_row_is_used_once_and_same_amount_repeats_pair_off_in_date_order()
    {
        var rows = new[]
        {
            Row(1, Checking, 1, -10000, "TRANSFER TO SAVINGS"),
            Row(2, Checking, 3, -10000, "TRANSFER TO SAVINGS"),
            Row(3, Savings, 2, 10000, "TRANSFER FROM CHECKING"),
            Row(4, Savings, 3, 10000, "TRANSFER FROM CHECKING"),
        };

        var result = MovePairer.Pair(rows, [ToSavings]);

        result.Pairs.Select(p => (p.OutTransactionId, p.InTransactionId)).Should().Equal((1L, 3L), (2L, 4L));
    }

    [Fact]
    public void Direction_matters_out_must_be_negative_in_must_be_positive()
    {
        var result = MovePairer.Pair(
            [Row(1, Checking, 1, 10000, "TRANSFER TO SAVINGS"), Row(2, Savings, 1, -10000, "TRANSFER FROM CHECKING")], [ToSavings]);

        result.Pairs.Should().BeEmpty();
        result.Pending.Should().BeEmpty("neither row is on the right side of the pattern");
    }

    [Fact]
    public void Several_patterns_work_independently()
    {
        var result = MovePairer.Pair(
            [Row(1, Checking, 2, -30000, "CARD PAYMENT VISA"), Row(2, Card, 3, 30000, "PAYMENT THANK YOU"),
             Row(3, Checking, 2, -5000, "TRANSFER TO SAVINGS"), Row(4, Savings, 2, 5000, "TRANSFER FROM CHECKING")],
            [ToSavings, CardPayment]);

        result.Pairs.Select(p => (p.OutTransactionId, p.InTransactionId, p.PatternId))
            .Should().BeEquivalentTo(new[] { (3L, 4L, 1L), (1L, 2L, 2L) });
    }

    [Fact]
    public void A_lone_leg_is_pending_and_pairs_when_its_partner_arrives_in_a_later_import()
    {
        var first = MovePairer.Pair([Row(1, Checking, 28, -10000, "TRANSFER TO SAVINGS")], [ToSavings]);
        first.Pairs.Should().BeEmpty();
        first.Pending.Should().Equal(1L);

        var later = MovePairer.Pair([Row(1, Checking, 28, -10000, "TRANSFER TO SAVINGS"), Row(9, Savings, 30, 10000, "TRANSFER FROM CHECKING")], [ToSavings]);
        later.Pairs.Should().Equal(new MovePair(1, 9, 1));
        later.Pending.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AccountKind.Checking, AccountKind.Savings, MoveCountsAs.Savings)]
    [InlineData(AccountKind.Savings, AccountKind.Checking, MoveCountsAs.Savings)]
    [InlineData(AccountKind.Savings, AccountKind.Savings, MoveCountsAs.None)]
    [InlineData(AccountKind.Checking, AccountKind.CreditCard, MoveCountsAs.None)]
    [InlineData(AccountKind.Checking, AccountKind.Checking, MoveCountsAs.None)]
    public void Counts_as_savings_only_when_exactly_one_leg_is_savings(AccountKind from, AccountKind to, MoveCountsAs expected) =>
        MovePairer.CountsAs(from, to).Should().Be(expected);
}
