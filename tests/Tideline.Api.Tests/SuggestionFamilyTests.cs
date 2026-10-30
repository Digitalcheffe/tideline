using Dapper;
using FluentAssertions;
using Tideline.Core;
using Tideline.Notify;

namespace Tideline.Api.Tests;

/// <summary>
/// The checks behind the suggestions that raise their own card. Each test builds a small made-up ledger, runs the evaluator on a chosen day and
/// reads the cards. The numbers are worked out by hand in each test.
/// </summary>
public sealed class SuggestionFamilyTests : IDisposable
{
    private readonly ApiFixture _api = new(importFixtures: false);
    private int _n;

    public void Dispose() => _api.Dispose();

    private sealed record Card(string Kind, string Period, string Message, long Subject);

    private long Cat(string name, string type)
    {
        _api.Execute("INSERT OR IGNORE INTO categories (name, default_type) VALUES (@name, @type)", new { name, type });
        return _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });
    }

    private long Account(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private void Line(string account, string date, long cents, string description, string? category, string type)
    {
        var categoryId = category is null ? (long?)null : Cat(category, type);
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, category_id, type, created_at, updated_at)
            VALUES (@account, @date, @cents, @description, @external, 'manual', @categoryId, @type, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """, new { account = Account(account), date, cents, description, external = $"manual:t{++_n}", categoryId, type });
    }

    /// <summary>Bank fields cannot be changed (the database refuses), so a line with another amount is the same line put in again.</summary>
    private void Redo(string description, long cents)
    {
        using var conn = _api.Open();
        var old = conn.QuerySingle<(long Account, string Date, long? Category, string Type)>(
            "SELECT account_id, posted_on, category_id, type FROM transactions WHERE original_description = @description", new { description });
        conn.Execute("DELETE FROM transactions WHERE original_description = @description", new { description });
        conn.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, category_id, type, created_at, updated_at)
            VALUES (@Account, @Date, @cents, @description, @external, 'manual', @Category, @Type, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """, new { old.Account, old.Date, cents, description, external = $"manual:t{++_n}", old.Category, old.Type });
    }

    private List<Card> Run(string today)
    {
        using var conn = _api.Open();
        new Evaluator().Run(conn, DateOnly.ParseExact(today, "yyyy-MM-dd"), TimeProvider.System);
        return conn.Query<(string Kind, string Period, string Message, long Subject)>("SELECT kind, period, message, subject_id FROM flags WHERE resolved_at IS NULL")
            .Select(f => new Card(f.Kind, f.Period, f.Message, f.Subject)).ToList();
    }

    private void Switch(string key, bool on) => _api.Execute("UPDATE recommendation_rules SET is_active = @on WHERE flag_kind = @key", new { key, on = on ? 1 : 0 });

    private static List<Card> Only(List<Card> cards, params string[] kinds) => cards.Where(c => kinds.Contains(c.Kind)).ToList();

    // ---- the list itself -----------------------------------------------------------------------------------------

    [Fact]
    public void Every_shipped_suggestion_has_a_fixed_short_key_a_unique_title_and_plain_words()
    {
        var all = SuggestionCatalog.All;

        all.Select(s => s.Key).Should().OnlyHaveUniqueItems().And.OnlyContain(k => k.Length <= 40);
        all.Select(s => s.Title).Should().OnlyHaveUniqueItems();
        all.Select(s => s.Advice).Should().OnlyContain(a => !a.Contains('—') && !a.Contains('–') && a.Length <= 600);
        all.Select(s => s.Key).Should().BeSubsetOf(FlagKinds.All);
        all.Where(s => !s.IsAlertAdvice).Should().OnlyContain(s => s.Note != null && s.Note.Contains("not financial or tax advice"));
    }

    // ---- taxes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_refund_over_two_percent_of_income_but_never_asking_more_than_a_thousand_raises_the_overpayment_card()
    {
        // $60,000 of income in 2025: 2% is $1,200, so the line is capped at $1,000
        Line("Checking", "2025-06-15", 6_000_000, "PAYROLL", "Salary", "income");
        Line("Checking", "2026-03-10", 150_000, "IRS TREAS 310 TAX REF", "Tax refund", "income");

        var card = Only(Run("2026-05-15"), FlagKinds.TaxOverpaid).Should().ContainSingle().Subject;

        card.Period.Should().Be("2025");
        card.Message.Should().Contain("$1,500.00").And.Contain("$125.00 a month");
        card.Subject.Should().Be(Cat("Tax refund", "income"));
    }

    [Fact]
    public void A_refund_under_the_line_raises_nothing_and_a_smaller_income_has_a_lower_line()
    {
        Line("Checking", "2025-06-15", 6_000_000, "PAYROLL", "Salary", "income");
        Line("Checking", "2026-03-10", 90_000, "IRS TREAS 310 TAX REF", "Tax refund", "income");
        Only(Run("2026-05-15"), FlagKinds.TaxOverpaid).Should().BeEmpty("$900 is under the $1,000 line for a $60,000 income");

        // $30,000 of income: 2% is $600, so $700 is enough
        Redo("PAYROLL", 3_000_000);
        Redo("IRS TREAS 310 TAX REF", 70_000);
        Only(Run("2026-05-16"), FlagKinds.TaxOverpaid).Should().ContainSingle();
    }

    [Fact]
    public void A_thousand_dollars_or_more_paid_at_filing_raises_the_underpayment_card_and_so_does_a_penalty_of_any_size()
    {
        Line("Checking", "2025-06-15", 6_000_000, "PAYROLL", "Salary", "income");
        Line("Checking", "2026-04-15", -120_000, "IRS USATAXPYMT", "Taxes", "one_time");

        var card = Only(Run("2026-05-15"), FlagKinds.TaxUnderpaid).Should().ContainSingle().Subject;
        card.Message.Should().Contain("$1,200.00").And.Contain("too little");

        Redo("IRS USATAXPYMT", -40_000);
        Only(Run("2026-05-16"), FlagKinds.TaxUnderpaid).Should().BeEmpty("$400 is under the line, and so the card clears by itself");

        Line("Checking", "2026-04-20", -5_000, "IRS PENALTY 2025", "Taxes", "one_time");
        Only(Run("2026-05-17"), FlagKinds.TaxUnderpaid).Should().ContainSingle().Which.Message.Should().Contain("penalty");
    }

    [Fact]
    public void Nothing_seen_for_the_last_tax_year_is_only_raised_once_the_filing_season_is_over()
    {
        Line("Checking", "2025-01-10", 6_000_000, "PAYROLL", "Salary", "income");
        Cat("Tax refund", "income");

        Only(Run("2026-03-01"), FlagKinds.TaxUnknown).Should().BeEmpty("it may still be on its way");
        Only(Run("2026-05-15"), FlagKinds.TaxUnknown).Should().ContainSingle().Which.Period.Should().Be("2025");

        Line("Checking", "2026-03-10", 150_000, "IRS TREAS 310 TAX REF", "Tax refund", "income");
        Only(Run("2026-05-16"), FlagKinds.TaxUnknown).Should().BeEmpty("a refund was found, so the card clears");
    }

    [Fact]
    public void Without_the_tax_categories_the_tax_suggestions_stay_quiet()
    {
        Line("Checking", "2025-06-15", 6_000_000, "PAYROLL", "Salary", "income");

        Only(Run("2026-05-15"), FlagKinds.TaxOverpaid, FlagKinds.TaxUnderpaid, FlagKinds.TaxUnknown).Should().BeEmpty();
    }

    // ---- savings -------------------------------------------------------------------------------------------------

    private void Saving(string month, long cents) => Line("Savings", month + "-10", cents, "TRANSFER TO SAVINGS", "Savings", "savings");

    [Fact]
    public void Nothing_going_into_savings_and_well_under_usual_are_two_separate_cards()
    {
        foreach (var m in new[] { "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07" }) Saving(m, 40_000); // $400 a month; August and September follow

        // August: nothing
        Only(Run("2026-09-05"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Select(c => c.Kind).Should().Equal(FlagKinds.SavingsNone);

        // August: $150 is under half of the $400 usual
        Saving("2026-08", 15_000);
        var low = Only(Run("2026-09-06"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Should().ContainSingle().Subject;
        low.Kind.Should().Be(FlagKinds.SavingsLow);
        low.Message.Should().Contain("$150.00").And.Contain("$400.00");

        // $250 is more than half
        _api.Execute("DELETE FROM transactions WHERE posted_on = '2026-08-10'");
        Saving("2026-08", 25_000);
        Only(Run("2026-09-07"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Should().BeEmpty();
    }

    [Fact]
    public void Interest_the_bank_paid_is_not_a_deposit_and_too_little_history_is_not_called_usual()
    {
        foreach (var m in new[] { "2026-05", "2026-06", "2026-07" }) Saving(m, 40_000);
        Line("Savings", "2026-08-31", 500, "INTEREST PAID", "Interest", "income");

        Only(Run("2026-09-05"), FlagKinds.SavingsNone).Should().ContainSingle("the $5.00 of interest does not count as putting something in");

        _api.Execute("DELETE FROM transactions WHERE posted_on IN ('2026-05-10', '2026-06-10')");
        Only(Run("2026-09-06"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Should().BeEmpty("only one month of deposits is not enough to say what is usual");
    }

    [Fact]
    public void Each_savings_suggestion_can_be_switched_off_on_its_own()
    {
        foreach (var m in new[] { "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07" }) Saving(m, 40_000);
        Switch(FlagKinds.SavingsNone, false);

        Only(Run("2026-09-05"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Should().BeEmpty();

        Saving("2026-08", 15_000);
        Only(Run("2026-09-06"), FlagKinds.SavingsNone, FlagKinds.SavingsLow).Should().ContainSingle().Which.Kind.Should().Be(FlagKinds.SavingsLow);
    }

    // ---- cashflow ------------------------------------------------------------------------------------------------

    private void Month(string month, long income, long bills, long required, long extra, long savings = 0)
    {
        Line("Checking", month + "-01", income, "PAYROLL", "Salary", "income");
        if (bills > 0) Line("Checking", month + "-03", -bills, "THE BILLS", "Utilities", "bill");
        if (required > 0) Line("Checking", month + "-05", -required, "THE ESSENTIALS", "Groceries", "required");
        if (extra > 0) Line("Checking", month + "-07", -extra, "THE FUN", "Dining", "extra");
        if (savings > 0) Line("Checking", month + "-09", -savings, "SAVINGS", "Savings", "savings");
    }

    [Fact]
    public void Bills_and_required_costs_above_sixty_percent_of_income_raise_the_monthly_card_and_exactly_sixty_does_not()
    {
        // $3,000 in; $1,500 of bills and $400 of required is 63%
        Month("2026-09", 300_000, 150_000, 40_000, 0);
        var card = Only(Run("2026-10-05"), FlagKinds.NeedsHighMonth, FlagKinds.NeedsHighYear).Should().ContainSingle().Subject;
        card.Kind.Should().Be(FlagKinds.NeedsHighMonth);
        card.Period.Should().Be("2026-09");
        card.Message.Should().Contain("63%").And.Contain("$1,900.00").And.Contain("$3,000.00");

        // $1,800 is exactly 60%: not above it
        Redo("THE BILLS", -140_000);
        Only(Run("2026-10-06"), FlagKinds.NeedsHighMonth).Should().BeEmpty();
    }

    [Fact]
    public void Wants_above_thirty_percent_and_savings_under_twenty_percent_are_off_until_switched_on()
    {
        // $3,000 in, $1,000 of fun is 33%, and nothing saved
        Month("2026-09", 300_000, 100_000, 50_000, 100_000);
        Only(Run("2026-10-05"), FlagKinds.WantsHighMonth, FlagKinds.SavingsShareLowMonth).Should().BeEmpty("both ship switched off");

        Switch(FlagKinds.WantsHighMonth, true);
        Switch(FlagKinds.SavingsShareLowMonth, true);
        var cards = Only(Run("2026-10-06"), FlagKinds.WantsHighMonth, FlagKinds.SavingsShareLowMonth);
        cards.Select(c => c.Kind).Should().BeEquivalentTo([FlagKinds.WantsHighMonth, FlagKinds.SavingsShareLowMonth]);
        cards.Single(c => c.Kind == FlagKinds.WantsHighMonth).Message.Should().Contain("33%");
        cards.Single(c => c.Kind == FlagKinds.SavingsShareLowMonth).Message.Should().Contain("0%");
    }

    [Fact]
    public void The_yearly_version_looks_at_the_year_just_ended_and_is_a_separate_switch()
    {
        for (var m = 1; m <= 12; m++) Month($"2025-{m:00}", 300_000, 150_000, 40_000, 0); // 63% all year

        var cards = Only(Run("2026-02-10"), FlagKinds.NeedsHighMonth, FlagKinds.NeedsHighYear);
        cards.Should().ContainSingle(c => c.Kind == FlagKinds.NeedsHighYear).Which.Period.Should().Be("2025");

        Switch(FlagKinds.NeedsHighYear, false);
        Only(Run("2026-02-11"), FlagKinds.NeedsHighYear).Should().BeEmpty("it clears when switched off");
    }

    [Fact]
    public void With_no_income_seen_the_shares_cannot_be_worked_out_and_stay_quiet()
    {
        Line("Checking", "2026-09-03", -150_000, "THE BILLS", "Utilities", "bill");

        Only(Run("2026-10-05"), FlagKinds.NeedsHighMonth, FlagKinds.WantsHighMonth, FlagKinds.SavingsShareLowMonth).Should().BeEmpty();
    }

    [Fact]
    public void No_savings_account_is_raised_once_there_is_a_month_of_data_and_goes_when_one_is_added()
    {
        _api.Execute("UPDATE accounts SET is_closed = 1 WHERE kind = 'savings'");

        Only(Run("2026-10-05"), FlagKinds.NoSavingsAccount).Should().BeEmpty("no lines at all yet");
        Line("Checking", "2026-08-03", -2_000, "SOMETHING", "Dining", "extra");
        Only(Run("2026-10-06"), FlagKinds.NoSavingsAccount).Should().ContainSingle().Which.Period.Should().Be("2026");

        _api.Execute("UPDATE accounts SET is_closed = 0 WHERE kind = 'savings'");
        Only(Run("2026-10-07"), FlagKinds.NoSavingsAccount).Should().BeEmpty();
    }

    [Fact]
    public void The_cushion_needs_a_savings_account_with_a_known_balance_and_fires_under_three_months()
    {
        for (var m = 7; m <= 9; m++) Month($"2026-{m:00}", 300_000, 100_000, 50_000, 0); // $1,500 a month of bills and required

        Only(Run("2026-10-05"), FlagKinds.EmergencyCushion).Should().BeEmpty("the savings balance is not known yet");

        _api.Execute("UPDATE accounts SET opening_balance_cents = 150000, opening_on = '2026-01-01' WHERE name = 'Savings'"); // $1,500 is one month
        var card = Only(Run("2026-10-06"), FlagKinds.EmergencyCushion).Should().ContainSingle().Subject;
        card.Message.Should().Contain("1.0 months").And.Contain("$1,500.00");
        card.Period.Should().Be("2026-Q4");

        _api.Execute("UPDATE accounts SET opening_balance_cents = 450000 WHERE name = 'Savings'"); // exactly three months
        Only(Run("2026-10-07"), FlagKinds.EmergencyCushion).Should().BeEmpty();

        _api.Execute("UPDATE accounts SET opening_balance_cents = 100000, is_closed = 1 WHERE name = 'Savings'");
        Only(Run("2026-10-08"), FlagKinds.EmergencyCushion).Should().BeEmpty("a closed account is not a cushion");
    }

    // ---- subscriptions -------------------------------------------------------------------------------------------

    private void Subscription(string name, long cents, string frequency = "monthly", int everyN = 1)
    {
        var category = Cat("Streaming", "extra");
        _api.Execute("""
            INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, is_active)
            VALUES (@name, @category, @cents, @cents, @frequency, @everyN, 5, '2026-01-05', 3, 1)
            """, new { name, category, cents, frequency, everyN });
    }

    [Fact]
    public void The_quarterly_review_counts_subscriptions_and_spreads_a_yearly_one_over_twelve_months()
    {
        Subscription("Music", 1_000);
        Subscription("Video", 2_000);
        Subscription("Cloud", 12_000, "yearly");

        var card = Only(Run("2026-10-05"), FlagKinds.SubscriptionsReview, FlagKinds.SubscriptionsMany).Should().ContainSingle().Subject;

        card.Kind.Should().Be(FlagKinds.SubscriptionsReview);
        card.Period.Should().Be("2026-Q4");
        card.Message.Should().Contain("3 subscriptions").And.Contain("$40.00 a month").And.Contain("$480.00 a year");
    }

    [Fact]
    public void More_than_ten_subscriptions_adds_a_second_card_and_ten_is_not_more_than_ten()
    {
        for (var i = 1; i <= 10; i++) Subscription($"Service {i}", 1_000);
        Only(Run("2026-10-05"), FlagKinds.SubscriptionsMany).Should().BeEmpty("ten is not over ten");

        Subscription("Service 11", 1_000);
        var cards = Only(Run("2026-10-06"), FlagKinds.SubscriptionsReview, FlagKinds.SubscriptionsMany);
        cards.Select(c => c.Kind).Should().BeEquivalentTo([FlagKinds.SubscriptionsReview, FlagKinds.SubscriptionsMany]);
        cards.Single(c => c.Kind == FlagKinds.SubscriptionsMany).Message.Should().Contain("11 subscriptions");
    }

    [Fact]
    public void Bills_and_ended_items_are_not_subscriptions_and_with_none_nothing_is_raised()
    {
        var bills = Cat("Utilities", "bill");
        _api.Execute("INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, is_active) VALUES ('Power', @bills, 9000, 9000, 'monthly', 1, 3, '2026-01-03', 3, 1)", new { bills });
        Subscription("Old one", 1_000);
        _api.Execute("UPDATE recurring SET is_active = 0 WHERE name = 'Old one'");

        Only(Run("2026-10-05"), FlagKinds.SubscriptionsReview, FlagKinds.SubscriptionsMany).Should().BeEmpty();
    }
}
