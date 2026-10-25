using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

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

    [Fact]
    public async Task Starter_suggestions_exist_for_every_kind_of_flag_and_are_plain_words()
    {
        var rules = (await Ok(HttpMethod.Get, "/api/v1/recommendation-rules")).GetProperty("rules").EnumerateArray().ToList();

        var kinds = Tideline.Core.FlagKinds.All;
        rules.Select(r => r.GetProperty("flagKind").GetString()).Should().BeEquivalentTo(kinds, "one starter suggestion for each kind");
        rules.Select(r => r.GetProperty("suggestion").GetString()!).Should().OnlyContain(s => !s.Contains('—') && !s.Contains('–'), "plain words, no dashes");
        (await Cards()).Should().BeEmpty("nothing is flagged yet");
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
    public async Task A_rule_can_ask_for_a_severity_and_words_in_the_flag()
    {
        await Ok(HttpMethod.Post, "/api/v1/recommendation-rules", new { name = "Water only", flagKind = "recurring_missing", minSeverity = "alert", contains = "water", suggestion = "Call the water company." }, HttpStatusCode.Created);
        AddFlag("recurring_missing", "warn", "Water has not been paid\nx", subject: 1);
        AddFlag("recurring_missing", "alert", "Power has not been paid\nx", subject: 2);
        AddFlag("recurring_missing", "alert", "Water has not been paid\nx", period: "2026-04", subject: 3);

        var mine = (await Cards()).Where(c => c.GetProperty("ruleName").GetString() == "Water only").ToList();

        mine.Should().ContainSingle("only the alert about water fits");
        mine[0].GetProperty("flagTitle").GetString().Should().StartWith("Water");
    }

    [Fact]
    public async Task Dismissing_hides_one_suggestion_for_one_flag_and_it_stays_hidden()
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
        (await Cards()).Should().ContainSingle("a different flag still gets it");
    }

    [Fact]
    public async Task A_suggestion_switched_off_or_deleted_stops_showing_and_bad_input_is_refused_in_words()
    {
        AddFlag("review_needed", "info", "3 lines to review\nx");
        var rule = (await Cards()).Single().GetProperty("ruleId").GetInt64();

        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{rule}", new { isActive = false });
        (await Cards()).Should().BeEmpty();
        await Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{rule}", new { isActive = true });
        (await Cards()).Should().ContainSingle();
        await Ok(HttpMethod.Delete, $"/api/v1/recommendation-rules/{rule}");
        (await Cards()).Should().BeEmpty();

        async Task Refused(object body, string mention)
        {
            var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/recommendation-rules", body);
            status.Should().Be(HttpStatusCode.BadRequest);
            json.GetProperty("detail").GetString().Should().Contain(mention);
        }
        await Refused(new { name = "", flagKind = "review_needed", suggestion = "x" }, "name");
        await Refused(new { name = "A", flagKind = "nonsense", suggestion = "x" }, "kinds of flag");
        await Refused(new { name = "A", flagKind = "review_needed", suggestion = "" }, "Write the suggestion");
        await Refused(new { name = "A", flagKind = "review_needed", minSeverity = "loud", suggestion = "x" }, "info, warn or alert");
    }

    [Fact]
    public async Task Changes_are_audited_and_a_viewer_can_read_but_not_change()
    {
        await Ok(HttpMethod.Post, "/api/v1/recommendation-rules", new { name = "Audited", flagKind = "review_needed", suggestion = "x" }, HttpStatusCode.Created);
        (await _api.Get("/api/v1/audit?action=recommendation_rule.created")).GetProperty("items").GetArrayLength().Should().Be(1);

        var viewer = _api.AddUser("view2@example.com", "Viewer", Tideline.Core.Security.Role.Viewer);
        var client = await _api.SignInAsync("view2@example.com", ApiFixture.Password, viewer.Secret);
        (await client.GetAsync("/api/v1/recommendations")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync("/api/v1/recommendation-rules/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
