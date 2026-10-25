using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>
/// The same checks as the hand-worked fixtures, on 5,000 made-up lines across six accounts and 21 months: every number on
/// every screen must still equal the sum of the lines behind it, and the screens must stay quick.
/// </summary>
public sealed class LargeDataTests : IDisposable
{
    private const string Whole = "from=2025-01-01&to=2026-09-30";
    private static readonly string[] Buckets = ["in", "bills", "required", "extra", "oneTime", "savings"];

    private readonly ApiFixture _api = new(demoLines: 5000);

    public void Dispose() => _api.Dispose();

    private static int Sign(string bucket) => bucket == "in" ? 1 : -1;

    private async Task<(JsonElement Body, TimeSpan Took)> Timed(string url)
    {
        var sw = Stopwatch.StartNew();
        var body = await _api.Get(url);
        return (body, sw.Elapsed);
    }

    [Fact]
    public async Task All_five_thousand_lines_are_in_and_the_ledger_matches_the_bank_for_every_deposit_account()
    {
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(5000);
        _api.Scalar<long>("SELECT COUNT(*) FROM accounts").Should().Be(6);

        // the bank's last balance of each deposit account is a snapshot written at import; the ledger must add up to it
        var mismatches = _api.Scalar<long>("""
            SELECT COUNT(*) FROM accounts a
            JOIN balance_snapshots s ON s.account_id = a.id AND s.source = 'bank'
              AND s.as_of_on = (SELECT MAX(as_of_on) FROM balance_snapshots WHERE account_id = a.id AND source = 'bank')
            WHERE a.opening_balance_cents + (SELECT COALESCE(SUM(amount_cents), 0) FROM transactions t WHERE t.account_id = a.id AND t.posted_on <= s.as_of_on) <> s.balance_cents
            """);
        mismatches.Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM balance_snapshots WHERE source = 'bank'").Should().BeGreaterThan(50);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_spread_adds_up_and_every_number_drills_to_its_lines_over_the_whole_span()
    {
        var (o, took) = await Timed($"/api/v1/overview?{Whole}");
        var s = o.GetProperty("spread");

        var parts = new[] { "bills", "required", "extra", "oneTime", "savings", "leftOver" }.Sum(k => s.GetProperty(k).Cents());
        parts.Should().Be(s.GetProperty("in").Cents());
        s.GetProperty("in").Cents().Should().BeGreaterThan(0);
        s.GetProperty("savings").Cents().Should().BeGreaterThan(0, "the transfers to the money market count as savings");

        foreach (var b in Buckets)
        {
            var figure = s.GetProperty(b);
            (Sign(b) * (await _api.Drill(figure.Drill())).SumCents).Should().Be(figure.Cents(), b);
        }
        took.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Every_month_adds_up_too_and_the_months_add_up_to_the_whole()
    {
        var o = await _api.Get($"/api/v1/overview?{Whole}");
        var months = o.GetProperty("months").Items().ToList();
        months.Should().HaveCount(21);

        foreach (var key in Buckets)
            months.Sum(m => m.GetProperty(key).GetInt64()).Should().Be(o.GetProperty("spread").GetProperty(key).Cents(), $"{key} by month");

        foreach (var m in months)
        {
            var month = m.GetProperty("month").GetString()!;
            var first = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd");
            var one = (await _api.Get($"/api/v1/overview?from={first:yyyy-MM-dd}&to={new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month)):yyyy-MM-dd}")).GetProperty("spread");
            foreach (var b in Buckets)
            {
                one.GetProperty(b).Cents().Should().Be(m.GetProperty(b).GetInt64(), $"{month} {b}");
                (Sign(b) * (await _api.Drill(one.GetProperty(b).Drill())).SumCents).Should().Be(one.GetProperty(b).Cents(), $"{month} {b} drill");
            }
        }
    }

    [Fact]
    public async Task Account_balances_by_month_agree_with_the_ledger_and_every_cell_drills_to_its_net()
    {
        foreach (var year in new[] { 2025, 2026 })
        {
            var (a, took) = await Timed($"/api/v1/accounts/by-month?year={year}");
            took.Should().BeLessThan(TimeSpan.FromSeconds(5));

            foreach (var account in a.GetProperty("accounts").Items())
            {
                var id = account.GetProperty("accountId").GetInt64();
                foreach (var cell in account.GetProperty("cells").Items())
                {
                    (await _api.Drill(cell.GetProperty("drill").GetString())).SumCents.Should().Be(cell.GetProperty("netCents").GetInt64(), $"{account.GetProperty("name")} {cell.GetProperty("month")}");

                    if (cell.GetProperty("endBalanceCents").ValueKind == JsonValueKind.Number)
                    {
                        var month = cell.GetProperty("month").GetString()!;
                        var last = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd");
                        last = new DateOnly(last.Year, last.Month, DateTime.DaysInMonth(last.Year, last.Month));
                        var ledger = _api.Scalar<long>("""
                            SELECT COALESCE((SELECT opening_balance_cents FROM accounts WHERE id = @id), 0)
                                 + COALESCE((SELECT SUM(amount_cents) FROM transactions WHERE account_id = @id AND posted_on <= @last), 0)
                            """, new { id, last = last.ToString("yyyy-MM-dd") });
                        cell.GetProperty("endBalanceCents").GetInt64().Should().Be(ledger, $"{account.GetProperty("name")} end of {month}");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task The_bills_grid_total_equals_the_bills_bucket_and_every_paid_cell_drills()
    {
        var (b, took) = await Timed("/api/v1/bills?year=2025");
        took.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var names = b.GetProperty("rows").Items().Select(r => r.GetProperty("name").GetString()!).ToList();
        names.Should().Contain(["Mortgage", "Electric", "Water", "Internet", "Phone", "Auto insurance", "Home insurance"]);
        names.Should().Contain("Other: Property tax", "bills with no recurring item get their own row");

        var bills2025 = (await _api.Get("/api/v1/overview?from=2025-01-01&to=2025-12-31")).GetProperty("spread").GetProperty("bills").Cents();
        b.GetProperty("totalPaidCents").GetInt64().Should().Be(bills2025);

        foreach (var row in b.GetProperty("rows").Items())
            foreach (var cell in row.GetProperty("cells").Items().Where(c => c.GetProperty("paidCents").GetInt64() > 0))
                (-(await _api.Drill(cell.GetProperty("drill").GetString())).SumCents).Should().Be(cell.GetProperty("paidCents").GetInt64(), $"{row.GetProperty("name")} {cell.GetProperty("month")}");

        // the home insurance runs every sixth month: paid in January and July only
        var home = b.GetProperty("rows").Items().Single(r => r.GetProperty("name").GetString() == "Home insurance").GetProperty("cells");
        home.Items().Where(c => c.GetProperty("status").GetString() != "not_expected").Select(c => c.GetProperty("month").GetString())
            .Should().Equal("2025-01", "2025-07");
    }

    [Theory]
    [InlineData("required")]
    [InlineData("extra")]
    public async Task Required_and_extra_categories_add_up_to_their_totals_and_drill(string screen)
    {
        var (r, took) = await Timed($"/api/v1/{screen}?{Whole}");
        took.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var total = r.GetProperty("total").Cents();
        total.Should().BeGreaterThan(0);
        r.GetProperty("categories").Items().Sum(c => c.GetProperty("cents").GetInt64()).Should().Be(total);
        (-(await _api.Drill(r.GetProperty("total").Drill())).SumCents).Should().Be(total);
        foreach (var c in r.GetProperty("categories").Items())
            (-(await _api.Drill(c.GetProperty("drill").GetString())).SumCents).Should().Be(c.GetProperty("cents").GetInt64(), c.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Extra_lists_the_subscriptions_with_their_latest_charge()
    {
        var r = await _api.Get($"/api/v1/extra?{Whole}");

        var netflix = r.GetProperty("subscriptions").Items().Single(s => s.GetProperty("name").GetString() == "Netflix");
        netflix.GetProperty("priceChanged").GetBoolean().Should().BeFalse("the rise to 17.99 was months before the fixed test date");
        netflix.GetProperty("yearlyCostCents").GetInt64().Should().Be(1799 * 12);
        netflix.GetProperty("lastChargeCents").GetInt64().Should().Be(1799);
    }

    [Fact]
    public async Task Income_and_cards_agree_with_the_lines()
    {
        var i = await _api.Get("/api/v1/income?year=2025");
        (await _api.Drill(i.GetProperty("total").Drill())).SumCents.Should().Be(i.GetProperty("total").Cents());
        i.GetProperty("categories").Items().Select(c => c.GetProperty("name").GetString()).Should().Contain(["Salary", "Interest", "Tax refund"]);

        var (c, took) = await Timed("/api/v1/cards?month=2026-08");
        took.Should().BeLessThan(TimeSpan.FromSeconds(5));
        foreach (var card in c.GetProperty("cards").Items())
        {
            var month = card.GetProperty("months")[7];
            (await _api.Drill(month.GetProperty("drill").GetString())).SumCents.Should().Be(month.GetProperty("paymentsCents").GetInt64() - month.GetProperty("purchasesCents").GetInt64());
            foreach (var cat in card.GetProperty("categories").Items())
                (-(await _api.Drill(cat.GetProperty("drill").GetString())).SumCents).Should().Be(cat.GetProperty("cents").GetInt64());
        }
    }

    [Fact]
    public async Task Unmatched_merchants_land_in_the_review_pile_and_the_lines_view_pages_quickly()
    {
        var sw = Stopwatch.StartNew();
        var review = await _api.Get($"/api/v1/lines?{Whole}&review=true&pageSize=500");
        review.GetProperty("total").GetInt64().Should().BeGreaterThan(10);
        review.GetProperty("items").Items().Should().OnlyContain(l => l.GetProperty("description").GetString()!.StartsWith("ODDS AND ENDS"));

        var all = await _api.Get($"/api/v1/lines?{Whole}&move=include&pageSize=500&page=3&sort=amount_desc");
        all.GetProperty("total").GetInt64().Should().Be(5000);
        all.GetProperty("items").GetArrayLength().Should().Be(500);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Same_day_repeat_purchases_are_all_there()
    {
        var repeats = _api.Scalar<long>("""
            SELECT COUNT(*) FROM (SELECT account_id, posted_on, original_description, amount_cents FROM transactions
                                  GROUP BY account_id, posted_on, original_description, amount_cents HAVING COUNT(*) > 1)
            """);
        repeats.Should().BeGreaterThan(0, "the importer keeps genuine same-day repeats");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Moving_one_big_line_between_types_changes_the_buckets_by_exactly_that_amount()
    {
        var before = (await _api.Get($"/api/v1/overview?{Whole}")).GetProperty("spread");
        var line = _api.Scalar<long>("SELECT id FROM transactions WHERE original_description LIKE 'COSTCO%' ORDER BY amount_cents LIMIT 1");
        var detail = await _api.Get($"/api/v1/transactions/{line}");
        var cents = -detail.GetProperty("bank").GetProperty("amountCents").GetInt64();
        var shopping = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Shopping'");

        var (status, _) = await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = detail.GetProperty("version").GetString(), categoryId = shopping, type = "extra" });

        status.Should().Be(System.Net.HttpStatusCode.OK);
        var after = (await _api.Get($"/api/v1/overview?{Whole}")).GetProperty("spread");
        after.GetProperty("required").Cents().Should().Be(before.GetProperty("required").Cents() - cents);
        after.GetProperty("extra").Cents().Should().Be(before.GetProperty("extra").Cents() + cents);
        after.GetProperty("leftOver").Cents().Should().Be(before.GetProperty("leftOver").Cents());
    }
}
