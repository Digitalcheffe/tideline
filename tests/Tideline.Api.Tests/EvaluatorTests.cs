using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class EvaluatorTests : IDisposable
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

    private async Task<JsonElement> Evaluate() => await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");

    private async Task<List<JsonElement>> Flags(string state = "open") => (await Ok(HttpMethod.Get, $"/api/v1/flags?state={state}")).EnumerateArray().ToList();

    private static string Key(JsonElement f) => $"{f.GetProperty("kind").GetString()}|{f.GetProperty("subjectType").GetString()}|{f.GetProperty("subjectId").GetInt64()}|{f.GetProperty("period").GetString()}";

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        var first = await Evaluate();
        var second = await Evaluate();

        first.GetProperty("created").GetInt32().Should().BeGreaterThan(0, "the fixtures have lines tagged for review at least");
        second.GetProperty("created").GetInt32().Should().Be(0);
        second.GetProperty("resolved").GetInt32().Should().Be(0);
        second.GetProperty("found").GetInt32().Should().Be(first.GetProperty("found").GetInt32());
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'evaluator.run'").Should().Be(2);
    }

    [Fact]
    public async Task A_missing_bill_a_carried_card_balance_and_a_new_loan_payee_raise_exactly_those_alerts_acknowledging_quiets_them_and_paying_resolves()
    {
        await Evaluate();
        var baseline = (await Flags()).Where(f => f.GetProperty("severity").GetString() == "alert").Select(Key).ToHashSet();

        // a bill due on the 5th that nobody paid for March
        var water = (await Ok(HttpMethod.Post, "/api/v1/recurring", new
        {
            name = "Water", categoryId = Id("categories", "Utilities"), expectedMinCents = 4000, expectedMaxCents = 9000, dueDay = 5, firstDueOn = "2026-03-05",
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        // the card is paid in full and due on the 1st, and March ended with a balance nobody paid by April 1
        var card = Id("accounts", "Card");
        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{card}", new { card = new { isPayInFull = true, dueDay = 1 } });
        await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = card, postedOn = "2026-03-30", amountCents = -5_000_000, description = "BIG PURCHASE", type = "extra" }, HttpStatusCode.Created);
        // a payment that looks like a loan Tideline has never heard of
        var debt = (await Ok(HttpMethod.Post, "/api/v1/transactions", new
        {
            accountId = Id("accounts", "Checking"), postedOn = "2026-03-28", amountCents = -4_000, description = "AFFIRM PAYMENT 884", type = "required",
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        await Evaluate();
        var now = await Flags();
        var added = now.Where(f => f.GetProperty("severity").GetString() == "alert" && !baseline.Contains(Key(f))).ToList();

        added.Select(f => f.GetProperty("kind").GetString()).Should().BeEquivalentTo(
            FlagKindsOf("recurring_missing", "card_not_paid_in_full", "new_debt"), "exactly those three");
        var missing = added.Single(f => f.GetProperty("kind").GetString() == "recurring_missing");
        missing.GetProperty("subjectId").GetInt64().Should().Be(water);
        missing.GetProperty("period").GetString().Should().Be("2026-03");
        missing.GetProperty("title").GetString().Should().Be("Water not paid for Mar 2026");
        missing.GetProperty("detail").GetString().Should().Contain("$40.00 to $90.00").And.Contain("Mar 5");
        added.Single(f => f.GetProperty("kind").GetString() == "new_debt").GetProperty("subjectId").GetInt64().Should().Be(debt);
        added.Single(f => f.GetProperty("kind").GetString() == "card_not_paid_in_full").GetProperty("subjectId").GetInt64().Should().Be(card);

        // acknowledging stops it showing and it stays stopped when the evaluator runs again
        var cardFlag = added.Single(f => f.GetProperty("kind").GetString() == "card_not_paid_in_full").GetProperty("id").GetInt64();
        await Ok(HttpMethod.Post, $"/api/v1/flags/{cardFlag}/acknowledge", new { note = "paying it Friday" });
        (await Flags()).Select(Key).Should().NotContain(Key(added.Single(f => f.GetProperty("kind").GetString() == "card_not_paid_in_full")));
        var before = _api.Scalar<long>("SELECT COUNT(*) FROM flags");
        (await Evaluate()).GetProperty("created").GetInt32().Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM flags").Should().Be(before);
        var acked = (await Flags("all")).Single(f => f.GetProperty("id").GetInt64() == cardFlag);
        acked.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        acked.GetProperty("ackNote").GetString().Should().Be("paying it Friday");
        (await Ok(HttpMethod.Post, $"/api/v1/flags/{cardFlag}/acknowledge", new { }, HttpStatusCode.BadRequest)).GetProperty("detail").GetString().Should().Contain("already");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'flag.acknowledged'").Should().Be(1);

        // paying the bill resolves its flag
        var payment = (await Ok(HttpMethod.Post, "/api/v1/transactions", new
        {
            accountId = Id("accounts", "Checking"), postedOn = "2026-03-06", amountCents = -6_000, description = "WATER DEPT", type = "bill", categoryId = Id("categories", "Utilities"),
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        await Ok(HttpMethod.Patch, $"/api/v1/transactions/{payment}", new { version = await _api.VersionOf(payment), recurringId = water });

        var result = await Evaluate();

        result.GetProperty("resolved").GetInt32().Should().BeGreaterThan(0);
        var waterFlag = (await Flags("all")).Single(f => Key(f) == Key(missing));
        waterFlag.GetProperty("resolved").GetBoolean().Should().BeTrue();
        (await Flags()).Select(Key).Should().NotContain(Key(missing));
    }

    private static string[] FlagKindsOf(params string[] kinds) => kinds;

    [Fact]
    public async Task A_bill_paid_for_an_unusual_amount_is_a_warning_not_an_alert()
    {
        var water = (await Ok(HttpMethod.Post, "/api/v1/recurring", new
        {
            name = "Water", categoryId = Id("categories", "Utilities"), expectedMinCents = 4000, expectedMaxCents = 9000, dueDay = 5, firstDueOn = "2026-03-05",
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var line = (await Ok(HttpMethod.Post, "/api/v1/transactions", new
        {
            accountId = Id("accounts", "Checking"), postedOn = "2026-03-06", amountCents = -30_000, description = "WATER DEPT", type = "bill", categoryId = Id("categories", "Utilities"),
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        await Ok(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), recurringId = water });

        await Evaluate();

        var flag = (await Flags()).Single(f => f.GetProperty("kind").GetString() == "recurring_out_of_range" && f.GetProperty("subjectId").GetInt64() == water);
        flag.GetProperty("severity").GetString().Should().Be("warn");
        flag.GetProperty("detail").GetString().Should().Contain("$300.00");
    }

    [Fact]
    public async Task A_balance_that_disagrees_with_the_bank_is_an_alert_until_it_agrees_again()
    {
        var savings = Id("accounts", "Savings");
        await Evaluate();
        (await Flags()).Should().NotContain(f => f.GetProperty("kind").GetString() == "balance_mismatch", "the fixtures reconcile");

        _api.Execute("UPDATE balance_snapshots SET balance_cents = balance_cents + 500 WHERE account_id = @savings AND source = 'bank'", new { savings });
        await Evaluate();
        var flag = (await Flags()).Single(f => f.GetProperty("kind").GetString() == "balance_mismatch");
        flag.GetProperty("severity").GetString().Should().Be("alert");
        flag.GetProperty("detail").GetString().Should().Contain("$5.00");

        _api.Execute("UPDATE balance_snapshots SET balance_cents = balance_cents - 500 WHERE account_id = @savings AND source = 'bank'", new { savings });
        await Evaluate();
        (await Flags()).Should().NotContain(f => f.GetProperty("kind").GetString() == "balance_mismatch");
        (await Flags("all")).Single(f => f.GetProperty("kind").GetString() == "balance_mismatch").GetProperty("resolved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_import_is_flagged_until_a_later_good_one_of_the_same_file()
    {
        _api.Execute("""
            INSERT INTO import_batches (source, file_name, started_at, finished_at, status, error)
            VALUES ('csv', 'acct_9999_x.csv', '2026-03-30T10:00:00Z', '2026-03-30T10:00:01Z', 'failed', 'No account has the bank number 9999.')
            """);
        await Evaluate();
        var flag = (await Flags()).Single(f => f.GetProperty("kind").GetString() == "import_failed");
        flag.GetProperty("title").GetString().Should().Be("acct_9999_x.csv could not be imported");
        flag.GetProperty("detail").GetString().Should().Contain("9999");

        _api.Execute("""
            INSERT INTO import_batches (source, file_name, started_at, finished_at, status) VALUES ('csv', 'acct_9999_x.csv', '2026-03-31T10:00:00Z', '2026-03-31T10:00:01Z', 'done')
            """);
        await Evaluate();
        (await Flags()).Should().NotContain(f => f.GetProperty("kind").GetString() == "import_failed");
    }

    [Fact]
    public async Task A_big_one_time_item_is_listed_and_one_linked_to_a_plan_is_acknowledged_as_planned()
    {
        var plan = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Wood stove", targetCents = 100_000, targetOn = "2027-01-01" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        async Task<long> OneTime(string what)
        {
            var id = (await Ok(HttpMethod.Post, "/api/v1/transactions", new
            {
                accountId = Id("accounts", "Checking"), postedOn = "2026-03-20", amountCents = -60_000, description = what, type = "one_time",
            }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
            return id;
        }
        var dentist = await OneTime("DENTIST");
        var stove = await OneTime("STOVE SHOP");
        await Ok(HttpMethod.Patch, $"/api/v1/transactions/{stove}", new { version = await _api.VersionOf(stove), planId = plan });

        await Evaluate();

        var all = await Flags("all");
        var dentistFlag = all.Single(f => f.GetProperty("kind").GetString() == "one_time_review" && f.GetProperty("subjectId").GetInt64() == dentist);
        dentistFlag.GetProperty("acknowledged").GetBoolean().Should().BeFalse();
        var stoveFlag = all.Single(f => f.GetProperty("kind").GetString() == "one_time_review" && f.GetProperty("subjectId").GetInt64() == stove);
        stoveFlag.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        stoveFlag.GetProperty("ackNote").GetString().Should().Be("planned: Wood stove");

        // acknowledging the dentist takes it off the Bills attention list
        (await _api.Get("/api/v1/bills?year=2026")).GetProperty("oneTimeAttention").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("transactionId").GetInt64()).Should().Contain(dentist);
        await Ok(HttpMethod.Post, $"/api/v1/flags/{dentistFlag.GetProperty("id").GetInt64()}/acknowledge", new { note = "yes, I know about it" });
        (await _api.Get("/api/v1/bills?year=2026")).GetProperty("oneTimeAttention").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("transactionId").GetInt64()).Should().NotContain(dentist);
    }

    [Fact]
    public async Task Lines_tagged_review_are_counted_once_for_the_month()
    {
        await Evaluate();

        var flag = (await Flags()).Single(f => f.GetProperty("kind").GetString() == "review_needed");
        flag.GetProperty("title").GetString().Should().MatchRegex(@"^\d+ lines? tagged review$");
        flag.GetProperty("severity").GetString().Should().Be("info");
        flag.GetProperty("link").GetString().Should().Be("/lines?review=true");
    }

    [Fact]
    public async Task A_plan_that_has_slipped_is_flagged_and_a_payee_that_keeps_charging_is_noticed()
    {
        var savings = Id("accounts", "Savings");
        await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Roof", targetCents = 2_000_000, startOn = "2025-10-01", targetOn = "2026-10-01", progressMode = "account_balance", fundingAccountId = savings }, HttpStatusCode.Created);
        // a payee charging $19.99 in each of the last three whole months, never before
        foreach (var month in new[] { "2026-01-10", "2026-02-10", "2026-03-10" })
            await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = Id("accounts", "Checking"), postedOn = month, amountCents = -1_999, description = "STREAMCO PLUS", type = "extra" }, HttpStatusCode.Created);

        await Evaluate();

        var flags = await Flags();
        flags.Should().Contain(f => f.GetProperty("kind").GetString() == "plan_behind" && f.GetProperty("severity").GetString() == "alert");
        var recurring = flags.Single(f => f.GetProperty("kind").GetString() == "new_recurring");
        recurring.GetProperty("title").GetString().Should().Be("STREAMCO PLUS looks like a new recurring charge");
        recurring.GetProperty("detail").GetString().Should().Contain("$19.99");
    }

    [Fact]
    public async Task The_retirement_shortfall_getting_bigger_is_noticed_once_there_is_a_figure_to_compare_with()
    {
        await Ok(HttpMethod.Post, "/api/v1/people", new { name = "Alex", birthOn = "1987-04-01", retireAge = 65, socialSecurityAnnualTodayCents = 4_000_000 }, HttpStatusCode.Created);
        var account = (await Ok(HttpMethod.Post, "/api/v1/accounts", new { name = "401k", kind = "retirement", retirement = new { accountType = "401k", employeeMonthlyCents = 150_000 } }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        await Ok(HttpMethod.Post, $"/api/v1/accounts/{account}/snapshots", new { asOn = "2026-04-01", balanceCents = 10_000_000 }, HttpStatusCode.Created);
        await Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { goalAnnualIncomeTodayCents = 8_000_000 });

        await Evaluate();
        (await Flags()).Should().NotContain(f => f.GetProperty("kind").GetString() == "retirement_gap_grew", "nothing to compare with the first time");

        // pretend last month's shortfall was much smaller
        _api.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('retirement_gap:2026-03', '1000000', '2026-03-01T00:00:00Z')");
        await Evaluate();

        (await Flags()).Single(f => f.GetProperty("kind").GetString() == "retirement_gap_grew").GetProperty("detail").GetString().Should().Contain("up from $10,000.00");
    }

    [Fact]
    public async Task A_viewer_can_see_the_flags_a_member_can_acknowledge_and_only_an_owner_can_run_the_evaluators()
    {
        await Evaluate();
        var id = (await Flags()).First().GetProperty("id").GetInt64();
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);

        (await viewer.GetAsync("/api/v1/flags")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"/api/v1/flags/{id}/acknowledge", System.Net.Http.Json.JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync("/api/v1/notify/evaluate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync($"/api/v1/flags/{id}/acknowledge", System.Net.Http.Json.JsonContent.Create(new { note = "seen" }))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_overview_counts_and_lists_what_needs_you()
    {
        await Evaluate();

        var overview = await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31");

        var open = await Flags();
        overview.GetProperty("flags").GetProperty("info").GetInt32().Should().Be(open.Count(f => f.GetProperty("severity").GetString() == "info"));
        overview.GetProperty("needsYou").GetArrayLength().Should().Be(Math.Min(20, open.Count));
        overview.GetProperty("needsYou")[0].GetProperty("title").GetString().Should().NotBeNullOrEmpty();
    }
}
