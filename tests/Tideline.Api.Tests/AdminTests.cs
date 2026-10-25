using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class AdminTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Count(string sql, object? args = null) => _api.Scalar<long>(sql, args);

    private static string TokenOf(string setupUrl) => Regex.Match(setupUrl, @"token=([^&]+)").Groups[1].Value;

    private async Task<JsonElement> Create(string email, string name, string role)
    {
        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/users", new { email, displayName = name, role });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return body;
    }

    // ---- people --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_adds_a_person_and_gets_a_setup_link_that_works_once()
    {
        var created = await Create("Sam@Example.com", "Sam", "member");

        created.GetProperty("user").GetProperty("email").GetString().Should().Be("sam@example.com");
        created.GetProperty("user").GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        var url = created.GetProperty("setupUrl").GetString()!;
        url.Should().StartWith("http://localhost:5173/setup?token=");

        var info = await _api.Anonymous.GetAsync($"/api/v1/auth/setup?token={TokenOf(url)}");
        info.StatusCode.Should().Be(HttpStatusCode.OK);
        Count("SELECT COUNT(*) FROM audit_log WHERE action IN ('user.created', 'user.setup_link_created')").Should().Be(2);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'user.created' AND user_id = @id", new { id = _api.OwnerId }).Should().Be(1, "audited as the owner who did it");
    }

    [Fact]
    public async Task Bad_or_repeated_details_are_refused()
    {
        await Create("sam@example.com", "Sam", "member");

        (await _api.Send(HttpMethod.Post, "/api/v1/users", new { email = "SAM@example.com", displayName = "Again", role = "member" })).Status.Should().Be(HttpStatusCode.Conflict);
        (await _api.Send(HttpMethod.Post, "/api/v1/users", new { email = "nope", displayName = "X", role = "member" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/users", new { email = "a@example.com", displayName = "", role = "member" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/users", new { email = "a@example.com", displayName = "A", role = "boss" })).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_list_shows_who_has_set_up_the_second_step_and_how_many_sessions_they_have()
    {
        await Create("sam@example.com", "Sam", "viewer");

        var list = (await _api.Get("/api/v1/users")).EnumerateArray().ToDictionary(u => u.GetProperty("email").GetString()!);

        list[ApiFixture.OwnerEmail].GetProperty("mfaEnrolled").GetBoolean().Should().BeTrue();
        list[ApiFixture.OwnerEmail].GetProperty("activeSessions").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        list["sam@example.com"].GetProperty("mfaEnrolled").GetBoolean().Should().BeFalse();
        list["sam@example.com"].GetProperty("role").GetString().Should().Be("viewer");
    }

    [Fact]
    public async Task Changing_a_role_is_audited_and_the_last_owner_cannot_be_demoted_or_deactivated()
    {
        var sam = (await Create("sam@example.com", "Sam", "member")).GetProperty("user").GetProperty("id").GetInt64();

        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{sam}", new { role = "viewer", displayName = "Samantha" })).Status.Should().Be(HttpStatusCode.OK);
        var audit = Count("SELECT COUNT(*) FROM audit_log WHERE action = 'user.updated' AND before_json LIKE '%member%' AND after_json LIKE '%viewer%'");
        audit.Should().Be(1);

        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{_api.OwnerId}", new { role = "member" })).Status.Should().Be((HttpStatusCode)422);
        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{_api.OwnerId}", new { isActive = false })).Status.Should().Be((HttpStatusCode)422);

        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{sam}", new { role = "owner" })).Status.Should().Be(HttpStatusCode.OK);
        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{_api.OwnerId}", new { role = "member" })).Status.Should().Be(HttpStatusCode.OK, "there is another owner now");
    }

    [Fact]
    public async Task Deactivating_signs_the_person_out_everywhere()
    {
        var (id, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        var sam = await _api.SignInAsync("sam@example.com", ApiFixture.Password, secret);
        (await sam.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{id}", new { isActive = false })).Status.Should().Be(HttpStatusCode.OK);

        (await sam.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resetting_the_second_step_signs_them_out_and_hands_the_owner_a_new_link()
    {
        var (id, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        var sam = await _api.SignInAsync("sam@example.com", ApiFixture.Password, secret);

        var (status, body) = await _api.Send(HttpMethod.Post, $"/api/v1/users/{id}/reset-mfa");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("setupUrl").GetString().Should().Contain("/setup?token=");
        (await sam.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Count("SELECT COUNT(*) FROM user_mfa WHERE user_id = @id", new { id }).Should().Be(0);
        Count("SELECT COUNT(*) FROM users WHERE id = @id AND mfa_enrolled_at IS NULL", new { id }).Should().Be(1);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.reset'").Should().Be(1);
    }

    [Fact]
    public async Task A_new_link_replaces_the_old_one()
    {
        var created = await Create("sam@example.com", "Sam", "member");
        var id = created.GetProperty("user").GetProperty("id").GetInt64();
        var first = TokenOf(created.GetProperty("setupUrl").GetString()!);

        var (_, again) = await _api.Send(HttpMethod.Post, $"/api/v1/users/{id}/setup-link");

        (await _api.Anonymous.GetAsync($"/api/v1/auth/setup?token={first}")).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await _api.Anonymous.GetAsync($"/api/v1/auth/setup?token={TokenOf(again.GetProperty("setupUrl").GetString()!)}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_people_are_a_404()
    {
        (await _api.Send(HttpMethod.Patch, "/api/v1/users/99999", new { role = "viewer" })).Status.Should().Be(HttpStatusCode.NotFound);
        (await _api.Send(HttpMethod.Post, "/api/v1/users/99999/reset-mfa")).Status.Should().Be(HttpStatusCode.NotFound);
        (await _api.Send(HttpMethod.Post, "/api/v1/users/99999/setup-link")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- sessions ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_sees_every_session_and_can_end_one()
    {
        var (_, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        var sam = await _api.SignInAsync("sam@example.com", ApiFixture.Password, secret);

        var all = (await _api.Get("/api/v1/sessions")).EnumerateArray().ToList();
        var theirs = all.Single(s => s.GetProperty("userEmail").GetString() == "sam@example.com");
        all.Count(s => s.GetProperty("isCurrent").GetBoolean()).Should().Be(1);
        theirs.GetProperty("secondStepPassed").GetBoolean().Should().BeTrue();

        (await _api.Send(HttpMethod.Delete, $"/api/v1/sessions/{theirs.GetProperty("id").GetInt64()}")).Status.Should().Be(HttpStatusCode.OK);

        (await sam.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'session.revoked'").Should().Be(1);
    }

    [Fact]
    public async Task Everyone_but_the_owner_is_kept_out_of_the_admin_endpoints()
    {
        var (_, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        var sam = await _api.SignInAsync("sam@example.com", ApiFixture.Password, secret);

        foreach (var url in new[] { "/api/v1/users", "/api/v1/sessions", "/api/v1/tokens", "/api/v1/audit", "/api/v1/system-events" })
            (await sam.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        (await sam.PostAsJsonAsync("/api/v1/tokens", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sam.PostAsJsonAsync("/api/v1/users", new { email = "a@example.com", displayName = "A" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- API tokens ----------------------------------------------------------------------------------------

    private async Task<(string Secret, long Id)> MakeToken(string scope, int? days = null, string name = "wall display")
    {
        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/tokens", new { name, scope, expiresInDays = days });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return (body.GetProperty("token").GetString()!, body.GetProperty("record").GetProperty("id").GetInt64());
    }

    private HttpClient Bearer(string secret)
    {
        var client = _api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    [Fact]
    public async Task A_token_is_shown_once_and_only_its_hash_is_kept()
    {
        var (secret, id) = await MakeToken("read");

        Regex.IsMatch(secret, "^tdl_[0-9A-Za-z]{40}$").Should().BeTrue();
        var listed = (await _api.Get("/api/v1/tokens")).EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == id);
        listed.GetProperty("prefix").GetString().Should().Be(secret[..12]);
        listed.ToString().Should().NotContain(secret);

        Count("SELECT COUNT(*) FROM api_tokens WHERE token_hash = @s", new { s = secret }).Should().Be(0);
        _api.Scalar<string>("SELECT token_hash FROM api_tokens WHERE id = @id", new { id }).Should().HaveLength(64);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'token.created' AND after_json LIKE '%tdl_%'").Should().Be(0, "the secret is never logged");
    }

    [Fact]
    public async Task A_read_token_can_read_the_numbers_but_not_change_anything()
    {
        var (secret, _) = await MakeToken("read");
        var client = Bearer(secret);

        (await client.GetAsync("/api/v1/overview?from=2026-03-01&to=2026-03-31")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/lines?from=2026-03-01&to=2026-03-31")).StatusCode.Should().Be(HttpStatusCode.OK);

        var id = _api.IdOf("MYSTERY VENDOR 9");
        var write = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { version = await _api.VersionOf(id), notes = "x" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await write.Content.ReadAsStringAsync()).Should().Contain("token_read_only");
    }

    [Fact]
    public async Task A_write_token_can_edit_lines_and_the_audit_log_says_which_token_did_it()
    {
        var (secret, tokenId) = await MakeToken("write", name: "automation");
        var client = Bearer(secret);
        var id = _api.IdOf("MYSTERY VENDOR 9");

        var patch = await client.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { version = await _api.VersionOf(id), notes = "from automation" });

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "no anti-forgery header is needed with a bearer token");
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'transaction.update' AND api_token_id = @tokenId", new { tokenId }).Should().Be(1);

        var audit = await _api.Get("/api/v1/audit?action=transaction.update");
        audit.GetProperty("items")[0].GetProperty("apiTokenName").GetString().Should().Be("automation");
    }

    [Theory]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/tokens")]
    [InlineData("/api/v1/sessions")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/auth/sessions")]
    public async Task Even_a_write_token_cannot_reach_people_secrets_or_logs(string url)
    {
        var (secret, _) = await MakeToken("write");

        var response = await Bearer(secret).GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("token_not_allowed");
    }

    [Fact]
    public async Task A_token_that_is_revoked_or_expired_stops_working_and_a_wrong_one_never_did()
    {
        var (revokedSecret, id) = await MakeToken("read");
        var (shortSecret, _) = await MakeToken("read", days: 1, name: "short");

        (await Bearer(revokedSecret).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Send(HttpMethod.Delete, $"/api/v1/tokens/{id}")).Status.Should().Be(HttpStatusCode.OK);
        (await Bearer(revokedSecret).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await Bearer(shortSecret).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Clock.Advance(TimeSpan.FromDays(2));
        (await Bearer(shortSecret).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await Bearer("tdl_" + new string('x', 40)).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'token.revoked'").Should().Be(1);
    }

    [Fact]
    public async Task A_token_stops_working_when_the_person_who_made_it_is_deactivated()
    {
        var (secret, _) = await MakeToken("read");
        var sam = await Create("sam@example.com", "Sam", "owner");
        (await _api.Send(HttpMethod.Patch, $"/api/v1/users/{_api.OwnerId}", new { isActive = false })).Status.Should().Be(HttpStatusCode.OK);

        (await Bearer(secret).GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        sam.GetProperty("user").GetProperty("role").GetString().Should().Be("owner");
    }

    [Fact]
    public async Task Bad_token_requests_are_refused()
    {
        (await _api.Send(HttpMethod.Post, "/api/v1/tokens", new { name = "", scope = "read" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/tokens", new { name = "x", scope = "admin" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/tokens", new { name = "x", scope = "read", expiresInDays = -5 })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Delete, "/api/v1/tokens/99999")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- the logs ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_audit_log_can_be_filtered_and_pages_and_shows_before_and_after()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{id}", new { version = await _api.VersionOf(id), notes = "one" });
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{id}", new { version = await _api.VersionOf(id), notes = "two" });

        var forLine = await _api.Get($"/api/v1/audit?entity=transaction&entityId={id}");
        forLine.GetProperty("total").GetInt64().Should().Be(2);
        var latest = forLine.GetProperty("items")[0];
        latest.GetProperty("userName").GetString().Should().Be("Test Owner");
        latest.GetProperty("beforeJson").GetString().Should().Contain("\"notes\":\"one\"");
        latest.GetProperty("afterJson").GetString().Should().Contain("\"notes\":\"two\"");

        (await _api.Get("/api/v1/audit?action=login.&pageSize=1")).GetProperty("items").GetArrayLength().Should().Be(1);
        (await _api.Get($"/api/v1/audit?user={_api.OwnerId}&from=2000-01-01&to=2099-01-01")).GetProperty("total").GetInt64().Should().BeGreaterThan(2);
        (await _api.Get("/api/v1/audit?from=2099-01-01")).GetProperty("total").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task System_events_can_be_read_and_filtered()
    {
        _api.Execute("INSERT INTO system_events (at, level, source, message) VALUES ('2026-03-01T00:00:00Z', 'error', 'inbox', 'Import failed for x.csv')");
        _api.Execute("INSERT INTO system_events (at, level, source, message) VALUES ('2026-03-02T00:00:00Z', 'info', 'backup', 'Backup done')");

        (await _api.Get("/api/v1/system-events")).GetProperty("total").GetInt64().Should().Be(2);
        var errors = await _api.Get("/api/v1/system-events?level=error");
        errors.GetProperty("items")[0].GetProperty("message").GetString().Should().Contain("Import failed");
        (await _api.Get("/api/v1/system-events?source=backup")).GetProperty("total").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task The_audit_log_cannot_be_changed_through_the_api()
    {
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Post })
        {
            var (status, _) = await _api.Send(method, "/api/v1/audit/1", new { });
            ((int)status).Should().BeOneOf(404, 405);
        }
    }
}
