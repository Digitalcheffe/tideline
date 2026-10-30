using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core;

namespace Tideline.Api.Tests;

/// <summary>Suggestions are shipped by the app: people switch them on and off and mark a card reviewed.</summary>
public sealed class RecommendationTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    private long AddFlag(string kind, string severity, string message, string period = "2026-03", long subject = 1)
    {
        _api.Execute("INSERT INTO flags (kind, subject_type, subject_id, period, severity, message, created_at) VALUES (@kind, 'category', @subject, @period, @severity, @message, '2026-04-01T00:00:00Z')",
            new { kind, severity, message, period, subject });
        return _api.Scalar<long>("SELECT MAX(id) FROM flags");
    }

    private async Task<List<JsonElement>> Cards() => (await Ok(HttpMethod.Get, "/api/v1/recommendations")).EnumerateArray().ToList();

    private async Task<List<JsonElement>> Rules() => (await Ok(HttpMethod.Get, "/api/v1/recommendation-rules")).GetProperty("rules").EnumerateArray().ToList();

    private long RuleId(string key) => _api.Scalar<long>("SELECT id FROM recommendation_rules WHERE flag_kind = @key", new { key });

    [Fact]
    public async Task The_app_ships_one_suggestion_for_every_kind_of_flag_and_they_are_plain_words()
    {
        var rules = await Rules();

        rules.Select(r => r.GetProperty("key").GetString()).Should().BeEquivalentTo(FlagKinds.All, "one suggestion for each kind");
        rules.Select(r => r.GetProperty("advice").GetString()!).Should().OnlyContain(s => !s.Contains('—') && !s.Contains('–'), "plain words, no dashes");
        rules.Should().OnlyContain(r => r.GetProperty("title").GetString()!.Length > 0);
        (await Cards()).Should().BeEmpty("nothing is flagged yet");
    }

    [Fact]
    public async Task Starting_again_brings_the_words_up_to_date_and_keeps_every_switch()
    {
        var id = RuleId(FlagKinds.ReviewNeeded);
        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{id}", new { isActive = false });
        _api.Execute("UPDATE recommendation_rules SET suggestion = 'old words', name = 'Old name' WHERE id = @id", new { id });
        _api.Execute("INSERT INTO recommendation_rules (name, flag_kind, min_severity, suggestion, is_active, sort_order, created_at, updated_at) VALUES ('Mine', 'review_needed', 'alert', 'a rule made before', 1, 999, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");

        using (var conn = _api.Open()) Tideline.Data.Services.SuggestionSync.Run(conn, TimeProvider.System);

        var rule = (await Rules()).Single(r => r.GetProperty("key").GetString() == FlagKinds.ReviewNeeded);
        rule.GetProperty("advice").GetString().Should().Contain("Open the lines tagged review");
        rule.GetProperty("title").GetString().Should().Be("Lines are waiting for review");
        rule.GetProperty("isActive").GetBoolean().Should().BeFalse("the person's switch is kept");
        _api.Scalar<long>("SELECT COUNT(*) FROM recommendation_rules WHERE flag_kind = 'review_needed'").Should().Be(1, "a second one for the same kind is removed");
    }

    [Fact]
    public async Task A_flag_that_is_still_true_gets_the_suggestion_for_its_kind_and_one_that_clears_loses_it()
    {
        var flag = AddFlag("recurring_missing", "alert", "Power has not been paid\nDue on the 3rd.");

        var cards = await Cards();

        var card = cards.Should().ContainSingle().Subject;
        card.GetProperty("flagId").GetInt64().Should().Be(flag);
        card.GetProperty("flagTitle").GetString().Should().Be("Power has not been paid");
        card.GetProperty("suggestion").GetString().Should().Contain("late");
        card.GetProperty("link").GetString().Should().StartWith("/bills");

        _api.Execute("UPDATE flags SET resolved_at = '2026-04-02T00:00:00Z' WHERE id = @flag", new { flag });
        (await Cards()).Should().BeEmpty();
    }

    [Fact]
    public async Task Marking_a_card_reviewed_hides_it_for_that_flag_and_it_stays_hidden()
    {
        var flag = AddFlag("review_needed", "info", "3 lines to review\nx");
        var card = (await Cards()).Single();
        var ruleId = card.GetProperty("ruleId").GetInt64();

        await Ok(HttpMethod.Post, "/api/v1/recommendations/dismiss", new { ruleId, flagId = flag });

        (await Cards()).Should().BeEmpty();
        _api.Execute("UPDATE flags SET resolved_at = '2026-04-02T00:00:00Z' WHERE id = @flag", new { flag });
        _api.Execute("UPDATE flags SET resolved_at = NULL WHERE id = @flag", new { flag });
        (await Cards()).Should().BeEmpty("the same flag coming back does not bring the card back");
        var again = await _api.Send(HttpMethod.Post, "/api/v1/recommendations/dismiss", new { ruleId, flagId = flag });
        again.Status.Should().Be(HttpStatusCode.BadRequest);

        AddFlag("review_needed", "info", "5 lines to review\nx", period: "2026-04");
        (await Cards()).Should().ContainSingle("a new period is a different flag, so it still gets the suggestion");
    }

    [Fact]
    public async Task A_suggestion_switched_off_stops_showing_and_comes_back_when_switched_on()
    {
        AddFlag("review_needed", "info", "3 lines to review\nx");
        var rule = (await Cards()).Single().GetProperty("ruleId").GetInt64();

        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{rule}", new { isActive = false });
        (await Cards()).Should().BeEmpty();
        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{rule}", new { isActive = true });
        (await Cards()).Should().ContainSingle();

        (await _api.Send(HttpMethod.Patch, $"/api/v1/recommendation-rules/{rule}", new { })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Patch, "/api/v1/recommendation-rules/999999", new { isActive = false })).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suggestions_cannot_be_added_changed_or_deleted_by_a_person()
    {
        (await _api.Send(HttpMethod.Post, "/api/v1/recommendation-rules", new { name = "Mine", flagKind = "review_needed", suggestion = "x" })).Status
            .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await _api.Send(HttpMethod.Delete, $"/api/v1/recommendation-rules/{RuleId(FlagKinds.ReviewNeeded)}")).Status
            .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        var before = (await Rules()).Single(r => r.GetProperty("key").GetString() == FlagKinds.ReviewNeeded).GetProperty("advice").GetString();
        (await _api.Send(HttpMethod.Patch, $"/api/v1/recommendation-rules/{RuleId(FlagKinds.ReviewNeeded)}", new { isActive = true, suggestion = "my own words" })).Status.Should().Be(HttpStatusCode.OK);
        (await Rules()).Single(r => r.GetProperty("key").GetString() == FlagKinds.ReviewNeeded).GetProperty("advice").GetString().Should().Be(before, "the words are not editable");
    }

    [Fact]
    public async Task Changes_are_audited_and_a_viewer_can_read_but_not_change()
    {
        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{RuleId(FlagKinds.ReviewNeeded)}", new { isActive = false });
        (await _api.Get("/api/v1/audit?action=recommendation_rule.updated")).GetProperty("items").GetArrayLength().Should().Be(1);

        var viewer = _api.AddUser("view2@example.com", "Viewer", Tideline.Core.Security.Role.Viewer);
        var client = await _api.SignInAsync("view2@example.com", ApiFixture.Password, viewer.Secret);
        (await client.GetAsync("/api/v1/recommendations")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PatchAsync("/api/v1/recommendation-rules/1", JsonContent.Create(new { isActive = false }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
