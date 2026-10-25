using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class AssetLedgerTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    private async Task<long> NewAccount(object body) => (await Ok(HttpMethod.Post, "/api/v1/accounts", body, HttpStatusCode.Created)).GetProperty("id").GetInt64();

    private async Task Snapshot(long account, string asOn, long cents) =>
        await Ok(HttpMethod.Post, $"/api/v1/accounts/{account}/snapshots", new { asOn, balanceCents = cents, source = "statement" }, HttpStatusCode.Created);

    private async Task<long> Line(long cents, string description, string date, string category = "Groceries")
    {
        var categoryId = _api.Scalar<long>("SELECT id FROM categories WHERE name = @category", new { category });
        var accountId = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'");
        return (await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId, postedOn = date, amountCents = cents, description, categoryId, type = "extra" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
    }

    private async Task Link(long line, long? asset) =>
        await Ok(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), assetId = asset ?? 0 });

    [Fact]
    public async Task An_asset_shows_what_it_is_worth_what_is_owed_the_equity_and_what_was_put_into_it()
    {
        var car = await NewAccount(new { name = "Car", kind = "asset" });
        await NewAccount(new { name = "Car loan", kind = "loan", loan = new { loanType = "auto", rateBps = 500, paymentCents = 56_900, securedAssetId = car } });
        var loan = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Car loan'");
        await Snapshot(car, "2025-06-01", 3_400_000);
        await Snapshot(car, "2026-03-01", 3_000_000);
        await Snapshot(loan, "2026-03-01", 2_000_000);
        var tires = await Line(-45_000, "TIRES", "2025-09-10");
        var oil = await Line(-12_000, "OIL CHANGE", "2026-01-15");
        var refund = await Line(5_000, "TIRE REBATE", "2026-02-02");
        foreach (var l in new[] { tires, oil, refund }) await Link(l, car);

        var a = (await Ok(HttpMethod.Get, "/api/v1/assets")).EnumerateArray().Single();

        a.GetProperty("name").GetString().Should().Be("Car");
        a.GetProperty("valueCents").GetInt64().Should().Be(3_000_000);
        a.GetProperty("valueAsOf").GetString().Should().Be("2026-03-01");
        a.GetProperty("owedCents").GetInt64().Should().Be(2_000_000);
        a.GetProperty("equityCents").GetInt64().Should().Be(1_000_000);
        a.GetProperty("putInCents").GetInt64().Should().Be(52_000, "45,000 + 12,000 less the 5,000 refund");
        a.GetProperty("lines").GetInt64().Should().Be(3);
        a.GetProperty("firstValueCents").GetInt64().Should().Be(3_400_000);
        a.GetProperty("valueChangeCents").GetInt64().Should().Be(-400_000);
        a.GetProperty("netCents").GetInt64().Should().Be(-452_000, "it lost 4,000 in value and 520 went into it");

        var years = a.GetProperty("byYear").EnumerateArray().ToDictionary(y => y.GetProperty("year").GetString()!, y => y.GetProperty("putInCents").GetInt64());
        years.Should().Equal(new Dictionary<string, long> { ["2026"] = 7_000, ["2025"] = 45_000 });
        a.GetProperty("loans").EnumerateArray().Single().GetProperty("owedCents").GetInt64().Should().Be(2_000_000);

        // every number opens the lines behind it
        (-(await _api.Drill(a.GetProperty("drill").GetString())).SumCents).Should().Be(52_000);
        foreach (var y in a.GetProperty("byYear").EnumerateArray())
            (-(await _api.Drill(y.GetProperty("drill").GetString())).SumCents).Should().Be(y.GetProperty("putInCents").GetInt64());
        foreach (var c in a.GetProperty("byCategory").EnumerateArray())
            (-(await _api.Drill(c.GetProperty("drill").GetString())).SumCents).Should().Be(c.GetProperty("putInCents").GetInt64());
    }

    [Fact]
    public async Task Linking_a_line_is_audited_shown_in_the_drawer_and_undone_with_zero()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        var roof = await Line(-90_000, "ROOF REPAIR", "2026-03-05");

        await Link(roof, house);

        var detail = await _api.Get($"/api/v1/transactions/{roof}");
        detail.GetProperty("asset").GetProperty("name").GetString().Should().Be("House");
        var audit = (await _api.Get("/api/v1/audit?action=transaction.update")).GetProperty("items")[0];
        audit.GetProperty("afterJson").GetString().Should().Contain($"\"assetId\":{house}");
        audit.GetProperty("beforeJson").GetString().Should().Contain("\"assetId\":null");

        await Link(roof, null);
        (await _api.Get($"/api/v1/transactions/{roof}")).GetProperty("asset").ValueKind.Should().Be(JsonValueKind.Null);
        (await Ok(HttpMethod.Get, "/api/v1/assets")).EnumerateArray().Single().GetProperty("putInCents").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Several_lines_are_linked_at_once_and_only_an_asset_can_be_linked()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        var a = await Line(-1_000, "PAINT", "2026-03-05");
        var b = await Line(-2_000, "BRUSHES", "2026-03-06");

        await Ok(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new[] { a, b }, set = new { assetId = house } });

        (await Ok(HttpMethod.Get, $"/api/v1/assets/{house}")).GetProperty("putInCents").GetInt64().Should().Be(3_000);

        var checking = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'");
        var (status, json) = await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{a}", new { version = await _api.VersionOf(a), assetId = checking });
        status.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("detail").GetString().Should().Contain("not an asset");

        (await _api.Send(HttpMethod.Get, "/api/v1/assets/999999")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_line_that_becomes_a_transfer_is_let_go_of_its_asset()
    {
        var house = await NewAccount(new { name = "House", kind = "asset" });
        var outLeg = await Line(-5_000, "TO SAVINGS", "2026-03-07");
        await Link(outLeg, house);
        var savings = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Savings'");
        var inLeg = (await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = savings, postedOn = "2026-03-07", amountCents = 5_000, description = "FROM CHECKING", categoryId = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Groceries'"), type = "extra" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        await Ok(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { outLeg, inLeg } }, HttpStatusCode.Created);

        (await Ok(HttpMethod.Get, $"/api/v1/assets/{house}")).GetProperty("putInCents").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task An_asset_with_no_figure_yet_says_so_instead_of_inventing_one()
    {
        var shed = await NewAccount(new { name = "Shed", kind = "asset" });

        var a = await Ok(HttpMethod.Get, $"/api/v1/assets/{shed}");

        a.GetProperty("valueCents").ValueKind.Should().Be(JsonValueKind.Null);
        a.GetProperty("equityCents").ValueKind.Should().Be(JsonValueKind.Null);
        a.GetProperty("netCents").ValueKind.Should().Be(JsonValueKind.Null);
        a.GetProperty("drill").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
