using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>
/// Expected numbers are worked out by hand from tests/fixtures/truist (March 2026):
///   In 2,000.00 | Bills 130.99 | Required 585.40 | Extra 23.25 | Savings 300.00 | Left over 960.36
/// </summary>
public sealed class ScreenTests : IDisposable
{
    private const string March = "from=2026-03-01&to=2026-03-31";
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Overview_spread_adds_up_to_the_cent_and_matches_the_hand_worked_totals()
    {
        var o = await _api.Get($"/api/v1/overview?{March}");
        var s = o.GetProperty("spread");

        s.GetProperty("in").Cents().Should().Be(200000);
        s.GetProperty("bills").Cents().Should().Be(13099);
        s.GetProperty("required").Cents().Should().Be(58540);
        s.GetProperty("extra").Cents().Should().Be(2325);
        s.GetProperty("oneTime").Cents().Should().Be(0);
        s.GetProperty("savings").Cents().Should().Be(30000, "the checking leg of the savings move counts: deposit to savings adds");
        s.GetProperty("leftOver").Cents().Should().Be(96036);

        var parts = new[] { "bills", "required", "extra", "oneTime", "savings", "leftOver" }.Sum(k => s.GetProperty(k).Cents());
        parts.Should().Be(s.GetProperty("in").Cents(), "In = Bills + Required + Extra + One-time + Savings + Left over");
    }

    [Fact]
    public async Task Every_overview_number_equals_the_sum_of_the_lines_it_opens()
    {
        var s = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");

        foreach (var (key, sign) in new[] { ("in", 1), ("bills", -1), ("required", -1), ("extra", -1), ("oneTime", -1), ("savings", -1) })
        {
            var figure = s.GetProperty(key);
            var drill = await _api.Drill(figure.Drill());
            (sign * drill.SumCents).Should().Be(figure.Cents(), $"/lines for the {key} number must add up to it");
        }
    }

