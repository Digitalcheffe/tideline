using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

/// <summary>
/// The "needs a category" pile (issue #63): a live count that opens the lines behind it, a quick answer, and a question for every check.
/// The fixture has three lines the rules could not place: MYSTERY VENDOR 9 (-20.00), INTEREST PAID (+1.25) and COFFEE SHOP (-4.50).
/// </summary>
public sealed class NeedsCategoryTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private Task<JsonElement> Needs() => _api.Get("/api/v1/needs-category");

    private async Task<JsonElement> Answer(long id, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(HttpMethod.Post, $"/api/v1/transactions/{id}/answer", body);
        status.Should().Be(expected, json.ToString());
        return json;
    }

    /// <summary>A check the rules cannot name: the bank says only a number and an amount, so it lands in the catch-all.</summary>
    private async Task<long> AddUnnamedCheck(string description = "Check #1099", long cents = -12000)
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/transactions",
            new { accountId = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'"), postedOn = "2026-03-20", amountCents = cents, description, categoryId = Category("Uncategorized") });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private async Task<List<JsonElement>> OpenChecks() =>
        (await _api.Get("/api/v1/flags")).EnumerateArray().Where(f => f.GetProperty("kind").GetString() == "check_unknown").ToList();

    private async Task<List<string?>> Tags(long id) =>
        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();

    // ---- the count and the lines behind it ------------------------------------------------------------------------

    [Fact]
    public async Task The_count_is_live_and_its_drill_opens_exactly_those_lines()
    {
        var n = await Needs();

        n.GetProperty("count").GetInt64().Should().Be(3);
        n.GetProperty("sumCents").GetInt64().Should().Be(-2000 + 125 - 450);
        n.GetProperty("checks").GetInt64().Should().Be(0);
        var (count, sum) = await _api.Drill(n.GetProperty("drill").GetString());
        (count, sum).Should().Be((3, -2325), "every number opens the lines behind it");

        var lines = await _api.Get("/api/v1/lines?from=2026-03-01&to=2026-03-31&needs=true");
        lines.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString())
            .Should().BeEquivalentTo(["MYSTERY VENDOR 9", "INTEREST PAID", "COFFEE SHOP"]);
    }

    [Fact]
    public async Task A_line_with_no_category_counts_and_a_transfer_or_a_left_out_line_does_not()
    {
        var check = await AddUnnamedCheck();
        (await Needs()).GetProperty("count").GetInt64().Should().Be(4);

        await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { check }, reason = "test" });
        (await Needs()).GetProperty("count").GetInt64().Should().Be(3, "a line left out of the totals is not waiting for an answer");

        var move = _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE move_id IS NOT NULL");
        move.Should().BeGreaterThan(0, "the fixture has transfers, which never have a category");
        (await _api.Get("/api/v1/lines?from=2026-01-01&to=2026-12-31&needs=true&move=include"))
            .GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("moveId").ValueKind == JsonValueKind.Null);
    }

    // ---- answering ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Answering_sets_the_category_its_type_and_the_payee_drops_review_and_takes_the_line_out_of_the_pile()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        (await Tags(id)).Should().Contain("review");

        await Answer(id, new { categoryId = Category("Groceries"), payee = "Corner Shop" });

        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("categoryName").GetString().Should().Be("Groceries");
        t.GetProperty("type").GetString().Should().Be("required", "the type is the category's own");
        t.GetProperty("payee").GetString().Should().Be("Corner Shop");
        t.GetProperty("isManualOverride").GetBoolean().Should().BeTrue("rules never overwrite an answer");
        (await Tags(id)).Should().NotContain("review");
        (await Needs()).GetProperty("count").GetInt64().Should().Be(2);

        var audit = (await _api.Get("/api/v1/audit?action=transaction.update")).GetProperty("items")[0];
        audit.GetProperty("beforeJson").GetString().Should().Contain("review");
        audit.GetProperty("afterJson").GetString().Should().Contain("required").And.NotContain("review");
    }

    [Fact]
    public async Task A_bad_answer_is_refused_with_a_reason_and_a_stale_one_cannot_overwrite_a_good_one()
    {
        var id = _api.IdOf("COFFEE SHOP");

        (await Answer(id, new { categoryId = Category("Uncategorized") }, HttpStatusCode.BadRequest)).GetProperty("detail").GetString().Should().Contain("real category");
        (await Answer(id, new { categoryId = 987654 }, HttpStatusCode.BadRequest)).GetProperty("detail").GetString().Should().Contain("no category");
        (await Answer(id, new { payee = "x" }, HttpStatusCode.BadRequest)).GetProperty("detail").GetString().Should().Contain("Pick a category");
        (await Needs()).GetProperty("count").GetInt64().Should().Be(3, "nothing changed");

        await Answer(id, new { categoryId = Category("Groceries") });
        (await Answer(id, new { categoryId = Category("Utilities") }, HttpStatusCode.Conflict)).GetProperty("detail").GetString().Should().Contain("already has a category");
        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("categoryName").GetString().Should().Be("Groceries");
    }

    [Fact]
    public async Task Giving_a_line_a_category_by_the_other_routes_also_answers_review_unless_the_same_save_sets_the_tags()
    {
        var mystery = _api.IdOf("MYSTERY VENDOR 9");
        var coffee = _api.IdOf("COFFEE SHOP");
        var interest = _api.IdOf("INTEREST PAID");

        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{mystery}", new { version = await _api.VersionOf(mystery), categoryId = Category("Groceries") });
        (await Tags(mystery)).Should().NotContain("review", "a category sent without tags answers review");

        await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new[] { coffee }, set = new { categoryId = Category("Groceries") } });
        (await Tags(coffee)).Should().NotContain("review", "the bulk change does the same");

        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{interest}",
            new { version = await _api.VersionOf(interest), categoryId = Category("Salary"), tags = new[] { "review", "look-again" } });
        (await Tags(interest)).Should().Contain("review", "tags a person sends themselves are theirs");
        (await Needs()).GetProperty("count").GetInt64().Should().Be(1, "only the line that still carries the tag waits");
    }

    // ---- checks ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unnamed_check_raises_its_own_alert_and_answering_clears_it_at_once()
    {
        var check = await AddUnnamedCheck("Check #1099", -12000);

        await _api.Send(HttpMethod.Post, "/api/v1/notify/evaluate");

        var flag = (await OpenChecks()).Should().ContainSingle().Subject;
        flag.GetProperty("title").GetString().Should().Be("Check #1099, $120.00, Mar 20: what was it?");
        flag.GetProperty("severity").GetString().Should().Be("warn");
        flag.GetProperty("link").GetString().Should().Be("/needs");
        (await Needs()).GetProperty("checks").GetInt64().Should().Be(1);

        await Answer(check, new { categoryId = Category("Rent"), payee = "Landlord" });

        (await OpenChecks()).Should().BeEmpty("it clears when the line is answered, not at the next nightly run");
        (await Needs()).GetProperty("checks").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Each_check_is_asked_about_on_its_own_and_one_that_was_already_named_is_not_asked_about()
    {
        await AddUnnamedCheck("Check #1100", -5000);
        await AddUnnamedCheck("Check #1101", -7500);

        await _api.Send(HttpMethod.Post, "/api/v1/notify/evaluate");

        var titles = (await OpenChecks()).Select(f => f.GetProperty("title").GetString()).ToList();
        titles.Should().BeEquivalentTo(["Check #1100, $50.00, Mar 20: what was it?", "Check #1101, $75.00, Mar 20: what was it?"],
            "the rent check in the fixture was named by a rule, so it is not here");
    }

    [Fact]
    public async Task A_check_that_was_answered_by_hand_in_the_ledger_also_clears_its_alert()
    {
        var check = await AddUnnamedCheck();
        await _api.Send(HttpMethod.Post, "/api/v1/notify/evaluate");
        (await OpenChecks()).Should().HaveCount(1);

        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{check}", new { version = await _api.VersionOf(check), categoryId = Category("Utilities") });

        (await OpenChecks()).Should().BeEmpty();
    }

    // ---- who may ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_see_the_count_but_not_answer_and_a_member_can_answer()
    {
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);
        var id = _api.IdOf("COFFEE SHOP");
        var body = JsonContent(new { categoryId = Category("Groceries") });

        (await viewer.GetAsync("/api/v1/needs-category")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"/api/v1/transactions/{id}/answer", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync($"/api/v1/transactions/{id}/answer", JsonContent(new { categoryId = Category("Groceries") }))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static System.Net.Http.Json.JsonContent JsonContent(object o) => System.Net.Http.Json.JsonContent.Create(o);
}
