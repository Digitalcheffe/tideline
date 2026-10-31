using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>Running every rule again over past lines: a preview that saves nothing, then the real thing, which spares lines set by hand.</summary>
public sealed class RerunRulesTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private static readonly object March = new { from = "2026-03-01", to = "2026-03-31" };

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private async Task<long> MakeRule()
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/rules", new
        {
            name = "Mystery shops",
            setCategoryId = Category("Groceries"),
            setType = "extra",
            setPayee = "Mystery",
            tags = new[] { "Mystery Tag" },
            conditions = new[] { new { field = "description", op = "contains_any", value = JsonSerializer.SerializeToElement(new[] { "MYSTERY VENDOR" }) } },
        });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task A_rule_made_after_the_lines_arrived_is_used_on_them_and_the_preview_changes_nothing()
    {
        var line = _api.IdOf("MYSTERY VENDOR 9");
        var rule = await MakeRule();
        var auditBefore = _api.Scalar<long>("SELECT COUNT(*) FROM audit_log");

        var (status, preview) = await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun/test", March);
        status.Should().Be(HttpStatusCode.OK, preview.ToString());
        preview.GetProperty("wouldChange").GetInt32().Should().BeGreaterThan(0);
        var change = preview.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("transactionId").GetInt64() == line);
        change.GetProperty("before").GetProperty("categoryName").GetString().Should().NotBe("Groceries");
        change.GetProperty("after").GetProperty("categoryName").GetString().Should().Be("Groceries");
        change.GetProperty("after").GetProperty("ruleName").GetString().Should().Be("Mystery shops");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log").Should().Be(auditBefore, "a preview writes nothing");
        _api.Scalar<long?>("SELECT rule_id FROM transactions WHERE id = @line", new { line }).Should().NotBe(rule);

        var (_, done) = await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun", March);
        var changed = done.GetProperty("changed").GetInt32();
        changed.Should().Be(preview.GetProperty("wouldChange").GetInt32(), "it does what the preview said");
        _api.Scalar<long>("SELECT rule_id FROM transactions WHERE id = @line", new { line }).Should().Be(rule);
        _api.Scalar<long>("SELECT category_id FROM transactions WHERE id = @line", new { line }).Should().Be(Category("Groceries"));
        _api.Scalar<string>("SELECT payee FROM transactions WHERE id = @line", new { line }).Should().Be("Mystery");
        _api.Scalar<long>("SELECT COUNT(*) FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.transaction_id = @line AND g.name = 'mystery-tag'", new { line }).Should().Be(1);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'rule.rerun'").Should().Be(changed, "one entry for each changed line");
        _api.Scalar<string>("SELECT after_json FROM audit_log WHERE action = 'rule.rerun' AND entity_id = @id", new { id = line.ToString() }).Should().Contain("applyId");

        var (_, again) = await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun", March);
        again.GetProperty("changed").GetInt32().Should().Be(0, "running them twice changes nothing the second time");
    }

    [Fact]
    public async Task Lines_set_by_hand_and_bill_links_a_person_made_are_not_touched()
    {
        var byHand = _api.IdOf("MYSTERY VENDOR 9");
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{byHand}", new { version = await _api.VersionOf(byHand), categoryId = Category("Dining"), type = "extra" });
        await MakeRule();

        // a line a rule does change, that a person had linked to a bill
        var (_, item) = await _api.Send(HttpMethod.Post, "/api/v1/recurring", new { name = "Mystery bill", categoryId = Category("Groceries"), expectedMinCents = 1000, expectedMaxCents = 2000, dueDay = 8, firstDueOn = "2026-01-08" });
        var recurring = item.GetProperty("id").GetInt64();
        var linked = _api.Scalar<long>("SELECT id FROM transactions WHERE is_manual_override = 0 AND move_id IS NULL AND left_out_at IS NULL AND posted_on BETWEEN '2026-03-01' AND '2026-03-31' AND original_description NOT LIKE 'MYSTERY VENDOR%' LIMIT 1");
        _api.Execute("UPDATE transactions SET recurring_id = @recurring WHERE id = @linked", new { recurring, linked });

        await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun", March);

        _api.Scalar<long>("SELECT category_id FROM transactions WHERE id = @byHand", new { byHand }).Should().Be(Category("Dining"), "a line set by hand is never touched");
        _api.Scalar<long?>("SELECT recurring_id FROM transactions WHERE id = @linked", new { linked }).Should().Be(recurring, "a bill link stays when no rule names one");
    }

    [Fact]
    public async Task It_checks_the_dates()
    {
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun", new { from = "March", to = "2026-03-31" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun/test", new { from = "2026-03-31", to = "2026-03-01" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/rerun", new { from = "2026-03-31", to = "2026-03-01" })).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
