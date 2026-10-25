using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class TransactionTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long IdOf(string description) => _api.Scalar<long>("SELECT id FROM transactions WHERE original_description = @description", new { description });

    [Fact]
    public async Task Detail_shows_the_bank_fields_apart_from_our_own_and_the_rule_that_set_it()
    {
        var t = await _api.Get($"/api/v1/transactions/{IdOf("CITY POWER AUTOPAY")}");

        var bank = t.GetProperty("bank");
        bank.GetProperty("amountCents").GetInt64().Should().Be(-12000);
        bank.GetProperty("postedOn").GetString().Should().Be("2026-03-03");
        bank.GetProperty("description").GetString().Should().Be("CITY POWER AUTOPAY");
        bank.GetProperty("bankBalanceCents").GetInt64().Should().Be(288000);
        bank.GetProperty("externalId").GetString().Should().StartWith("truist:");
        bank.GetProperty("source").GetString().Should().Be("csv");

        t.GetProperty("raw").GetProperty("Full description").GetString().Should().Be("CITY POWER AUTOPAY");
        t.GetProperty("categoryName").GetString().Should().Be("Utilities");
        t.GetProperty("type").GetString().Should().Be("bill");
        t.GetProperty("rule").GetProperty("name").GetString().Should().Be("Bill: power");
        t.GetProperty("recurring").GetProperty("name").GetString().Should().Be("City Power");
        t.GetProperty("isManualOverride").GetBoolean().Should().BeFalse();
        t.GetProperty("splits").GetArrayLength().Should().Be(0);
        t.GetProperty("unassignedCents").GetInt64().Should().Be(-12000);
        t.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_transfer_shows_the_other_side()
    {
        var t = await _api.Get($"/api/v1/transactions/{IdOf("TRANSFER TO SAVINGS")}");

        t.GetProperty("type").ValueKind.Should().Be(JsonValueKind.Null);
        var partner = t.GetProperty("movePartner");
        partner.GetProperty("accountName").GetString().Should().Be("Savings");
        partner.GetProperty("amountCents").GetInt64().Should().Be(30000);
    }

    [Fact]
    public async Task Splits_and_what_is_left_unassigned_are_shown()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var id = IdOf("GROCERY STORE #12");
        _api.Execute("INSERT INTO split_lines (transaction_id, amount_cents, category_id, type, note, created_at) VALUES (@id, -1000, 900, 'extra', 'socks', '2026-04-01T00:00:00Z')", new { id });

        var t = await _api.Get($"/api/v1/transactions/{id}");

        t.GetProperty("splits").GetArrayLength().Should().Be(1);
        t.GetProperty("splits")[0].GetProperty("categoryName").GetString().Should().Be("Clothing");
        t.GetProperty("splits")[0].GetProperty("note").GetString().Should().Be("socks");
        t.GetProperty("unassignedCents").GetInt64().Should().Be(-7540);
    }

    [Fact]
    public async Task An_unknown_transaction_is_a_404_problem()
    {
        var response = await _api.Client.GetAsync("/api/v1/transactions/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("999999");
    }
}