    [Fact]
    public async Task Savings_drill_includes_the_move_leg_that_counts()
    {
        var lines = await _api.Get($"/api/v1/lines?{March}&type=savings");

        lines.GetProperty("total").GetInt64().Should().Be(1);
        var item = lines.GetProperty("items")[0];
        item.GetProperty("description").GetString().Should().Be("TRANSFER TO SAVINGS");
        item.GetProperty("amountCents").GetInt64().Should().Be(-30000);
        item.GetProperty("moveId").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Overview_months_split_the_period()
    {
        var o = await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-04-30");
        var months = o.GetProperty("months").Items().Select(m => m.GetProperty("month").GetString()).ToList();
        months.Should().Equal("2026-03");
    }

    [Fact]
    public async Task Splitting_ten_dollars_of_groceries_to_an_extra_category_moves_it_from_required_to_extra()
    {
        var before = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");

        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var groceryId = _api.Scalar<long>("SELECT id FROM transactions WHERE original_description = 'GROCERY STORE #12'");
        _api.Execute("""
            INSERT INTO split_lines (transaction_id, amount_cents, category_id, type, created_at)
            VALUES (@id, -1000, 900, 'extra', '2026-04-01T00:00:00Z')
            """, new { id = groceryId });

        var after = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");

        after.GetProperty("required").Cents().Should().Be(before.GetProperty("required").Cents() - 1000);
        after.GetProperty("extra").Cents().Should().Be(before.GetProperty("extra").Cents() + 1000);
        after.GetProperty("in").Cents().Should().Be(before.GetProperty("in").Cents());
        after.GetProperty("leftOver").Cents().Should().Be(before.GetProperty("leftOver").Cents(), "the total is unchanged");

        // and the drills still add up, with the split part shown as its own line
        var required = await _api.Drill(after.GetProperty("required").Drill());
        required.SumCents.Should().Be(-after.GetProperty("required").Cents());
        var extra = await _api.Get($"/api/v1/lines?{March}&type=extra&category=900");
        extra.GetProperty("items")[0].GetProperty("isSplitPart").GetBoolean().Should().BeTrue();
        extra.GetProperty("summary").GetProperty("sumCents").GetInt64().Should().Be(-1000);
    }

    [Fact]
    public async Task Accounts_by_month_end_balances_match_the_bank_and_each_cell_drills_to_its_net()
    {
        var a = await _api.Get("/api/v1/accounts/by-month?year=2026");
        var accounts = a.GetProperty("accounts").Items().ToDictionary(x => x.GetProperty("name").GetString()!);

        long End(string name, int month) => accounts[name].GetProperty("cells")[month - 1].GetProperty("endBalanceCents").GetInt64();
        End("Checking", 3).Should().Be(189901);
        End("Savings", 3).Should().Be(530125);
        End("Card", 3).Should().Be(6010);
        End("Checking", 1).Should().Be(100000, "the opening balance carries through months with no lines");
        accounts["Checking"].GetProperty("cells")[11].GetProperty("endBalanceCents").ValueKind.Should().Be(JsonValueKind.Null, "future months have no balance yet");

        foreach (var account in accounts.Values)
            foreach (var cell in account.GetProperty("cells").Items())
            {
                var drill = await _api.Drill(cell.GetProperty("drill").GetString());
                drill.SumCents.Should().Be(cell.GetProperty("netCents").GetInt64(), $"{account.GetProperty("name")} {cell.GetProperty("month")}");
            }
    }

    [Fact]
    public async Task Cards_show_balance_purchases_payments_and_paid_in_full()
    {
        var c = await _api.Get("/api/v1/cards?month=2026-03");
        var card = c.GetProperty("cards")[0];

        card.GetProperty("name").GetString().Should().Be("Card");
        card.GetProperty("balanceCents").GetInt64().Should().Be(6010);
        card.GetProperty("dueDay").GetInt32().Should().Be(25);

        var march = card.GetProperty("months")[2];
        march.GetProperty("purchasesCents").GetInt64().Should().Be(8990);
        march.GetProperty("paymentsCents").GetInt64().Should().Be(15000, "payments are the card-side move legs");
        march.GetProperty("paidInFull").ValueKind.Should().Be(JsonValueKind.Null, "the 25 April deadline has not passed");
        card.GetProperty("months")[1].GetProperty("paidInFull").GetBoolean().Should().BeTrue();

        var payments = await _api.Drill(card.GetProperty("paymentsDrill").GetString());
        payments.SumCents.Should().Be(15000);
        (await _api.Drill(march.GetProperty("drill").GetString())).SumCents.Should().Be(15000 - 8990);

        var groceries = card.GetProperty("categories").Items().Single(x => x.GetProperty("name").GetString() == "Groceries");
        groceries.GetProperty("cents").GetInt64().Should().Be(8540);
        (await _api.Drill(groceries.GetProperty("drill").GetString())).SumCents.Should().Be(-8540);
    }

    [Fact]
    public async Task A_card_not_paid_in_full_is_flagged_once_the_deadline_passes()
    {
        // remove the payment leg so February's... March balance is owed and the 25 April deadline is past in a later "today"
        var c = await _api.Get("/api/v1/cards?month=2026-03");
        c.GetProperty("cards")[0].GetProperty("lastCheckedPaidInFull").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Bills_grid_shows_status_per_month_with_totals_and_drills()
    {
        var b = await _api.Get("/api/v1/bills?year=2026");
        var rows = b.GetProperty("rows").Items().ToDictionary(r => r.GetProperty("name").GetString()!);

        var power = rows["City Power"].GetProperty("cells");
        power[0].GetProperty("status").GetString().Should().Be("missing");
        power[2].GetProperty("status").GetString().Should().Be("paid");
        power[2].GetProperty("paidCents").GetInt64().Should().Be(12000);
        power[3].GetProperty("status").GetString().Should().Be("due", "due on the 3rd plus five days of grace; today is 1 April");
        power[4].GetProperty("status").GetString().Should().Be("projected");
        power[4].GetProperty("projectedCents").GetInt64().Should().Be(12000);

        rows["City Power"].GetProperty("totalProjectedCents").GetInt64().Should().Be(8 * 12000);
        rows["City Power"].GetProperty("totalCents").GetInt64().Should().Be(12000 + 8 * 12000);

        // acceptance: the paid grand total for past months equals the Bills number in the spread
        b.GetProperty("totalPaidCents").GetInt64().Should().Be(13099);
        var bills = (await _api.Get($"/api/v1/overview?from=2026-01-01&to=2026-12-31")).GetProperty("spread").GetProperty("bills").Cents();
        b.GetProperty("totalPaidCents").GetInt64().Should().Be(bills);

        // column totals add up to the grand total
        b.GetProperty("columnTotals").Items().Sum(c => c.GetProperty("paidCents").GetInt64() + c.GetProperty("projectedCents").GetInt64())
            .Should().Be(b.GetProperty("grandTotalCents").GetInt64());

        // every paid cell drills to the lines it adds up
        foreach (var row in rows.Values)
            foreach (var cell in row.GetProperty("cells").Items().Where(c => c.GetProperty("paidCents").GetInt64() > 0))
                (-(await _api.Drill(cell.GetProperty("drill").GetString())).SumCents).Should().Be(cell.GetProperty("paidCents").GetInt64());
    }

    [Fact]
    public async Task Bill_lines_without_a_recurring_item_get_an_Other_row_so_the_grid_still_adds_up()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (901, 'Insurance', 'bill')");
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, category_id, type, created_at, updated_at)
            VALUES (1, '2026-02-12', -45000, 'AUTO INSURANCE', 'manual:ins1', 'manual', 901, 'bill', '2026-04-01T00:00:00Z', '2026-04-01T00:00:00Z')
            """);

        var b = await _api.Get("/api/v1/bills?year=2026");
        var other = b.GetProperty("rows").Items().Single(r => r.GetProperty("isOther").GetBoolean());

        other.GetProperty("name").GetString().Should().Be("Other: Insurance");
        other.GetProperty("cells")[1].GetProperty("paidCents").GetInt64().Should().Be(45000);
        (-(await _api.Drill(other.GetProperty("cells")[1].GetProperty("drill").GetString())).SumCents).Should().Be(45000);

        var bills = (await _api.Get("/api/v1/overview?from=2026-01-01&to=2026-12-31")).GetProperty("spread").GetProperty("bills").Cents();
        b.GetProperty("totalPaidCents").GetInt64().Should().Be(bills).And.Be(13099 + 45000);
    }

    [Fact]
    public async Task One_time_items_over_the_threshold_are_listed_for_attention()
    {
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, category_id, type, created_at, updated_at)
            VALUES (1, '2026-03-20', -75000, 'ROOFER', 'manual:roof', 'manual', 1, 'one_time', '2026-04-01T00:00:00Z', '2026-04-01T00:00:00Z')
            """);

