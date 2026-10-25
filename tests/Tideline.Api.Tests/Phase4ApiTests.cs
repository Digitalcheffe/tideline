using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class Phase4ApiTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Id(string table, string name) => _api.Scalar<long>($"SELECT id FROM {table} WHERE name = @name", new { name });

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    private async Task Refused(HttpMethod method, string url, object? body, HttpStatusCode status, string mention)
    {
        var (got, json) = await _api.Send(method, url, body);
        got.Should().Be(status, json.ToString());
        json.GetProperty("detail").GetString().Should().Contain(mention);
    }

    private async Task<long> NewAccount(object body) => (await Ok(HttpMethod.Post, "/api/v1/accounts", body, HttpStatusCode.Created)).GetProperty("id").GetInt64();

    private async Task Snapshot(long account, string asOn, long cents, string source = "statement") =>
        await Ok(HttpMethod.Post, $"/api/v1/accounts/{account}/snapshots", new { asOn, balanceCents = cents, source }, HttpStatusCode.Created);

    // ---- net worth -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Net_worth_adds_money_accounts_and_takes_cards_as_what_is_owed()
    {
        var nw = await Ok(HttpMethod.Get, "/api/v1/net-worth");

        var assets = nw.GetProperty("assets").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("cents").GetInt64());
        assets["Checking"].Should().Be(_api.Scalar<long>("SELECT COALESCE(opening_balance_cents, 0) + (SELECT SUM(amount_cents) FROM transactions WHERE account_id = accounts.id) FROM accounts WHERE name = 'Checking'"));
        assets["Savings"].Should().Be(530_125);
        nw.GetProperty("assetsCents").GetInt64().Should().Be(assets.Values.Sum());
        nw.GetProperty("netWorthCents").GetInt64().Should().Be(nw.GetProperty("assetsCents").GetInt64() - nw.GetProperty("liabilitiesCents").GetInt64());
        nw.GetProperty("asOf").GetString().Should().Be("2026-04-01");
        nw.GetProperty("trend").EnumerateArray().Last().GetProperty("netWorthCents").GetInt64().Should().Be(nw.GetProperty("netWorthCents").GetInt64());
    }

    [Fact]
    public async Task A_loan_and_the_car_it_is_secured_by_give_equity_and_a_countdown_that_matches_the_spec()
    {
        var carPayments = (await Ok(HttpMethod.Post, "/api/v1/categories", new { name = "Car payment", defaultType = "bill" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var car = await NewAccount(new { name = "Car", kind = "asset" });
        var loan = await NewAccount(new { name = "Car loan", kind = "loan", loan = new { loanType = "auto", rateBps = 500, paymentCents = 56_900, paymentCategoryId = carPayments, securedAssetId = car } });
        await Snapshot(car, "2026-03-01", 3_000_000);
        await Snapshot(loan, "2026-03-01", 2_000_000);

        var nw = await Ok(HttpMethod.Get, "/api/v1/net-worth");

        var l = nw.GetProperty("loans").EnumerateArray().Single();
        l.GetProperty("balanceCents").GetInt64().Should().Be(2_000_000);
        l.GetProperty("payoff").GetProperty("monthsLeft").GetDouble().Should().BeApproximately(38.09, 0.005);
        l.GetProperty("payoff").GetProperty("interestLeftCents").GetInt64().Should().Be(167_038);
        l.GetProperty("payoff").GetProperty("payoffMonth").GetString().Should().Be("2029-07", "this month (2026-04) plus 39");
        var equity = nw.GetProperty("equity").EnumerateArray().Single();
        equity.GetProperty("equityCents").GetInt64().Should().Be(1_000_000);
        nw.GetProperty("liabilities").EnumerateArray().Should().Contain(x => x.GetProperty("name").GetString() == "Car loan" && x.GetProperty("cents").GetInt64() == 2_000_000);

        var effect = await Ok(HttpMethod.Get, $"/api/v1/loans/{loan}/countdown?extraCents=10000");
        effect.GetProperty("with").GetProperty("monthsLeft").GetDouble().Should().BeApproximately(31.99, 0.005);
        effect.GetProperty("monthsSaved").GetDouble().Should().Be(6.09);
        effect.GetProperty("interestSavedCents").GetInt64().Should().Be(167_038 - 140_429);
    }

    [Fact]
    public async Task A_payment_in_the_loans_category_moves_the_balance_on_and_a_statement_figure_wins_again()
    {
        var category = (await Ok(HttpMethod.Post, "/api/v1/categories", new { name = "Car payment", defaultType = "bill" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var loan = await NewAccount(new { name = "Car loan", kind = "loan", loan = new { loanType = "auto", rateBps = 500, paymentCents = 56_900, paymentCategoryId = category } });
        await Snapshot(loan, "2026-02-01", 2_000_000);
        async Task<long> Balance() => (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("loans")[0].GetProperty("balanceCents").GetInt64();

        await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = Id("accounts", "Checking"), postedOn = "2026-03-05", amountCents = -56_900, description = "CAR LOAN PAYMENT", categoryId = category, type = "bill" }, HttpStatusCode.Created);

        (await Balance()).Should().Be(1_951_433, "B * (1 + r) - P");
        var computed = (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("loans")[0];
        computed.GetProperty("source").GetString().Should().Be("computed");
        computed.GetProperty("asOf").GetString().Should().Be("2026-03-05");

        await Snapshot(loan, "2026-03-20", 1_940_000); // the statement says otherwise, and it is later
        (await Balance()).Should().Be(1_940_000);
    }

    [Fact]
    public async Task The_trend_starts_once_every_loan_asset_and_retirement_account_has_a_figure()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        var loan = await NewAccount(new { name = "Mortgage", kind = "loan" });
        await Snapshot(loan, "2025-11-01", 20_000_000);   // owed from November...
        await Snapshot(house, "2026-02-01", 30_000_000);  // ...but the house is only entered in February

        var trend = (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("trend").EnumerateArray().ToList();

        trend.First().GetProperty("month").GetString().Should().Be("2026-02", "before that the trend would swing by the whole value of the house");
        trend.Select(t => t.GetProperty("netWorthCents").GetInt64()).Should().OnlyContain(v => v > -10_000_000);
    }

    [Fact]
    public async Task A_loan_whose_payment_does_not_cover_the_interest_says_so_instead_of_failing()
    {
        var loan = await NewAccount(new { name = "Hard loan", kind = "loan", loan = new { loanType = "personal", rateBps = 1200, paymentCents = 1_000 } });
        await Snapshot(loan, "2026-03-01", 1_000_000);

        var l = (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("loans")[0];

        l.GetProperty("payoff").GetProperty("paysOff").GetBoolean().Should().BeFalse();
        l.GetProperty("payoff").GetProperty("payoffMonth").ValueKind.Should().Be(JsonValueKind.Null);
        var effect = await Ok(HttpMethod.Get, $"/api/v1/loans/{loan}/countdown?extraCents=50000");
        effect.GetProperty("with").GetProperty("paysOff").GetBoolean().Should().BeTrue();
        effect.GetProperty("monthsSaved").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_old_estimate_is_stale_and_every_figure_has_its_date()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        await Snapshot(house, "2025-10-01", 30_000_000, "estimate");

        var nw = await Ok(HttpMethod.Get, "/api/v1/net-worth");

        var line = nw.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "House");
        line.GetProperty("asOf").GetString().Should().Be("2025-10-01");
        line.GetProperty("source").GetString().Should().Be("estimate");
        line.GetProperty("stale").GetBoolean().Should().BeTrue();

        await Snapshot(house, "2026-03-30", 31_000_000, "statement");
        var fresh = (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "House");
        fresh.GetProperty("cents").GetInt64().Should().Be(31_000_000);
        fresh.GetProperty("stale").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Missing_inputs_are_named_rather_than_guessed()
    {
        await NewAccount(new { name = "House", kind = "asset" });
        await NewAccount(new { name = "Mortgage", kind = "loan" });

        var needs = (await Ok(HttpMethod.Get, "/api/v1/net-worth")).GetProperty("needsInput").EnumerateArray().Select(n => n.GetString()).ToList();

        needs.Should().Contain("Enter what House is worth.").And.Contain("Enter what is owed on Mortgage.").And.Contain("Enter the rate and the monthly payment for Mortgage.");
    }

    [Fact]
    public async Task Typed_in_balances_are_checked_audited_and_can_be_removed()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        await Refused(HttpMethod.Post, $"/api/v1/accounts/{Id("accounts", "Checking")}/snapshots", new { asOn = "2026-03-01", balanceCents = 1 }, HttpStatusCode.BadRequest, "come from the bank");
        await Refused(HttpMethod.Post, $"/api/v1/accounts/{house}/snapshots", new { asOn = "2026-03-01", balanceCents = -5 }, HttpStatusCode.BadRequest, "zero or more");
        await Refused(HttpMethod.Post, $"/api/v1/accounts/{house}/snapshots", new { asOn = "2027-01-01", balanceCents = 5 }, HttpStatusCode.BadRequest, "not happened");
        await Refused(HttpMethod.Post, $"/api/v1/accounts/{house}/snapshots", new { balanceCents = 5 }, HttpStatusCode.BadRequest, "which day");
        await Refused(HttpMethod.Post, $"/api/v1/accounts/{house}/snapshots", new { asOn = "2026-03-01", balanceCents = 5, source = "bank" }, HttpStatusCode.BadRequest, "statement figure or an estimate");
        await Refused(HttpMethod.Post, "/api/v1/accounts/99999/snapshots", new { asOn = "2026-03-01", balanceCents = 5 }, HttpStatusCode.NotFound, "no such account");

        await Snapshot(house, "2026-03-01", 100);
        await Snapshot(house, "2026-03-01", 200); // same day, same source: replaces
        var list = await Ok(HttpMethod.Get, $"/api/v1/accounts/{house}/snapshots");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("balanceCents").GetInt64().Should().Be(200);
        var entry = (await _api.Get("/api/v1/audit?action=snapshot.saved")).GetProperty("items")[0];
        entry.GetProperty("beforeJson").GetString().Should().Contain("100");
        entry.GetProperty("afterJson").GetString().Should().Contain("200");

        await Ok(HttpMethod.Delete, $"/api/v1/accounts/{house}/snapshots/{list[0].GetProperty("id").GetInt64()}");
        (await Ok(HttpMethod.Get, $"/api/v1/accounts/{house}/snapshots")).GetArrayLength().Should().Be(0);
    }

    // ---- retirement ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retirement_says_what_to_fill_in_and_then_matches_the_spec_example_to_the_cent()
    {
        var empty = await Ok(HttpMethod.Get, "/api/v1/retirement");
        empty.GetProperty("ready").GetBoolean().Should().BeFalse();
        empty.GetProperty("missing").GetArrayLength().Should().BeGreaterThan(2);
        empty.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);

        // today is 1 April 2026; born in April 1987 and retiring at 65 is 26 years, 312 months, away
        await Ok(HttpMethod.Post, "/api/v1/people", new { name = "Alex", birthOn = "1987-04-01", retireAge = 65, socialSecurityAnnualTodayCents = 4_000_000 }, HttpStatusCode.Created);
        var account = await NewAccount(new { name = "401k", kind = "retirement", retirement = new { accountType = "401k", employeeMonthlyCents = 100_000, employerMonthlyCents = 50_000 } });
        await Snapshot(account, "2026-04-01", 10_000_000);
        await Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { goalAnnualIncomeTodayCents = 8_000_000 });

        var r = await Ok(HttpMethod.Get, "/api/v1/retirement");

        r.GetProperty("missing").GetArrayLength().Should().Be(0, r.GetProperty("missing").ToString());
        r.GetProperty("ready").GetBoolean().Should().BeTrue();
        r.GetProperty("monthsToRetirement").GetInt32().Should().Be(312);
        var result = r.GetProperty("result");
        result.GetProperty("projectedCents").GetInt64().Should().Be(159_614_375);
        result.GetProperty("requiredCents").GetInt64().Should().Be(215_659_127);
        result.GetProperty("deficitCents").GetInt64().Should().Be(56_044_752);
        result.GetProperty("deficitTodayCents").GetInt64().Should().Be(25_987_656);
        result.GetProperty("extraMonthlyNeededCents").GetInt64().Should().Be(74_919);
        result.GetProperty("moneyLastsYears").GetDouble().Should().BeApproximately(20.49, 0.005);
        r.GetProperty("retireAtAge").GetInt32().Should().BeGreaterThan(65);
        r.GetProperty("chart")[0].GetProperty("balanceCents").GetInt64().Should().Be(10_000_000);
        r.GetProperty("chart").EnumerateArray().Last().GetProperty("balanceCents").GetInt64().Should().Be(159_614_375);
    }

    [Fact]
    public async Task Changing_an_assumption_changes_the_answer_and_bad_ones_are_refused_and_audited()
    {
        await Ok(HttpMethod.Post, "/api/v1/people", new { name = "Alex", birthOn = "1987-04-01", retireAge = 65, socialSecurityAnnualTodayCents = 4_000_000 }, HttpStatusCode.Created);
        var account = await NewAccount(new { name = "401k", kind = "retirement", retirement = new { accountType = "401k", employeeMonthlyCents = 150_000 } });
        await Snapshot(account, "2026-04-01", 10_000_000);
        await Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { goalAnnualIncomeTodayCents = 8_000_000 });
        var before = (await Ok(HttpMethod.Get, "/api/v1/retirement")).GetProperty("result").GetProperty("deficitCents").GetInt64();

        await Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { returnBeforeBps = 800 });

        (await Ok(HttpMethod.Get, "/api/v1/retirement")).GetProperty("result").GetProperty("deficitCents").GetInt64().Should().BeLessThan(before);
        (await _api.Get("/api/v1/audit?action=retirement_goal.saved")).GetProperty("items")[0].GetProperty("beforeJson").GetString().Should().Contain("600");
        await Refused(HttpMethod.Put, "/api/v1/retirement/goal", new { withdrawalRateBps = 5 }, HttpStatusCode.BadRequest, "withdrawal rate");
        await Refused(HttpMethod.Put, "/api/v1/retirement/goal", new { returnBeforeBps = 9000 }, HttpStatusCode.BadRequest, "25%");
        await Refused(HttpMethod.Post, "/api/v1/people", new { name = "", retireAge = 65 }, HttpStatusCode.BadRequest, "name");
        await Refused(HttpMethod.Post, "/api/v1/people", new { name = "Old", retireAge = 5 }, HttpStatusCode.BadRequest, "between 30 and 100");
    }

    [Fact]
    public async Task An_account_left_out_of_the_goal_is_not_counted()
    {
        await Ok(HttpMethod.Post, "/api/v1/people", new { name = "Alex", birthOn = "1987-04-01", retireAge = 65, socialSecurityAnnualTodayCents = 4_000_000 }, HttpStatusCode.Created);
        var a = await NewAccount(new { name = "401k", kind = "retirement", retirement = new { accountType = "401k", employeeMonthlyCents = 150_000 } });
        var hsa = await NewAccount(new { name = "HSA", kind = "retirement", retirement = new { accountType = "hsa", isIncludedInGoal = false } });
        await Snapshot(a, "2026-04-01", 10_000_000);
        await Snapshot(hsa, "2026-04-01", 99_999_999);
        await Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { goalAnnualIncomeTodayCents = 8_000_000 });

        (await Ok(HttpMethod.Get, "/api/v1/retirement")).GetProperty("result").GetProperty("projectedCents").GetInt64().Should().Be(159_614_375);
    }

    // ---- plans ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_funded_from_an_account_is_on_track_or_behind_and_shows_on_the_overview()
    {
        var savings = Id("accounts", "Savings"); // $5,301.25 today
        var good = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Wood stove", targetCents = 1_000_000, startOn = "2025-10-01", targetOn = "2026-10-01", progressMode = "account_balance", fundingAccountId = savings }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var bad = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Roof", targetCents = 2_000_000, startOn = "2025-10-01", targetOn = "2026-10-01", progressMode = "account_balance", fundingAccountId = savings }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        var plans = (await Ok(HttpMethod.Get, "/api/v1/plans")).EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);

        plans["Wood stove"].GetProperty("actualCents").GetInt64().Should().Be(530_125);
        plans["Wood stove"].GetProperty("expectedCents").GetInt64().Should().Be(500_000, "six of twelve months gone, linear");
        plans["Wood stove"].GetProperty("status").GetString().Should().Be("on_track");
        plans["Wood stove"].GetProperty("neededPerMonthCents").GetInt64().Should().Be((1_000_000 - 530_125 + 5) / 6);
        plans["Roof"].GetProperty("status").GetString().Should().Be("behind");
        plans["Roof"].GetProperty("trouble").GetProperty("status").GetString().Should().Be("behind");

        var overview = (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("plans").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        overview.Keys.Should().BeEquivalentTo("Wood stove", "Roof");
        overview["Roof"].GetProperty("status").GetString().Should().Be("behind");

        await Ok(HttpMethod.Patch, $"/api/v1/plans/{bad}", new { isTrackable = false });
        (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("plans").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Wood stove");
        _ = good;
    }

    [Fact]
    public async Task A_plan_counted_from_linked_lines_follows_the_savings_lines_you_link()
    {
        var plan = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Kid fund", targetCents = 1_200_000, startOn = "2026-01-01", targetOn = "2027-01-01", startAmountCents = 100_000, progressMode = "linked_lines" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var line = _api.IdOf("MYSTERY VENDOR 9");
        var amount = (await _api.Get($"/api/v1/transactions/{line}")).GetProperty("bank").GetProperty("amountCents").GetInt64();

        (await Ok(HttpMethod.Get, "/api/v1/plans")).EnumerateArray().Single().GetProperty("actualCents").GetInt64().Should().Be(100_000);

        await Ok(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), type = "savings", planId = plan });

        var p = (await Ok(HttpMethod.Get, "/api/v1/plans")).EnumerateArray().Single();
        p.GetProperty("actualCents").GetInt64().Should().Be(100_000 - amount);
        p.GetProperty("linkedLines").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Bad_plans_are_refused_and_changes_are_audited()
    {
        var savings = Id("accounts", "Savings");
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "", targetCents = 1, targetOn = "2027-01-01" }, HttpStatusCode.BadRequest, "name");
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 0, targetOn = "2027-01-01" }, HttpStatusCode.BadRequest, "more than zero");
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 5, startOn = "2027-02-01", targetOn = "2027-01-01" }, HttpStatusCode.BadRequest, "after the start");
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 5, targetOn = "2027-01-01", progressMode = "account_balance" }, HttpStatusCode.BadRequest, "needs the account");
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 5, targetOn = "2027-01-01", progressMode = "guess" }, HttpStatusCode.BadRequest, "balance or from lines");

        var id = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 5_000, startOn = "2026-01-01", targetOn = "2027-01-01", progressMode = "account_balance", fundingAccountId = savings }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        await Refused(HttpMethod.Post, "/api/v1/plans", new { name = "X", targetCents = 5, targetOn = "2027-01-01" }, HttpStatusCode.Conflict, "already used");
        await Ok(HttpMethod.Patch, $"/api/v1/plans/{id}", new { targetCents = 9_000, status = "done" });
        var entry = (await _api.Get("/api/v1/audit?action=plan.updated")).GetProperty("items")[0];
        entry.GetProperty("beforeJson").GetString().Should().Contain("5000").And.Contain("active");
        entry.GetProperty("afterJson").GetString().Should().Contain("9000").And.Contain("done");
        (await Ok(HttpMethod.Get, "/api/v1/plans")).EnumerateArray().Single().GetProperty("status").GetString().Should().Be("done");
        await Refused(HttpMethod.Patch, "/api/v1/plans/99999", new { targetCents = 1 }, HttpStatusCode.NotFound, "no such plan");
    }

    // ---- budget ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Budget_guidelines_pop_when_over_and_every_figure_drills_to_its_lines()
    {
        _api.Clock.Advance(TimeSpan.FromHours(-20)); // 31 March 2026: the month with data
        var groceries = Id("categories", "Groceries");
        var dining = Id("categories", "Rent");
        var utilities = Id("categories", "Utilities");

        await Ok(HttpMethod.Put, "/api/v1/budgets/2026", new
        {
            expectedIncomeCents = 12_000_000,
            lines = new object[]
            {
                new { categoryId = groceries, monthlyCents = 100 },    // $1 a month: certainly over
                new { categoryId = dining, monthlyCents = 99_999_999 }, // enormous: certainly not
                new { categoryId = utilities, annualCents = 100 },
            },
        });

        var budget = await Ok(HttpMethod.Get, "/api/v1/budgets/2026");
        var rows = budget.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);

        budget.GetProperty("month").GetString().Should().Be("2026-03");
        rows["Groceries"].GetProperty("popped").GetBoolean().Should().BeTrue();
        rows["Rent"].GetProperty("popped").GetBoolean().Should().BeFalse();
        rows["Utilities"].GetProperty("popped").GetBoolean().Should().BeTrue("an annual guideline of $1 is passed by the year so far");
        foreach (var row in new[] { rows["Groceries"], rows["Utilities"] })
            foreach (var figure in new[] { row.GetProperty("thisMonth"), row.GetProperty("ytd") })
            {
                var (count, sum) = await _api.Drill(figure.Drill());
                count.Should().BeGreaterThan(0);
                Math.Abs(sum).Should().Be(figure.Cents(), "the figure is what its lines add up to");
            }
        var income = budget.GetProperty("income");
        income.GetProperty("actualYtd").Cents().Should().Be((await _api.Get("/api/v1/overview?from=2026-01-01&to=2026-03-31")).GetProperty("spread").GetProperty("in").Cents());
    }

    [Fact]
    public async Task Income_is_short_when_a_finished_year_came_in_under_ninety_percent_of_what_was_expected()
    {
        // 2025 is over (twelve months have gone) and the fixtures have no income in it
        await Ok(HttpMethod.Put, "/api/v1/budgets/2025", new { expectedIncomeCents = 900_000_000 });
        (await Ok(HttpMethod.Get, "/api/v1/budgets/2025")).GetProperty("income").GetProperty("short").GetBoolean().Should().BeTrue();

        await Ok(HttpMethod.Put, "/api/v1/budgets/2025", new { clearExpectedIncome = true });
        (await Ok(HttpMethod.Get, "/api/v1/budgets/2025")).GetProperty("income").GetProperty("short").GetBoolean().Should().BeFalse("no expected income, nothing to fall short of");
    }

    [Fact]
    public async Task Fill_from_averages_proposes_but_saves_nothing_and_rounds_up_to_ten_dollars()
    {
        var groceries = Id("categories", "Groceries");
        var budget = await Ok(HttpMethod.Get, "/api/v1/budgets/2026");
        var marchGroceries = budget.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Groceries").GetProperty("ytd").Cents();

        var (status, proposal) = await _api.Send(HttpMethod.Post, "/api/v1/budgets/2026/fill-from-averages");

        status.Should().Be(HttpStatusCode.OK);
        proposal.GetProperty("from").GetString().Should().Be("2025-04-01");
        proposal.GetProperty("to").GetString().Should().Be("2026-03-31");
        var item = proposal.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("categoryId").GetInt64() == groceries);
        item.GetProperty("twelveMonthCents").GetInt64().Should().Be(marchGroceries);
        var average = (marchGroceries + 11) / 12;
        item.GetProperty("monthlyCents").GetInt64().Should().Be((average + 999) / 1000 * 1000).And.BeGreaterThanOrEqualTo(average);
        _api.Scalar<long>("SELECT COUNT(*) FROM budget_lines").Should().Be(0, "a proposal is not saved until a person saves it");

        // accepting it is a normal save of the lines
        await Ok(HttpMethod.Put, "/api/v1/budgets/2026", new { lines = proposal.GetProperty("items").EnumerateArray().Select(i => new { categoryId = i.GetProperty("categoryId").GetInt64(), monthlyCents = i.GetProperty("monthlyCents").GetInt64() }).ToArray() });
        _api.Scalar<long>("SELECT COUNT(*) FROM budget_lines").Should().BeGreaterThan(0);
        (await _api.Get("/api/v1/audit?action=budget.saved")).GetProperty("items")[0].GetProperty("afterJson").GetString().Should().Contain("monthlyCents");
    }

    [Fact]
    public async Task Bad_budgets_are_refused()
    {
        var groceries = Id("categories", "Groceries");
        await Refused(HttpMethod.Put, "/api/v1/budgets/1900", new { expectedIncomeCents = 1 }, HttpStatusCode.BadRequest, "sensible year");
        await Refused(HttpMethod.Put, "/api/v1/budgets/2026", new { expectedIncomeCents = -1 }, HttpStatusCode.BadRequest, "negative");
        await Refused(HttpMethod.Put, "/api/v1/budgets/2026", new { lines = new[] { new { categoryId = groceries, monthlyCents = 1 }, new { categoryId = groceries, monthlyCents = 2 } } }, HttpStatusCode.BadRequest, "only be in the budget once");
        await Refused(HttpMethod.Put, "/api/v1/budgets/2026", new { lines = new[] { new { categoryId = 99999L, monthlyCents = 1 } } }, HttpStatusCode.BadRequest, "no category");
        await Refused(HttpMethod.Put, "/api/v1/budgets/2026", new { lines = new[] { new { categoryId = groceries, monthlyCents = 1, annualCents = 2 } } }, HttpStatusCode.BadRequest, "not both");
    }

    // ---- savings --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Savings_shows_balances_deposits_interest_and_whether_a_plan_is_on_pace()
    {
        var savings = Id("accounts", "Savings");
        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{savings}", new { interestRateBps = 480 });
        await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Far goal", targetCents = 50_000_000, startOn = "2026-01-01", targetOn = "2027-04-01", progressMode = "account_balance", fundingAccountId = savings }, HttpStatusCode.Created);

        var s = await Ok(HttpMethod.Get, "/api/v1/savings");

        var account = s.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Savings");
        account.GetProperty("balanceCents").GetInt64().Should().Be(530_125);
        account.GetProperty("rateBps").GetInt32().Should().Be(480);
        account.GetProperty("interestPerMonthCents").GetInt64().Should().Be(2_121, "5,301.25 at 4.80% a year, a month's worth, rounded");
        account.GetProperty("deposits").GetArrayLength().Should().Be(12);
        var march = account.GetProperty("deposits").EnumerateArray().Single(d => d.GetProperty("month").GetString() == "2026-03");
        if (march.GetProperty("cents").GetInt64() > 0)
        {
            var (count, sum) = await _api.Drill(march.GetProperty("drill").GetString());
            count.Should().BeGreaterThan(0);
            sum.Should().Be(march.GetProperty("cents").GetInt64(), "the deposits figure is the money in that month");
        }
        var goal = s.GetProperty("goals").EnumerateArray().Single();
        goal.GetProperty("onPace").GetBoolean().Should().BeFalse();
        goal.GetProperty("projectedAtTargetCents").GetInt64().Should().BeGreaterThan(530_125).And.BeLessThan(50_000_000);
        s.GetProperty("totalBalanceCents").GetInt64().Should().Be(530_125);
    }

    // ---- who may do what ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_reads_everything_and_edits_plans_and_budgets_but_account_figures_are_the_owners()
    {
        var (_, secret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, secret);
        var house = await NewAccount(new { name = "House", kind = "asset" });

        foreach (var url in new[] { "/api/v1/net-worth", "/api/v1/retirement", "/api/v1/plans", "/api/v1/budgets/2026", "/api/v1/savings", "/api/v1/people" })
            (await member.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);

        (await member.PostAsJsonAsync("/api/v1/plans", new { name = "Mine", targetCents = 1_000, targetOn = "2027-01-01" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await member.PutAsJsonAsync("/api/v1/budgets/2026", new { expectedIncomeCents = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.PostAsJsonAsync($"/api/v1/accounts/{house}/snapshots", new { asOn = "2026-03-01", balanceCents = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
