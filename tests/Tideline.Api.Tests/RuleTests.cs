using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class RuleTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private const string Period = "from=2026-03-01&to=2026-03-31";

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private object Mystery(string? name = null, long? categoryId = null, string type = "extra") => new
    {
        name = name ?? "Mystery shops",
        setCategoryId = categoryId ?? Category("Groceries"),
        setType = type,
        setPayee = "Mystery",
        tags = new[] { "Mystery Tag" },
        conditions = new[] { new { field = "description", op = "contains_any", value = JsonSerializer.SerializeToElement(new[] { "MYSTERY VENDOR" }) } },
    };

    private async Task<long> CreateAsync(object rule)
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/rules", rule);
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private long AuditCount(string action) => _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = @action", new { action });

    [Fact]
    public async Task A_rule_is_saved_whole_listed_changed_and_deleted_with_every_step_audited()
    {
        var id = await CreateAsync(Mystery());

        var rule = (await _api.Get("/api/v1/rules")).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);
        rule.GetProperty("name").GetString().Should().Be("Mystery shops");
        rule.GetProperty("setCategoryName").GetString().Should().Be("Groceries");
        rule.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("mystery-tag");
        rule.GetProperty("conditions")[0].GetProperty("op").GetString().Should().Be("contains_any");

        var (status, _) = await _api.Send(HttpMethod.Patch, $"/api/v1/rules/{id}", new { name = "Mystery shops v2", setType = "required", tags = Array.Empty<string>(), conditions = new[] { new { field = "description", op = "starts_with", value = JsonSerializer.SerializeToElement("MYSTERY") } } });
        status.Should().Be(HttpStatusCode.OK);
        var changed = (await _api.Get("/api/v1/rules")).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);
        changed.GetProperty("name").GetString().Should().Be("Mystery shops v2");
        changed.GetProperty("tags").GetArrayLength().Should().Be(0);
        changed.GetProperty("conditions")[0].GetProperty("op").GetString().Should().Be("starts_with");

        var updated = (await _api.Get("/api/v1/audit?action=rule.updated")).GetProperty("items")[0];
        updated.GetProperty("beforeJson").GetString().Should().Contain("Mystery shops").And.Contain("contains_any");
        updated.GetProperty("afterJson").GetString().Should().Contain("Mystery shops v2").And.Contain("starts_with");

        (await _api.Send(HttpMethod.Delete, $"/api/v1/rules/{id}", null)).Item1.Should().Be(HttpStatusCode.OK);
        _api.Scalar<long>("SELECT COUNT(*) FROM rules WHERE id = @id", new { id }).Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM rule_conditions WHERE rule_id = @id", new { id }).Should().Be(0);
        AuditCount("rule.created").Should().Be(1);
        AuditCount("rule.deleted").Should().Be(1);
    }

    [Fact]
    public async Task Bad_rules_are_refused_with_a_reason()
    {
        async Task Refused(object body, HttpStatusCode expected, string mention)
        {
            var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/rules", body);
            status.Should().Be(expected, json.ToString());
            json.GetProperty("detail").GetString().Should().Contain(mention);
        }

        await Refused(new { name = "", setType = "extra" }, HttpStatusCode.BadRequest, "name");
        await Refused(new { name = "Nothing" }, HttpStatusCode.BadRequest, "category or a type");
        await Refused(new { name = "X", setType = "luxury" }, HttpStatusCode.BadRequest, "not a type");
        await Refused(new { name = "X", setCategoryId = 99999 }, HttpStatusCode.BadRequest, "category");
        await Refused(new { name = "X", setType = "extra", conditions = new[] { new { field = "colour", op = "is", value = JsonSerializer.SerializeToElement("red") } } }, HttpStatusCode.BadRequest, "not valid");

        await CreateAsync(Mystery("Same name"));
        await Refused(Mystery("Same name"), HttpStatusCode.Conflict, "name");
        (await _api.Send(HttpMethod.Patch, "/api/v1/rules/99999", Mystery())).Item1.Should().Be(HttpStatusCode.NotFound);
        (await _api.Send(HttpMethod.Delete, "/api/v1/rules/99999", null)).Item1.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Trying_a_rule_lists_what_would_change_and_changes_nothing()
    {
        var statesBefore = _api.Scalar<long>("SELECT COUNT(*) FROM audit_log");
        var linesBefore = _api.Scalar<string>("SELECT group_concat(id || ':' || COALESCE(category_id, 0) || ':' || type, ',') FROM (SELECT * FROM transactions ORDER BY id)");

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/rules/test", new { rule = Mystery(), from = "2026-03-01", to = "2026-03-31" });

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("linesChecked").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("wouldChange").GetInt32().Should().BeGreaterThan(0);
        var change = json.GetProperty("changes").EnumerateArray().First(c => c.GetProperty("description").GetString()!.StartsWith("MYSTERY VENDOR"));
        change.GetProperty("after").GetProperty("categoryName").GetString().Should().Be("Groceries");
        change.GetProperty("after").GetProperty("type").GetString().Should().Be("extra");
        change.GetProperty("after").GetProperty("payee").GetString().Should().Be("Mystery");
        change.GetProperty("after").GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Contain("mystery-tag");
        change.GetProperty("before").GetProperty("categoryName").GetString().Should().NotBe("Groceries");

        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log").Should().Be(statesBefore, "a test writes nothing");
        _api.Scalar<string>("SELECT group_concat(id || ':' || COALESCE(category_id, 0) || ':' || type, ',') FROM (SELECT * FROM transactions ORDER BY id)").Should().Be(linesBefore);
        _api.Scalar<long>("SELECT COUNT(*) FROM tags WHERE name = 'mystery-tag'").Should().Be(0, "a draft does not create its tags");
    }

    [Fact]
    public async Task Trying_a_change_to_a_saved_rule_replaces_it_rather_than_adding_a_second()
    {
        var id = await CreateAsync(Mystery());
        var (_, same) = await _api.Send(HttpMethod.Post, "/api/v1/rules/test", new { rule = Mystery(), ruleId = id, from = "2026-03-01", to = "2026-03-31" });
        same.GetProperty("wouldChange").GetInt32().Should().Be(0, "the saved rule already does exactly this, but the lines are not yet changed... ");

        var (status, _) = await _api.Send(HttpMethod.Post, "/api/v1/rules/test", new { rule = new { name = "x" }, from = "2026-03-01", to = "2026-03-31" });
        status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/test", new { rule = Mystery(), from = "March", to = "2026-03-31" })).Item1.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/test", new { rule = Mystery(), from = "2026-03-31", to = "2026-03-01" })).Item1.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Applying_a_rule_changes_past_lines_audits_each_and_leaves_hand_edited_lines_alone()
    {
        var line = _api.IdOf("MYSTERY VENDOR 9");
        var id = await CreateAsync(Mystery());

        var (status, json) = await _api.Send(HttpMethod.Post, $"/api/v1/rules/{id}/apply", new { from = "2026-03-01", to = "2026-03-31" });

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("changed").GetInt32().Should().Be(1);
        _api.Scalar<long>("SELECT rule_id FROM transactions WHERE id = @line", new { line }).Should().Be(id);
        _api.Scalar<long>("SELECT category_id FROM transactions WHERE id = @line", new { line }).Should().Be(Category("Groceries"));
        _api.Scalar<string>("SELECT payee FROM transactions WHERE id = @line", new { line }).Should().Be("Mystery");
        var audit = (await _api.Get("/api/v1/audit?action=rule.apply")).GetProperty("items")[0];
        audit.GetProperty("beforeJson").GetString().Should().Contain("categoryId");
        audit.GetProperty("afterJson").GetString().Should().Contain("applyId");

        var (_, again) = await _api.Send(HttpMethod.Post, $"/api/v1/rules/{id}/apply", new { from = "2026-03-01", to = "2026-03-31" });
        again.GetProperty("changed").GetInt32().Should().Be(0, "applying twice changes nothing the second time");

        // set the line by hand, change what the rule says, apply again: the hand-set line stays as it is
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), categoryId = Category("Dining"), type = "extra" });
        await _api.Send(HttpMethod.Patch, $"/api/v1/rules/{id}", Mystery(categoryId: Category("Utilities"), type: "required"));
        var (_, third) = await _api.Send(HttpMethod.Post, $"/api/v1/rules/{id}/apply", new { from = "2026-03-01", to = "2026-03-31" });
        third.GetProperty("changed").GetInt32().Should().Be(0);
        _api.Scalar<long>("SELECT category_id FROM transactions WHERE id = @line", new { line }).Should().Be(Category("Dining"), "a line set by hand is never touched");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'rule.apply'").Should().Be(1);

        (await _api.Send(HttpMethod.Post, "/api/v1/rules/99999/apply", new { from = "2026-03-01", to = "2026-03-31" })).Item1.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reordering_renumbers_the_priorities_and_must_name_every_rule_once()
    {
        var a = await CreateAsync(Mystery("Rule A"));
        var b = await CreateAsync(Mystery("Rule B"));
        var all = (await _api.Get("/api/v1/rules")).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        var reversed = all.AsEnumerable().Reverse().ToList();

        var (status, _) = await _api.Send(HttpMethod.Post, "/api/v1/rules/reorder", new { ids = reversed });

        status.Should().Be(HttpStatusCode.OK);
        (await _api.Get("/api/v1/rules")).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Should().Equal(reversed);
        _api.Scalar<long>("SELECT priority FROM rules WHERE id = @id", new { id = reversed[0] }).Should().Be(10);
        AuditCount("rule.reordered").Should().Be(1);

        (await _api.Send(HttpMethod.Post, "/api/v1/rules/reorder", new { ids = new[] { a } })).Item1.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/rules/reorder", new { ids = new[] { a, a, b } })).Item1.Should().Be(HttpStatusCode.BadRequest);
    }
}