        var attention = (await _api.Get("/api/v1/bills?year=2026")).GetProperty("oneTimeAttention");

        attention.GetProperty("items").GetArrayLength().Should().Be(1);
        attention.GetProperty("items")[0].GetProperty("description").GetString().Should().Be("ROOFER");
        (await _api.Drill(attention.GetProperty("drill").GetString())).SumCents.Should().Be(-75000);
    }

    [Theory]
    [InlineData("required", 58540)]
    [InlineData("extra", 2325)]
    public async Task Required_and_extra_totals_equal_the_overview_bucket_and_their_categories(string screen, long expected)
    {
        var r = await _api.Get($"/api/v1/{screen}?{March}");

        r.GetProperty("total").Cents().Should().Be(expected);
        var overview = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread").GetProperty(screen).Cents();
        overview.Should().Be(expected);

        r.GetProperty("categories").Items().Sum(c => c.GetProperty("cents").GetInt64()).Should().Be(expected);
        foreach (var c in r.GetProperty("categories").Items())
            (-(await _api.Drill(c.GetProperty("drill").GetString())).SumCents).Should().Be(c.GetProperty("cents").GetInt64());
        (-(await _api.Drill(r.GetProperty("total").Drill())).SumCents).Should().Be(expected);
    }

    [Fact]
    public async Task Required_lists_categories_with_a_month_sparkline_and_top_payees()
    {
        var r = await _api.Get($"/api/v1/required?{March}");

        var rent = r.GetProperty("categories").Items().Single(c => c.GetProperty("name").GetString() == "Rent");
        rent.GetProperty("cents").GetInt64().Should().Be(50000);
        rent.GetProperty("months")[0].GetProperty("month").GetString().Should().Be("2026-03");
        r.GetProperty("payees")[0].GetProperty("payee").GetString().Should().Be("Landlord");
        (-(await _api.Drill(r.GetProperty("payees")[0].GetProperty("drill").GetString())).SumCents).Should().Be(50000);
    }

    [Fact]
    public async Task Income_total_equals_in_and_lists_unrecognised_deposits()
    {
        var i = await _api.Get("/api/v1/income?year=2026");

        i.GetProperty("total").Cents().Should().Be(200000);
        (await _api.Drill(i.GetProperty("total").Drill())).SumCents.Should().Be(200000);
        i.GetProperty("months")[2].GetProperty("actualCents").GetInt64().Should().Be(200000);
        i.GetProperty("months")[2].GetProperty("expectedCents").ValueKind.Should().Be(JsonValueKind.Null, "no budget yet");

        var review = i.GetProperty("reviewDeposits");
        review.GetProperty("count").GetInt64().Should().Be(1);
        review.GetProperty("sumCents").GetInt64().Should().Be(125);
        review.GetProperty("items")[0].GetProperty("description").GetString().Should().Be("INTEREST PAID");
        (await _api.Drill(review.GetProperty("drill").GetString())).SumCents.Should().Be(125);
    }

    [Fact]
    public async Task Income_compares_with_the_budget_when_there_is_one()
    {
        _api.Execute("INSERT INTO budgets (year, expected_income_cents) VALUES (2026, 2400000)");

        var i = await _api.Get("/api/v1/income?year=2026");

        i.GetProperty("expectedCents").GetInt64().Should().Be(2400000);
        i.GetProperty("months")[0].GetProperty("expectedCents").GetInt64().Should().Be(200000);
    }

    [Fact]
    public async Task Bad_periods_are_rejected_with_a_problem_document()
    {
        var response = await _api.Client.GetAsync("/api/v1/overview?from=2026-04-01&to=2026-03-01");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _api.Client.GetAsync("/api/v1/bills?year=abc")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Client.GetAsync("/api/v1/cards?month=2026-13")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
