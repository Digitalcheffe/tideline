using FluentAssertions;
using Tideline.Core;
using Tideline.Core.Rules;

namespace Tideline.Core.Tests;

public class RulesEngineTests
{
    private const long Groceries = 1, Gas = 2, Delivery = 3, Uncategorized = 9, Salary = 10, Spotify = 11;

    private static LineType DefaultType(long categoryId) => categoryId switch
    {
        Groceries or Gas or Delivery => LineType.Required,
        Salary => LineType.Income,
        Spotify => LineType.Bill,
        _ => LineType.Extra,
    };

    private static RuleLine Line(string description, long cents, string bank = "1111",
        AccountKind kind = AccountKind.Checking, bool manual = false, bool move = false) =>
        new(description, cents, bank, kind, manual, move);

    private static Rule R(long id, int priority, long category, params RuleCondition[] conditions) =>
        new(id, $"rule {id}", priority, true, conditions, SetCategoryId: category);

    private static long? Winner(RuleLine line, params Rule[] rules) =>
        RulesEngine.Decide(line, rules, DefaultType)?.CategoryId;

    [Fact]
    public void First_match_by_priority_wins_regardless_of_input_order()
    {
        var general = R(1, 50, Groceries, new DescriptionContainsAny(["STORE"]));
        var specific = R(2, 10, Gas, new DescriptionContainsAny(["STORE FUEL"]));

        Winner(Line("STORE FUEL 123", -3000), general, specific).Should().Be(Gas);
        Winner(Line("STORE FUEL 123", -3000), specific, general).Should().Be(Gas);
        Winner(Line("STORE #55", -3000), specific, general).Should().Be(Groceries);
    }

    [Fact]
    public void Delivery_above_gas_trap()
    {
        var delivery = R(1, 10, Delivery, new DescriptionContainsAny(["DOORDASH"]));
        var gas = R(2, 20, Gas, new DescriptionContainsAny(["SHELL"]));

        Winner(Line("DD *DOORDASH SHELL DELI", -2500), gas, delivery).Should().Be(Delivery);
    }

    [Fact]
    public void Amount_bands_narrowest_first()
    {
        var big = R(1, 10, Uncategorized, new DescriptionContainsAny(["GADGET"]), new AmountAtLeast(90000));
        var small = R(2, 20, Groceries, new DescriptionContainsAny(["GADGET"]));

        Winner(Line("GADGET SHOP", -125000), small, big).Should().Be(Uncategorized);
        Winner(Line("GADGET SHOP", -4000), small, big).Should().Be(Groceries);
        Winner(Line("GADGET SHOP", -90000), small, big).Should().Be(Uncategorized, "the band edge is inclusive");
        Winner(Line("GADGET SHOP", -89999), small, big).Should().Be(Groceries);
    }

    [Fact]
    public void Check_ranges_use_both_bounds()
    {
        var rent = R(1, 10, Gas, new DescriptionStartsWith("Check #"), new AmountAtLeast(150000), new AmountAtMost(150000));
        var anyCheck = R(2, 20, Groceries, new DescriptionStartsWith("Check #"));

        Winner(Line("Check #1042", -150000), anyCheck, rent).Should().Be(Gas);
        Winner(Line("Check #1042", -150001), anyCheck, rent).Should().Be(Groceries);
    }

    [Fact]
    public void Income_rule_is_limited_to_money_in_on_bank_accounts_so_card_refunds_fall_through()
    {
        var payroll = R(1, 10, Salary,
            new DirectionIs(Direction.In), new AccountKindIn([AccountKind.Checking, AccountKind.Savings]),
            new DescriptionContainsAny(["PAYROLL"]));
        var catchAll = R(2, 99, Uncategorized);

        Winner(Line("ACME PAYROLL", 200000), payroll, catchAll).Should().Be(Salary);
        Winner(Line("PAYROLL REFUND", 5000, kind: AccountKind.CreditCard), payroll, catchAll).Should().Be(Uncategorized);
        Winner(Line("ACME PAYROLL", -200000), payroll, catchAll).Should().Be(Uncategorized);
    }

    [Fact]
    public void A_rule_with_no_conditions_matches_everything_and_catch_all_is_last()
    {
        Winner(Line("ANYTHING", -1), R(1, 99, Uncategorized)).Should().Be(Uncategorized);
    }

    [Fact]
    public void Nothing_matches_gives_null() =>
        RulesEngine.Decide(Line("X", -1), [R(1, 10, Gas, new DescriptionContainsAny(["SHELL"]))], DefaultType).Should().BeNull();

    [Fact]
    public void Inactive_rules_manual_overrides_and_moves_are_never_touched()
    {
        var catchAll = R(1, 99, Uncategorized);

        RulesEngine.Decide(Line("X", -1), [catchAll with { IsActive = false }], DefaultType).Should().BeNull();
        RulesEngine.Decide(Line("X", -1, manual: true), [catchAll], DefaultType).Should().BeNull();
        RulesEngine.Decide(Line("X", -1, move: true), [catchAll], DefaultType).Should().BeNull();
    }

