using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Cli;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Cli.Tests;

/// <summary>The starter categories and rules. Every description here is made up for the test; none is a line from a real household.</summary>
public sealed class StarterPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-starter-{Guid.NewGuid():N}");
    private readonly string _cs;

    public StarterPackTests()
    {
        Directory.CreateDirectory(_dir);
        _cs = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Code, string Text)> Run(params string[] args)
    {
        var sw = new StringWriter();
        var code = await new CliApp(sw, new FixedClock(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero))).RunAsync([.. args, "--db", _cs]);
        return (code, sw.ToString());
    }

    private async Task Loaded()
    {
        (await Run("migrate")).Code.Should().Be(0);
        (await Run("starter")).Code.Should().Be(0);
    }

    private (string Category, string Type, bool MatchedARule) Classify(string description, long cents, AccountKind kind)
    {
        using var conn = new SqliteConnectionFactory(_cs).Open();
        var c = LineClassifier.Load(conn, TimeProvider.System).Classify(new RuleLine(description, cents, "x", kind), "");
        return (conn.ExecuteScalar<string>("SELECT name FROM categories WHERE id = @id", new { id = c.CategoryId })!, c.Type.ToDb(), c.MatchedARule);
    }

    // ---- the file itself ----------------------------------------------------------------------------------

    [Fact]
    public void The_pack_is_in_the_build_and_has_categories_and_rules()
    {
        var pack = StarterPack.Load();

        pack.Categories.Should().HaveCountGreaterThan(30);
        pack.Rules.Should().HaveCountGreaterThan(40);
        pack.Accounts.Should().BeEmpty("a starter pack says nothing about anyone's accounts");
        pack.Recurring.Should().BeEmpty();
        pack.MovePatterns.Should().BeEmpty();
    }

    [Fact]
    public void Names_are_unique_every_rule_has_its_own_priority_and_sits_below_any_rule_a_person_made()
    {
        var pack = StarterPack.Load();

        pack.Categories.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        pack.Rules.Select(r => r.Name).Should().OnlyHaveUniqueItems().And.OnlyContain(n => n.StartsWith(StarterPack.RuleNamePrefix));
        pack.Rules.Select(r => r.Priority).Should().OnlyHaveUniqueItems().And.OnlyContain(p => p >= 1000, "rules made by hand use the numbers below 1000 and are tried first");
        pack.Rules.Should().OnlyContain(r => r.Set.Category != null && pack.Categories.Any(c => c.Name == r.Set.Category), "every rule names a category in the pack");
        pack.Rules.Should().OnlyContain(r => r.Set.Type == null && r.Set.Payee == null && r.Set.Recurring == null, "a type comes from the category; payees and recurring items belong to a household");
    }

    [Fact]
    public void Every_rule_looks_at_the_description_and_no_word_is_short_or_a_number()
    {
        var pack = StarterPack.Load();

        pack.Rules.Should().OnlyContain(r => r.When.ContainsKey("contains_any") || r.When.ContainsKey("starts_with"), "a rule with no words would match every line");

        var words = pack.Rules.SelectMany(r => r.When.TryGetValue("contains_any", out var v) ? v.EnumerateArray().Select(e => e.GetString()!) : []).ToList();
        words.Should().NotBeEmpty().And.OnlyContain(w => w.Trim().Length >= 3, "a shorter word matches by accident");
        words.Should().OnlyContain(w => !Regex.IsMatch(w, @"\d{4,}"), "a long number is an account or a store number, which belongs to one household");
    }

    [Fact]
    public void Words_that_are_known_to_match_by_accident_are_not_used_on_their_own()
    {
        // each of these sits inside a more common word: SHELL in a mortgage servicer, IRS in FIRST, TIRE in ENTIRE, MOBIL in MOBILE, DELI in DELIVERY,
        // HAIR in CHAIR, ULTA in CONSULTANT, CAVA in CAVALIER, PTA in CAPTAIN, TACO in TACOMA, FENCE in DEFENCE
        string[] risky = ["SHELL", "IRS", "TIRE", "MOBIL", "DELI", "HAIR", "ULTA", "CAVA", "PTA", "TACO", "FENCE", "ENERGY", "POWER", "WATER", "RENT", "CAFE", "SQ *", "CANVA", "ACADEMY"];

        var words = StarterPack.Load().Rules.SelectMany(r => r.When.TryGetValue("contains_any", out var v) ? v.EnumerateArray().Select(e => e.GetString()!) : []);

        words.Should().NotContain(risky);
    }

    // ---- loading it -----------------------------------------------------------------------------------------

    [Fact]
    public async Task It_loads_into_an_empty_database_and_is_safe_to_run_again()
    {
        (await Run("migrate")).Code.Should().Be(0);
        var pack = StarterPack.Load();

        var first = await Run("starter");
        var second = await Run("starter");

        first.Code.Should().Be(0);
        first.Text.Should().Contain($"Categories: {pack.Categories.Count} added, 0 already there.").And.Contain($"Rules: {pack.Rules.Count} added, 0 already there.");
        second.Text.Should().Contain($"Categories: 0 added, {pack.Categories.Count} already there.").And.Contain($"Rules: 0 added, {pack.Rules.Count} already there.");

        using var conn = new SqliteConnectionFactory(_cs).Open();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM rules").Should().Be(pack.Rules.Count);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM categories").Should().Be(pack.Categories.Count);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM rule_conditions").Should().BeGreaterThan(pack.Rules.Count);
    }

    [Fact]
    public async Task A_rule_you_made_yourself_is_tried_first()
    {
        await Loaded();
        var mine = Path.Combine(_dir, "mine.json");
        await File.WriteAllTextAsync(mine, """
            { "categories": [ { "name": "Family food", "defaultType": "required" } ],
              "rules": [ { "name": "My Kroger", "priority": 10, "when": { "contains_any": ["KROGER"] }, "set": { "category": "Family food" } } ] }
            """);
        (await Run("setup", mine)).Code.Should().Be(0);

        Classify("KROGER #123 ANYTOWN", -5000, AccountKind.Checking).Category.Should().Be("Family food");
        Classify("FOOD LION #55 ANYTOWN", -5000, AccountKind.Checking).Category.Should().Be("Groceries");
    }

    // ---- what it does with lines -----------------------------------------------------------------------------

    [Theory]
    // money in
    [InlineData("PAYROLL ACME INC 12AB ACH CREDIT", 250000, AccountKind.Checking, "Salary", "income")]
    [InlineData("PAYROLL TAX SERVICE ACH DEBIT", -5000, AccountKind.Checking, "Uncategorized", "extra")]
    [InlineData("INTEREST PAID FOR THE PERIOD", 123, AccountKind.Savings, "Interest and dividends", "income")]
    [InlineData("INTEREST CHARGE ON PURCHASES", -1500, AccountKind.CreditCard, "Fees and interest", "extra")]
    [InlineData("IRS TREAS 310 TAX REF", 120000, AccountKind.Checking, "Tax refund", "income")]
    [InlineData("USATAXPYMT IRS 1234 ACH DEBIT", -50000, AccountKind.Checking, "Taxes", "one_time")]
    [InlineData("FIRST CHOICE MARKET 88", -2000, AccountKind.Checking, "Uncategorized", "extra")]
    // housing, bills, loans
    [InlineData("ACH PMT NEWREZ-SHELLPOIN 123 ACH DEBIT", -150000, AccountKind.Checking, "Mortgage", "bill")]
    [InlineData("SHELLPOINT MORTGAGE SERVICING", -150000, AccountKind.Checking, "Mortgage", "bill")]
    [InlineData("SHELL OIL 5551 ANYTOWN ST", -4000, AccountKind.CreditCard, "Gas and fuel", "required")]
    [InlineData("BILLPAY DOMINION ENERGY 99 ACH DEBIT", -12000, AccountKind.Checking, "Utilities", "bill")]
    [InlineData("SOUTHWEST GAS 12345 NV", -9000, AccountKind.Checking, "Utilities", "bill")]
    [InlineData("VERIZON*RECURRING 800-VERIZON FL DEBIT CARD RECURRING PYMT", -8000, AccountKind.Checking, "Internet and phone", "bill")]
    [InlineData("debitpmt Example Insuranc 123 ACH DEBIT", -20000, AccountKind.Checking, "Insurance", "bill")]
    [InlineData("PAYMENTS FIRSTMARK 123 ACH DEBIT", -20000, AccountKind.Checking, "Student loans", "bill")]
    // food
    [InlineData("KROGER #123 03-22-26 ANYTOWN DEBIT CARD PURCHASE-PIN", -5000, AccountKind.Checking, "Groceries", "required")]
    [InlineData("KROGER FUEL CENTER #123", -4000, AccountKind.CreditCard, "Gas and fuel", "required")]
    [InlineData("DD *DOORDASH WAWA STORE 12 DOORDASH.COM CA US", -2500, AccountKind.CreditCard, "Delivery", "extra")]
    [InlineData("DD *DOORDASH DASHPASS DOORDASH.COM CA US", -999, AccountKind.CreditCard, "Subscriptions", "extra")]
    [InlineData("UBER *EATS HELP.UBER.COM CA US", -2500, AccountKind.CreditCard, "Delivery", "extra")]
    [InlineData("UBER *TRIP HELP.UBER.COM CA US", -1500, AccountKind.CreditCard, "Rideshare and taxi", "extra")]
    [InlineData("WAWA #1234 ANYTOWN PA US", -3000, AccountKind.CreditCard, "Gas and fuel", "required")]
    [InlineData("BP1234567SOME MARKET 12 ANYTOWN", -3000, AccountKind.CreditCard, "Gas and fuel", "required")]
    [InlineData("MCDONALD'S F1234 ANYTOWN", -1200, AccountKind.CreditCard, "Dining", "extra")]
    [InlineData("TST* SOMEWHERE TACOS ANYTOWN", -2500, AccountKind.CreditCard, "Dining", "extra")]
    [InlineData("STARBUCKS STORE 123", -600, AccountKind.CreditCard, "Coffee", "extra")]
    // shops and subscriptions
    [InlineData("AMAZON PRIME*AB12CD Amzn.com/bill WA", -13900, AccountKind.CreditCard, "Subscriptions", "extra")]
    [InlineData("AMAZON MKTPL*XY12Z Amzn.com/bill WA", -2500, AccountKind.CreditCard, "Shopping", "extra")]
    [InlineData("AMAZON MKTPLACE PMTSAmzn.com/bill WA", 1536, AccountKind.CreditCard, "Shopping", "extra")]
    [InlineData("CANVAS PRINT SHOP 555 TX", -4000, AccountKind.CreditCard, "Uncategorized", "extra")]
    [InlineData("NETFLIX.COM 866-579-7172 CA", -1549, AccountKind.CreditCard, "Subscriptions", "extra")]
    [InlineData("WALMART.COM 800-925-6278 AR", -4000, AccountKind.CreditCard, "Shopping", "extra")]
    [InlineData("OLD NAVY US 1234 ANYTOWN", -4000, AccountKind.CreditCard, "Clothing", "extra")]
    [InlineData("THE HOME DEPOT 1234 ANYTOWN", -9000, AccountKind.CreditCard, "Home improvement", "extra")]
    // health and pets
    [InlineData("ANYTOWN ANIMAL HO5550100 VA US", -20000, AccountKind.CreditCard, "Pets", "required")]
    [InlineData("ANYTOWN HOSPITAL 555-0100 TN", -5000, AccountKind.Checking, "Medical", "required")]
    [InlineData("CVS/PHARMACY #1234 ANYTOWN", -1800, AccountKind.CreditCard, "Medical", "required")]
    // away and about
    [InlineData("SOUTHWES #5262100 TX", -19900, AccountKind.CreditCard, "Travel", "one_time")]
    [InlineData("DELTA AIR LINES 0062 ATLANTA", -45000, AccountKind.CreditCard, "Travel", "one_time")]
    [InlineData("DISCOUNT TIRE #1234 ANYTOWN", -60000, AccountKind.CreditCard, "Auto upkeep", "required")]
    [InlineData("ENTIRE HOME CARE LLC", -1000, AccountKind.CreditCard, "Uncategorized", "extra")]
    [InlineData("TOLLPAYMNT EZPASSVA 123 ACH DEBIT", -3000, AccountKind.Checking, "Parking and tolls", "required")]
    [InlineData("ATM NETWORK CASH WITHDRAWAL", -10000, AccountKind.Checking, "Cash", "extra")]
    // left for a person: nothing here can tell what these are, so they go to review
    [InlineData("ZELLE PAYMENT TO SOME PERSON", -5000, AccountKind.Checking, "Uncategorized", "extra")]
    [InlineData("SQ *SOME LOCAL SHOP ANYTOWN", -2500, AccountKind.CreditCard, "Uncategorized", "extra")]
    [InlineData("Check #2210", -4500, AccountKind.Checking, "Uncategorized", "extra")]
    public async Task A_line_gets_the_category_and_type_it_should(string description, long cents, AccountKind kind, string category, string type)
    {
        await Loaded();

        var result = Classify(description, cents, kind);

        result.Category.Should().Be(category);
        result.Type.Should().Be(type);
    }

    // What SimpleFIN sends is not known yet (issue #62), so these are the differences it plausibly has from the bank's CSV text: any case,
    // the merchant alone with no store number or place, a bank prefix in front, or the name the shop uses online. Each must land where the CSV style does.
    [Theory]
    [InlineData("kroger", -5000, AccountKind.Checking, "Groceries")]
    [InlineData("Kroger Fuel Center", -4000, AccountKind.CreditCard, "Gas and fuel")]
    [InlineData("POS DEBIT KROGER 0123 ANYTOWN", -5000, AccountKind.Checking, "Groceries")]
    [InlineData("PURCHASE AUTHORIZED ON 03/22 SHEETZ 0123 ANYTOWN PA", -3000, AccountKind.Checking, "Gas and fuel")]
    [InlineData("Netflix", -1549, AccountKind.CreditCard, "Subscriptions")]
    [InlineData("netflix.com", -1549, AccountKind.CreditCard, "Subscriptions")]
    [InlineData("Spotify USA", -1099, AccountKind.Checking, "Subscriptions")]
    [InlineData("Recurring Payment Spotify", -1099, AccountKind.Checking, "Subscriptions")]
    [InlineData("Amazon.com", -2500, AccountKind.CreditCard, "Shopping")]
    [InlineData("Amazon Prime Membership", -13900, AccountKind.CreditCard, "Subscriptions")]
    [InlineData("AMZN Mktp US", -2500, AccountKind.CreditCard, "Shopping")]
    [InlineData("Walmart", -4000, AccountKind.CreditCard, "Shopping")]
    [InlineData("Target", -4000, AccountKind.CreditCard, "Shopping")]
    [InlineData("McDonald's", -1200, AccountKind.CreditCard, "Dining")]
    [InlineData("Starbucks", -600, AccountKind.CreditCard, "Coffee")]
    [InlineData("DoorDash", -2500, AccountKind.CreditCard, "Delivery")]
    [InlineData("Uber Eats", -2500, AccountKind.CreditCard, "Delivery")]
    [InlineData("Uber Trip", -1500, AccountKind.CreditCard, "Rideshare and taxi")]
    [InlineData("Lyft", -1500, AccountKind.CreditCard, "Rideshare and taxi")]
    [InlineData("CVS Pharmacy", -1800, AccountKind.CreditCard, "Medical")]
    [InlineData("Walgreens", -1800, AccountKind.CreditCard, "Medical")]
    [InlineData("Verizon Wireless", -8000, AccountKind.Checking, "Internet and phone")]
    [InlineData("Comcast Xfinity", -9000, AccountKind.Checking, "Internet and phone")]
    [InlineData("Home Depot", -9000, AccountKind.CreditCard, "Home improvement")]
    [InlineData("Southwest Airlines", -19900, AccountKind.CreditCard, "Travel")]
    [InlineData("Delta Air Lines", -45000, AccountKind.CreditCard, "Travel")]
    [InlineData("Payroll - Acme Inc", 250000, AccountKind.Checking, "Salary")]
    [InlineData("Direct Deposit Acme Inc", 250000, AccountKind.Checking, "Salary")]
    [InlineData("Interest Paid", 123, AccountKind.Savings, "Interest and dividends")]
    [InlineData("IRS TREAS 310 TAX REF", 120000, AccountKind.Checking, "Tax refund")]
    public async Task Other_styles_of_the_same_description_land_in_the_same_place(string description, long cents, AccountKind kind, string category)
    {
        await Loaded();

        Classify(description, cents, kind).Category.Should().Be(category);
    }

    [Fact]
    public async Task A_line_no_rule_matches_is_marked_for_review_not_guessed()
    {
        await Loaded();

        Classify("TOTALLY UNKNOWN SHOP 77", -1000, AccountKind.CreditCard).MatchedARule.Should().BeFalse();
        Classify("KROGER #1", -1000, AccountKind.CreditCard).MatchedARule.Should().BeTrue();
    }
}
