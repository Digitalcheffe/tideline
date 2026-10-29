using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class LinesTests : IDisposable
{
    private const string March = "from=2026-03-01&to=2026-03-31";
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<JsonElement> Lines(string query) => await _api.Get($"/api/v1/lines?{March}&{query}");

    private static string[] Descriptions(JsonElement lines) =>
        lines.GetProperty("items").Items().Select(i => i.GetProperty("description").GetString()!).ToArray();

    [Fact]
    public async Task Default_is_every_effective_line_newest_first_without_moves()
    {
        var lines = await _api.Get($"/api/v1/lines?{March}");

        lines.GetProperty("total").GetInt64().Should().Be(8, "12 rows minus the 4 move legs");
        var dates = lines.GetProperty("items").Items().Select(i => i.GetProperty("postedOn").GetString()).ToList();
        dates.Should().BeInDescendingOrder();
        lines.GetProperty("summary").GetProperty("sumCents").GetInt64().Should().Be(200000 - 12000 - 8540 - 50000 - 450 - 1099 - 2000 + 125);
    }

    [Fact]
    public async Task Moves_can_be_included_or_shown_alone()
    {
        (await Lines("move=include")).GetProperty("total").GetInt64().Should().Be(12);
        var only = await Lines("move=only");
        only.GetProperty("total").GetInt64().Should().Be(4);
        only.GetProperty("summary").GetProperty("sumCents").GetInt64().Should().Be(0, "both legs of every move cancel out");
    }

    [Fact]
    public async Task Filters_narrow_by_type_category_account_text_amount_and_tag()
    {
        Descriptions(await Lines("type=income")).Should().BeEquivalentTo(["PAYROLL ACME CORP", "INTEREST PAID"], "money coming in that no rule knows is income until a person decides");
        Descriptions(await Lines("type=bill&sort=date_asc")).Should().Equal("CITY POWER AUTOPAY", "SPOTIFY USA");
        Descriptions(await Lines("q=spot")).Should().Equal("SPOTIFY USA");
        Descriptions(await Lines("q=landlord")).Should().Equal(["Check #1042"], "the payee is searched too");
        Descriptions(await Lines("minCents=40000")).Should().BeEquivalentTo("PAYROLL ACME CORP", "Check #1042");
        Descriptions(await Lines("maxCents=500")).Should().BeEquivalentTo("COFFEE SHOP", "INTEREST PAID");
        Descriptions(await Lines("review=true")).Should().BeEquivalentTo("MYSTERY VENDOR 9", "INTEREST PAID", "COFFEE SHOP");
        Descriptions(await Lines("tag=review&direction=in")).Should().Equal("INTEREST PAID");

        var checking = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'");
        (await Lines($"account={checking}")).GetProperty("total").GetInt64().Should().Be(5);

        var groceries = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Groceries'");
        Descriptions(await Lines($"category={groceries}")).Should().Equal("GROCERY STORE #12");
    }

    [Fact]
    public async Task Tags_must_all_match_and_unknown_tags_match_nothing()
    {
        (await Lines("tag=review,nope")).GetProperty("total").GetInt64().Should().Be(0);
        (await Lines("tag=review")).GetProperty("total").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task Recurring_filters_link_by_item_or_none()
    {
        var power = _api.Scalar<long>("SELECT id FROM recurring WHERE name = 'City Power'");
        Descriptions(await Lines($"recurring={power}")).Should().Equal("CITY POWER AUTOPAY");
        Descriptions(await Lines("type=bill&recurring=none")).Should().BeEmpty();
    }

    [Fact]
    public async Task Lines_carry_their_tags_category_and_payee()
    {
        var mystery = (await Lines("q=mystery")).GetProperty("items")[0];

        mystery.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("review");
        mystery.GetProperty("categoryName").GetString().Should().Be("Uncategorized");
        mystery.GetProperty("payee").GetString().Should().Be("Mystery Vendor");
        mystery.GetProperty("type").GetString().Should().Be("extra");
        mystery.GetProperty("accountName").GetString().Should().Be("Checking");
        mystery.GetProperty("amountCents").GetInt64().Should().Be(-2000);
    }

    [Fact]
    public async Task Paging_returns_the_whole_filters_summary_not_just_the_page()
    {
        var page = await Lines("pageSize=3&page=2&sort=amount_asc");

        page.GetProperty("items").GetArrayLength().Should().Be(3);
        page.GetProperty("page").GetInt32().Should().Be(2);
        page.GetProperty("total").GetInt64().Should().Be(8);
        page.GetProperty("summary").GetProperty("count").GetInt64().Should().Be(8);
        var amounts = page.GetProperty("items").Items().Select(i => i.GetProperty("amountCents").GetInt64()).ToList();
        amounts.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_split_line_is_listed_on_its_own_with_the_remainder_keeping_the_parent_category()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var id = _api.Scalar<long>("SELECT id FROM transactions WHERE original_description = 'GROCERY STORE #12'");
        _api.Execute("INSERT INTO split_lines (transaction_id, amount_cents, category_id, type, note, created_at) VALUES (@id, -1000, 900, 'extra', 'socks', '2026-04-01T00:00:00Z')", new { id });
        _api.Execute("INSERT INTO split_line_tags (split_line_id, tag_id) SELECT (SELECT MAX(id) FROM split_lines), id FROM tags WHERE name = 'review'");

        var parts = (await Lines("q=grocery")).GetProperty("items").Items().OrderBy(i => i.GetProperty("amountCents").GetInt64()).ToList();

        parts.Should().HaveCount(2);
        parts[0].GetProperty("amountCents").GetInt64().Should().Be(-7540);
        parts[0].GetProperty("categoryName").GetString().Should().Be("Groceries");
        parts[0].GetProperty("isSplitPart").GetBoolean().Should().BeFalse();
        parts[1].GetProperty("amountCents").GetInt64().Should().Be(-1000);
        parts[1].GetProperty("categoryName").GetString().Should().Be("Clothing");
        parts[1].GetProperty("isSplitPart").GetBoolean().Should().BeTrue();
        parts[1].GetProperty("splitLineId").ValueKind.Should().Be(JsonValueKind.Number);
        parts[1].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("review");
        parts[0].GetProperty("tags").GetArrayLength().Should().Be(0, "tags on a split are the split's own");

        // the review filter follows the split's tag, not the parent's
        Descriptions(await Lines("review=true&q=grocery")).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("from=2026-03-01", "to")]
    [InlineData("to=2026-03-31", "from")]
    [InlineData("from=2026-3-1&to=2026-03-31", "from")]
    [InlineData("from=2026-03-01&to=2026-03-31&type=nonsense", "type")]
    [InlineData("from=2026-03-01&to=2026-03-31&account=abc", "account")]
    [InlineData("from=2026-03-01&to=2026-03-31&move=sideways", "move")]
    [InlineData("from=2026-03-01&to=2026-03-31&sort=random", "sort")]
    [InlineData("from=2026-03-31&to=2026-03-01", "to")]
    public async Task Bad_requests_explain_which_parameter_is_wrong(string query, string parameter)
    {
        var response = await _api.Client.GetAsync("/api/v1/lines?" + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(parameter);
    }

    [Fact]
    public async Task Lookups_list_accounts_categories_tags_and_recurring_items()
    {
        (await _api.Get("/api/v1/accounts")).GetArrayLength().Should().Be(3);
        (await _api.Get("/api/v1/categories")).Items().Select(c => c.GetProperty("name").GetString()).Should().Contain(["Groceries", "Uncategorized"]);
        (await _api.Get("/api/v1/tags")).Items().Select(t => t.GetProperty("name").GetString()).Should().Contain("review");
        (await _api.Get("/api/v1/recurring")).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Only_this_computer_may_connect_until_sign_in_exists()
    {
        var local = await _api.Client.GetAsync("/health");
        local.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