    [Fact]
    public void Type_falls_back_to_the_category_default_unless_the_rule_sets_one()
    {
        var withDefault = RulesEngine.Decide(Line("X", -1), [R(1, 10, Spotify)], DefaultType)!;
        withDefault.Type.Should().Be(LineType.Bill);

        var explicitType = RulesEngine.Decide(Line("X", -1), [R(1, 10, Spotify) with { SetType = LineType.OneTime }], DefaultType)!;
        explicitType.Type.Should().Be(LineType.OneTime);

        var neither = RulesEngine.Decide(Line("X", -1), [new Rule(1, "n", 10, true, [])], DefaultType)!;
        neither.Type.Should().BeNull();
    }

    [Fact]
    public void Decision_carries_payee_links_and_tags()
    {
        var rule = R(1, 10, Spotify) with { SetPayee = "Spotify", SetRecurringId = 7, SetPlanId = 8, TagIds = [3, 4] };

        var d = RulesEngine.Decide(Line("SPOTIFY USA", -1099), [rule], DefaultType)!;

        (d.Payee, d.RecurringId, d.PlanId).Should().Be(("Spotify", 7L, 8L));
        d.TagIds.Should().Equal(3, 4);
        d.Rule.Should().Be(rule);
    }

    [Fact]
    public void Conditions_behave_as_specified()
    {
        new DescriptionContainsAny(["spotify"]).Holds(Line("SPOTIFY USA", -1)).Should().BeTrue();
        new DescriptionEqualsAny(["Check #2231"]).Holds(Line("  Check #2231 ", -1)).Should().BeTrue();
        new DescriptionEqualsAny(["Check #2231"]).Holds(Line("Check #22310", -1)).Should().BeFalse();
        new DescriptionStartsWith("check #").Holds(Line("CHECK #1", -1)).Should().BeTrue();
        new AccountIs("1111").Holds(Line("X", -1, bank: "2222")).Should().BeFalse();
        new AmountAtLeast(1000).Holds(Line("X", 1000)).Should().BeTrue("amount bands use the absolute value");
        new DirectionIs(Direction.Out).Holds(Line("X", 0)).Should().BeFalse("zero is neither in nor out");
    }

    [Theory]
    [InlineData("description", "contains_any", "[\"A\",\"B\"]", typeof(DescriptionContainsAny))]
    [InlineData("description", "equals_any", "[\"Check #1\"]", typeof(DescriptionEqualsAny))]
    [InlineData("description", "starts_with", "\"Check #\"", typeof(DescriptionStartsWith))]
    [InlineData("amount", "gte", "900", typeof(AmountAtLeast))]
    [InlineData("amount", "lte", "1500.50", typeof(AmountAtMost))]
    [InlineData("account", "is", "\"1234\"", typeof(AccountIs))]
    [InlineData("account_kind", "in", "[\"checking\",\"savings\"]", typeof(AccountKindIn))]
    [InlineData("direction", "is", "\"in\"", typeof(DirectionIs))]
    public void Stored_conditions_are_built_from_field_op_and_json(string field, string op, string json, Type expected) =>
        RuleCondition.From(field, op, json).Should().BeOfType(expected);

    [Fact]
    public void Stored_dollar_amounts_become_cents_and_bad_input_is_rejected()
    {
        RuleCondition.From("amount", "lte", "1500.50").Should().Be(new AmountAtMost(150050));
        FluentActions.Invoking(() => RuleCondition.From("amount", "between", "1")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RuleCondition.From("account_kind", "in", "[\"boat\"]")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RuleCondition.From("direction", "is", "\"sideways\"")).Should().Throw<ArgumentException>();
    }
}

public class PayeeCleanerTests
{
    [Theory]
    [InlineData("Acme Hardware", "ACME HARDWARE #12", "Acme Hardware")]   // merchant name wins
    [InlineData("Zelle", "ZELLE PAYMENT TO JOE 12345", "Zelle Payment To Joe")]
    [InlineData("Bank of America", "BANK OF AMERICA CC", "Bank Of America Cc")]
    [InlineData("", "SQ *CORNER COFFEE 1234", "Corner Coffee")]
    [InlineData("", "TST* TACO PLACE 55", "Taco Place")]
    [InlineData("", "DD *DOORDASH PIZZA HUT", "Pizza Hut")]
    [InlineData("", "SPOTIFY USA", "Spotify Usa")]
    [InlineData("", "STORE #123 MAIN ST", "Store")]
    [InlineData("", "WIDGET CO - 0042", "Widget Co")]
    [InlineData("", "12345 ONLY DIGITS", "12345 Only Digits")]   // nothing before the first digit: fall back to the whole text
    public void Cleans_the_payee(string merchant, string description, string expected) =>
        PayeeCleaner.For(merchant, description).Should().Be(expected);

    [Fact]
    public void Payee_is_capped_at_40_characters() =>
        PayeeCleaner.For("", new string('A', 60)).Should().HaveLength(40);
}
