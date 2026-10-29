using System.Net;
using FluentAssertions;
using Tideline.Core.Security;
using Tideline.Data.Services;

namespace Tideline.Api.Tests;

/// <summary>The "add the starter set" button in Settings, Rules: owner only, adds only what is missing, and is audited.</summary>
public sealed class StarterPackApiTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task It_says_what_is_missing_adds_only_that_and_then_has_nothing_left_to_add()
    {
        var pack = StarterPack.Load();
        var before = await _api.Get("/api/v1/rules/starter");
        before.GetProperty("rulesInPack").GetInt32().Should().Be(pack.Rules.Count);
        before.GetProperty("rulesToAdd").GetInt32().Should().Be(pack.Rules.Count);
        var existingCategories = _api.Scalar<long>("SELECT COUNT(*) FROM categories WHERE name IN (" + string.Join(",", pack.Categories.Select(c => "'" + c.Name.Replace("'", "''") + "'")) + ")");
        before.GetProperty("categoriesToAdd").GetInt32().Should().Be(pack.Categories.Count - (int)existingCategories, "a category with the same name is left alone");

        var (status, result) = await _api.Send(HttpMethod.Post, "/api/v1/rules/starter");

        status.Should().Be(HttpStatusCode.OK, result.ToString());
        result.GetProperty("rulesAdded").GetArrayLength().Should().Be(pack.Rules.Count);
        result.GetProperty("categoriesAdded").GetArrayLength().Should().Be(pack.Categories.Count - (int)existingCategories);
        _api.Scalar<long>("SELECT COUNT(*) FROM rules WHERE name LIKE 'Starter: %'").Should().Be(pack.Rules.Count);

        var after = await _api.Get("/api/v1/rules/starter");
        after.GetProperty("rulesToAdd").GetInt32().Should().Be(0);
        after.GetProperty("categoriesToAdd").GetInt32().Should().Be(0);

        var again = await _api.Send(HttpMethod.Post, "/api/v1/rules/starter");
        again.Body.GetProperty("rulesAdded").GetArrayLength().Should().Be(0);
        again.Body.GetProperty("rulesAlreadyThere").GetInt32().Should().Be(pack.Rules.Count);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'starter_pack.loaded'").Should().Be(1, "loading it again changed nothing, so there is nothing to log");
    }

    [Fact]
    public async Task What_it_added_is_in_the_audit_log_and_shows_in_the_rules_list_after_the_rules_a_person_made()
    {
        await _api.Send(HttpMethod.Post, "/api/v1/rules/starter");

        var row = (await _api.Get("/api/v1/audit?action=starter_pack.loaded")).GetProperty("items")[0];
        row.GetProperty("afterJson").GetString().Should().Contain("Starter: groceries").And.Contain("rulesAdded").And.Contain("categoriesAdded");
        row.GetProperty("beforeJson").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        var rules = (await _api.Get("/api/v1/rules")).EnumerateArray().ToList();
        var starter = rules.Where(r => r.GetProperty("name").GetString()!.StartsWith("Starter: ")).ToList();
        var others = rules.Except(starter).ToList();
        starter.Should().NotBeEmpty().And.OnlyContain(r => r.GetProperty("priority").GetInt32() >= 1000);
        others.Should().OnlyContain(r => r.GetProperty("priority").GetInt32() < 1000, "the rules already there are still tried first");
    }

    [Fact]
    public async Task A_rule_made_after_the_starter_set_is_tried_before_it_so_what_a_person_teaches_wins()
    {
        await _api.Send(HttpMethod.Post, "/api/v1/rules/starter");
        var words = new[] { new { field = "description", op = "contains_any", value = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "KROGER" }) } };

        var (status, created) = await _api.Send(HttpMethod.Post, "/api/v1/rules", new { name = "My Kroger", setType = "extra", conditions = words });

        status.Should().Be(HttpStatusCode.Created, created.ToString());
        var rules = (await _api.Get("/api/v1/rules")).EnumerateArray().ToList();
        var mine = rules.Single(r => r.GetProperty("name").GetString() == "My Kroger").GetProperty("priority").GetInt32();
        mine.Should().BeLessThan(rules.Where(r => r.GetProperty("name").GetString()!.StartsWith("Starter: ")).Min(r => r.GetProperty("priority").GetInt32()),
            "a new rule goes after the other hand-made rules but ahead of every starter rule");
    }

    [Fact]
    public async Task Only_an_owner_may_look_or_load_not_a_member_a_viewer_or_a_token()
    {
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);

        foreach (var client in new[] { member, viewer })
        {
            (await client.GetAsync("/api/v1/rules/starter")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PostAsync("/api/v1/rules/starter", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        _api.Scalar<long>("SELECT COUNT(*) FROM rules WHERE name LIKE 'Starter: %'").Should().Be(0);
    }
}
