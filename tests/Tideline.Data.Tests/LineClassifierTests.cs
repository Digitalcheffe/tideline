using FluentAssertions;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data.Services;

namespace Tideline.Data.Tests;

/// <summary>What a line becomes when no rule, or a rule, speaks for it. All lines here are made up.</summary>
public sealed class LineClassifierTests
{
    private const long Uncategorized = 9, Groceries = 1, Review = 77;

    private static LineClassifier With(params Rule[] rules) => new(rules,
        new Dictionary<long, LineType> { [Uncategorized] = LineType.Extra, [Groceries] = LineType.Required },
        Uncategorized, Review);

    private static RuleLine Line(long cents, AccountKind kind, string description = "SOMETHING NEW 42") => new(description, cents, "x", kind);

    [Theory]
    [InlineData(AccountKind.Checking)]
    [InlineData(AccountKind.Savings)]
    public void Unmatched_money_into_a_checking_or_savings_account_is_income_for_review(AccountKind kind)
    {
        var c = With().Classify(Line(250_000, kind, "DEPOSIT FROM A NEW EMPLOYER"), "");

        c.Type.Should().Be(LineType.Income, "a first paycheck must not show up as negative spending before a rule exists");
        c.CategoryId.Should().Be(Uncategorized);
        c.TagIds.Should().Contain(Review);
        c.MatchedARule.Should().BeFalse();
    }

    [Fact]
    public void Unmatched_money_out_is_extra_and_a_credit_on_a_card_is_not_income()
    {
        With().Classify(Line(-2_500, AccountKind.Checking), "").Type.Should().Be(LineType.Extra);
        With().Classify(Line(-2_500, AccountKind.CreditCard), "").Type.Should().Be(LineType.Extra);
        With().Classify(Line(1_500, AccountKind.CreditCard, "A REFUND OR CREDIT"), "").Type.Should().Be(LineType.Extra, "a card credit is usually a refund, which is not income");
        With().Classify(Line(0, AccountKind.Checking), "").Type.Should().Be(LineType.Extra);
    }

    [Fact]
    public void A_rule_that_matches_decides_even_for_money_in()
    {
        var groceryRefund = new Rule(1, "Groceries", 10, true, [new DescriptionContainsAny(["GROCER"])], Groceries);

        var c = With(groceryRefund).Classify(Line(1_200, AccountKind.Checking, "GROCERY STORE REFUND"), "");

        c.Type.Should().Be(LineType.Required, "the category's own type is what the rule asked for");
        c.CategoryId.Should().Be(Groceries);
        c.MatchedARule.Should().BeTrue();
        c.TagIds.Should().NotContain(Review);
    }
}
