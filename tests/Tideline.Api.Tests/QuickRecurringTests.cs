using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>The quick way to make a recurring item from lines you can see (docs/FEATURES.md, Bills). "Today" is 1 April 2026.</summary>
public sealed class QuickRecurringTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Account(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private async Task<long> Line(string description, string payee, long cents, string date, string category = "Utilities", string type = "bill")
    {
        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/transactions", new
        {
            accountId = Account("Checking"), postedOn = date, amountCents = -cents, description, payee, categoryId = Category(category), type,
        });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetInt64();
    }

    private async Task<long[]> Water() => [
        await Line("QUICKTEST WATER 9921", "Quicktest Water", 4100, "2026-01-09"),
        await Line("QUICKTEST WATER 9921", "Quicktest Water", 4300, "2026-02-09"),
        await Line("QUICKTEST WATER 9921", "Quicktest Water", 3900, "2026-03-10"),
    ];

    [Fact]
    public async Task Opened_with_lines_the_form_comes_filled_in_and_ticks_the_same_payee()
    {
        var ids = await Water();
        var other = await Line("SOMEONE ELSE 1", "Someone Else", 2000, "2026-03-12");

        var s = await _api.Get($"/api/v1/recurring/suggest?lineIds={ids[0]}");

        s.GetProperty("name").GetString().Should().Be("Quicktest Water");
        s.GetProperty("categoryName").GetString().Should().Be("Utilities");
        s.GetProperty("frequency").GetString().Should().Be("monthly");
        s.GetProperty("everyN").GetInt32().Should().Be(1);
        s.GetProperty("dueDay").GetInt32().Should().Be(9, "the middle day it was paid on");
        s.GetProperty("usualCents").GetInt64().Should().Be(4100);
        s.GetProperty("minCents").GetInt64().Should().Be(3510, "10% under $39.00");
        s.GetProperty("maxCents").GetInt64().Should().Be(4730, "10% over $43.00");
        s.GetProperty("linksNextOnes").GetBoolean().Should().BeTrue();
        var lines = s.GetProperty("lines").EnumerateArray().ToList();
        lines.Where(l => l.GetProperty("selected").GetBoolean()).Select(l => l.GetProperty("id").GetInt64()).Should().BeEquivalentTo(ids);
        lines.Single(l => l.GetProperty("id").GetInt64() == other).GetProperty("selected").GetBoolean().Should().BeFalse("another payee is offered but not ticked");
    }

    [Fact]
    public async Task Opened_from_a_category_ticks_its_bill_lines_and_names_it_after_the_category()
    {
        var ids = await Water();
        var notBill = await Line("QUICKTEST TOY 1", "Quicktest Toy", 900, "2026-03-03", type: "extra");

        var s = await _api.Get($"/api/v1/recurring/suggest?categoryId={Category("Utilities")}");

        s.GetProperty("name").GetString().Should().Be("Utilities");
        var ticked = s.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("selected").GetBoolean()).Select(l => l.GetProperty("id").GetInt64()).ToList();
        ticked.Should().Contain(ids).And.NotContain(notBill);
    }

    [Fact]
    public async Task Confirming_makes_the_item_links_the_ticked_lines_and_adds_a_rule_for_next_months()
    {
        var ids = await Water();
        var keep = await Line("QUICKTEST WATER 9921", "Quicktest Water", 4000, "2026-03-20");
        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/recurring/quick", new
        {
            name = "Quicktest Water", categoryId = Category("Utilities"), minCents = 3500, maxCents = 4800, frequency = "monthly", everyN = 1, dueDay = 9,
            lineIds = ids, addRule = true,
        });

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("recurringId").GetInt64();
        body.GetProperty("linesLinked").GetInt32().Should().Be(3);
        body.GetProperty("linesSkipped").GetInt32().Should().Be(0);
        body.GetProperty("ruleId").ValueKind.Should().Be(JsonValueKind.Number);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id = @id", new { id }).Should().Be(3);
        _api.Scalar<long?>("SELECT recurring_id FROM transactions WHERE id = @keep", new { keep }).Should().BeNull("only the ticked lines are linked");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'recurring.created' AND entity_id = @id", new { id = id.ToString() }).Should().Be(1);

        // a line already in an item is skipped, not moved
        var (again, second) = await _api.Send(HttpMethod.Post, "/api/v1/recurring/quick", new
        {
            name = "Quicktest Water Two", categoryId = Category("Utilities"), minCents = 3500, maxCents = 4800, lineIds = ids,
        });
        again.Should().Be(HttpStatusCode.Created, second.ToString());
        second.GetProperty("linesLinked").GetInt32().Should().Be(0);
        second.GetProperty("linesSkipped").GetInt32().Should().Be(3);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id = @id", new { id }).Should().Be(3);
    }

    [Fact]
    public async Task A_form_that_cannot_be_saved_says_why_and_changes_nothing()
    {
        var before = _api.Scalar<long>("SELECT COUNT(*) FROM recurring");

        (await _api.Send(HttpMethod.Post, "/api/v1/recurring/quick", new { name = "No range", categoryId = Category("Utilities") })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/recurring/quick", new { name = "", categoryId = Category("Utilities"), minCents = 1, maxCents = 2 })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/recurring/quick", new { name = "Backwards", categoryId = Category("Utilities"), minCents = 9, maxCents = 2 })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Get, "/api/v1/recurring/suggest?lineIds=99999999")).Status.Should().Be(HttpStatusCode.NotFound);
        (await _api.Send(HttpMethod.Get, "/api/v1/recurring/suggest")).Status.Should().Be(HttpStatusCode.BadRequest);

        _api.Scalar<long>("SELECT COUNT(*) FROM recurring").Should().Be(before);
    }

    [Fact]
    public async Task Selected_lines_can_be_linked_to_an_item_that_exists_and_unlinked_again()
    {
        var ids = await Water();
        var item = _api.Scalar<long>("SELECT id FROM recurring ORDER BY id LIMIT 1");

        var linked = await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids, set = new { recurringId = item } });
        linked.Status.Should().Be(HttpStatusCode.OK, linked.Body.ToString());
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id = @item AND id IN @ids", new { item, ids }).Should().Be(3);

        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids, set = new { recurringId = 0 } })).Status.Should().Be(HttpStatusCode.OK);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id IS NOT NULL AND id IN @ids", new { ids }).Should().Be(0);

        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids, set = new { recurringId = 999999 } })).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
