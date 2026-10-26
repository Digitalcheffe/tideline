using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;

namespace Tideline.Api.Tests;

public sealed class AuthTests : IDisposable
{
    private const string Strong = "a long passphrase of mine";
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string? CookieValue(HttpResponseMessage r, string name = "tideline") =>
        r.Headers.TryGetValues("Set-Cookie", out var all)
            ? all.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(name + "="))?[(name.Length + 1)..]
            : null;

    private HttpRequestMessage With(HttpMethod method, string url, string? cookie, string? csrf = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookie is not null) request.Headers.Add("Cookie", $"tideline={cookie}");
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<string> CsrfFor(HttpClient raw, string cookie) =>
        (await Json(await raw.SendAsync(With(HttpMethod.Get, "/api/v1/auth/me", cookie)))).GetProperty("csrf").GetString()!;

    private long Count(string sql, object? args = null) => _api.Scalar<long>(sql, args);

    // ---- the front door --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/lines?from=2026-03-01&to=2026-03-31")]
    [InlineData("/api/v1/overview")]
    [InlineData("/api/v1/accounts")]
    [InlineData("/api/v1/transactions/1")]
    [InlineData("/api/v1/auth/me")]
    public async Task Nothing_answers_without_signing_in(string url)
    {
        var response = await _api.Anonymous.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(response)).GetProperty("type").GetString().Should().Be("not_signed_in");
    }

    [Fact]
    public async Task The_health_check_is_open_and_says_nothing_but_up()
    {
        var response = await _api.Anonymous.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"ok\",\"db\":\"ok\"}");
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_exactly_alike_and_set_no_cookie()
    {
        var wrong = await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = "not the password at all" });
        var unknown = await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@example.com", password = Strong });
        var empty = await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });

        foreach (var r in new[] { wrong, unknown, empty })
        {
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            r.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
        (await wrong.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_cookie_is_host_only_secure_http_only_and_same_site_strict()
    {
        using var secure = _api.Host.WithWebHostBuilder(b => b.UseSetting("Tideline:SecureCookie", "true"));
        var raw = _api.RawClient(secure);

        var login = await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.Should().StartWith("__host-tideline=").And.Contain("secure").And.Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/");
        cookie.Should().NotContain("domain=");
    }

    [Fact]
    public async Task Security_headers_are_on_every_api_response()
    {
        var response = await _api.Anonymous.GetAsync("/api/v1/overview");

        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'self'").And.Contain("frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("same-origin");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    // ---- the second step is not optional ------------------------------------------------------------------------

    [Fact]
    public async Task After_the_password_alone_only_the_sign_in_endpoints_answer()
    {
        var raw = _api.RawClient();
        var login = await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password });
        var pre = CookieValue(login)!;
        (await Json(login)).GetProperty("mfaEnrolled").GetBoolean().Should().BeTrue();

        var me = await Json(await raw.SendAsync(With(HttpMethod.Get, "/api/v1/auth/me", pre)));
        me.GetProperty("mfaPassed").GetBoolean().Should().BeFalse();

        foreach (var url in new[] { "/api/v1/overview", "/api/v1/accounts", "/api/v1/lines?from=2026-03-01&to=2026-03-31", "/api/v1/transactions/1", "/api/v1/auth/sessions" })
        {
            var r = await raw.SendAsync(With(HttpMethod.Get, url, pre));
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
            (await Json(r)).GetProperty("type").GetString().Should().Be("mfa_required", url);
        }
    }

    [Fact]
    public async Task The_second_step_signs_you_in_and_the_cookie_changes_so_the_old_one_is_dead()
    {
        var raw = _api.RawClient();
        var pre = CookieValue(await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }))!;
        var csrf = await CsrfFor(raw, pre);

        var mfa = await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", pre, csrf, new { code = _api.NextCode(_api.OwnerSecret) }));
        mfa.StatusCode.Should().Be(HttpStatusCode.OK);
        var full = CookieValue(mfa)!;

        full.Should().NotBe(pre);
        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/overview", pre))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the pre-second-step cookie is no good any more");
        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/overview", full))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_a_used_code_cannot_be_used_again()
    {
        var raw = _api.RawClient();
        string Fresh() => CookieValue(raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }).GetAwaiter().GetResult())!;

        var first = Fresh();
        var csrf = await CsrfFor(raw, first);
        (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", first, csrf, new { code = "000000" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var code = _api.NextCode(_api.OwnerSecret);
        (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", first, csrf, new { code }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = Fresh();
        var replay = await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", second, await CsrfFor(raw, second), new { code }));
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the same code is not accepted twice");
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_fifteen_minutes_and_each_step_is_audited()
    {
        for (var i = 0; i < 5; i++)
            (await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = "wrong wrong wrong wrong" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var right = await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password });
        right.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "locked, even with the right password");

        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'login.failure'").Should().Be(5);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'login.locked'").Should().Be(1);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'login.refused_locked'").Should().Be(1);

        _api.Clock.Advance(TimeSpan.FromMinutes(16));
        var later = await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password });
        later.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_codes_count_towards_the_same_lock_and_end_the_session()
    {
        var raw = _api.RawClient();
        var pre = CookieValue(await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }))!;
        var csrf = await CsrfFor(raw, pre);

        for (var i = 0; i < 5; i++)
            (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", pre, csrf, new { code = "111111" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/auth/me", pre))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session was ended when the account locked");
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.failure'").Should().Be(5);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'login.locked'").Should().Be(1);
    }

    // ---- CSRF ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_from_a_cookie_session_needs_the_anti_forgery_header()
    {
        var raw = _api.RawClient();
        var pre = CookieValue(await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }))!;
        var csrf = await CsrfFor(raw, pre);
        var full = CookieValue(await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", pre, csrf, new { code = _api.NextCode(_api.OwnerSecret) })))!;

        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);
        object body = new { version, notes = "x" };

        var none = await raw.SendAsync(With(HttpMethod.Patch, $"/api/v1/transactions/{id}", full, null, body));
        none.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(none)).GetProperty("type").GetString().Should().Be("csrf");

        (await raw.SendAsync(With(HttpMethod.Patch, $"/api/v1/transactions/{id}", full, "forged", body))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await raw.SendAsync(With(HttpMethod.Patch, $"/api/v1/transactions/{id}", full, csrf, body))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- recovery codes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_person_sets_up_from_a_link_then_has_ten_recovery_codes_that_work_once_each()
    {
        var (id, _) = _api.AddUser("new@example.com", "New Person", Role.Member, enrolled: false, password: null);
        string token;
        using (var conn = _api.Open()) token = new SetupLinks(conn, _api.Clock).Create(id).Token;
        var raw = _api.RawClient();

        // the link says who it is for, and refuses a made-up one
        (await Json(await raw.GetAsync($"/api/v1/auth/setup?token={token}"))).GetProperty("email").GetString().Should().Be("new@example.com");
        (await raw.GetAsync("/api/v1/auth/setup?token=nonsense")).StatusCode.Should().Be(HttpStatusCode.Gone);

        // a weak password is refused, a good one starts the session
        var weak = await raw.PostAsJsonAsync("/api/v1/auth/setup", new { token, password = "password1234" });
        weak.StatusCode.Should().Be((HttpStatusCode)422);
        (await Json(weak)).GetProperty("detail").GetString().Should().Contain("common");
        var setup = await raw.PostAsJsonAsync("/api/v1/auth/setup", new { token, password = Strong });
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var pre = CookieValue(setup)!;
        (await raw.PostAsJsonAsync("/api/v1/auth/setup", new { token, password = Strong })).StatusCode.Should().Be(HttpStatusCode.Gone, "a link works once");

        // not signed in fully yet
        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/overview", pre))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // the authenticator: a QR code and the secret, then a first code proves it works
        var csrf = await CsrfFor(raw, pre);
        var start = await Json(await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/enroll/totp/start", pre, csrf)));
        start.GetProperty("qrSvg").GetString().Should().StartWith("<svg");
        start.GetProperty("otpauthUri").GetString().Should().StartWith("otpauth://totp/Tideline:new%40example.com?secret=");
        var secret = start.GetProperty("secret").GetString()!;

        (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/enroll/totp/confirm", pre, csrf, new { code = "123456" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var confirm = await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/enroll/totp/confirm", pre, csrf, new { code = _api.NextCode(secret) }));
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var full = CookieValue(confirm)!;
        var codes = (await Json(confirm)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/overview", full))).StatusCode.Should().Be(HttpStatusCode.OK);

        // enrolled now: the authenticator cannot be swapped by someone who only has the password
        (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/enroll/totp/start", full, csrf))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // a recovery code stands in for the authenticator, once
        async Task<HttpStatusCode> SignInWithRecovery(string code)
        {
            var again = CookieValue(await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = "new@example.com", password = Strong }))!;
            return (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", again, await CsrfFor(raw, again), new { recoveryCode = code }))).StatusCode;
        }
        (await SignInWithRecovery(codes[0])).Should().Be(HttpStatusCode.OK);
        (await SignInWithRecovery(codes[0])).Should().Be(HttpStatusCode.Unauthorized, "spent");
        (await SignInWithRecovery(codes[1].ToUpperInvariant().Replace("-", " "))).Should().Be(HttpStatusCode.OK, "typing style does not matter");
        Count("SELECT COUNT(*) FROM recovery_codes WHERE used_at IS NULL").Should().Be(8);
        Count("SELECT COUNT(*) FROM recovery_codes WHERE code_hash IN (@a, @b)", new { a = codes[0], b = codes[1] }).Should().Be(0, "only hashes are stored");
    }

    [Fact]
    public async Task New_recovery_codes_need_a_current_authenticator_code_and_replace_the_old_ones()
    {
        var before = Count("SELECT COUNT(*) FROM recovery_codes");
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/recovery-codes", new { code = "000000" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var ok = await _api.Client.PostAsJsonAsync("/api/v1/auth/recovery-codes", new { code = _api.NextCode(_api.OwnerSecret) });

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(ok)).GetProperty("recoveryCodes").GetArrayLength().Should().Be(10);
        Count("SELECT COUNT(*) FROM recovery_codes").Should().Be(10);
        before.Should().Be(0, "the owner in the fixture had none until now");
    }

    // ---- sessions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_out_ends_the_session_and_is_audited()
    {
        var raw = _api.RawClient();
        var pre = CookieValue(await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }))!;
        var csrf = await CsrfFor(raw, pre);
        var full = CookieValue(await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/mfa/totp", pre, csrf, new { code = _api.NextCode(_api.OwnerSecret) })))!;

        (await raw.SendAsync(With(HttpMethod.Post, "/api/v1/auth/logout", full, csrf))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await raw.SendAsync(With(HttpMethod.Get, "/api/v1/overview", full))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Count("SELECT COUNT(*) FROM audit_log WHERE action = 'logout'").Should().Be(1);
    }

    [Fact]
    public async Task A_session_ends_after_twelve_idle_hours_and_after_fourteen_days()
    {
        var idle = await _api.SignInAsync(ApiFixture.OwnerEmail, ApiFixture.Password, _api.OwnerSecret);
        _api.Clock.Advance(TimeSpan.FromHours(13));
        (await idle.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var active = await _api.SignInAsync(ApiFixture.OwnerEmail, ApiFixture.Password, _api.OwnerSecret);
        for (var day = 0; day < 13; day++)
        {
            _api.Clock.Advance(TimeSpan.FromHours(11));
            (await active.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK, $"still in use at hour {(day + 1) * 11}");
            _api.Clock.Advance(TimeSpan.FromHours(11)); // two 11 hour gaps a day: never idle for 12
            (await active.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        _api.Clock.Advance(TimeSpan.FromDays(2));
        (await active.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "fourteen days is the limit however active");
    }

    [Fact]
    public async Task You_can_list_your_sessions_and_sign_the_others_out()
    {
        var other = await _api.SignInAsync(ApiFixture.OwnerEmail, ApiFixture.Password, _api.OwnerSecret);

        var list = await _api.Get("/api/v1/auth/sessions");
        list.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        list.EnumerateArray().Count(s => s.GetProperty("isCurrent").GetBoolean()).Should().Be(1);

        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/auth/sessions/revoke-others");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("revoked").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        (await other.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _api.Client.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK, "this session stays");
    }

    [Fact]
    public async Task Nobody_can_sign_out_someone_elses_session_through_the_self_service_endpoint()
    {
        var (_, secret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, secret);
        var theirSession = (await Json(await member.GetAsync("/api/v1/auth/sessions"))).EnumerateArray().Single().GetProperty("id").GetInt64();

        var (status, _) = await _api.Send(HttpMethod.Delete, $"/api/v1/auth/sessions/{theirSession}");

        status.Should().Be(HttpStatusCode.NotFound);
        (await member.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Too_many_tries_on_the_sign_in_endpoints_are_turned_away()
    {
        using var strict = _api.Host.WithWebHostBuilder(b => b.UseSetting("Tideline:AuthRequestsPerMinute", "3"));
        var raw = _api.RawClient(strict);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = "x@example.com", password = "whatever it is" })).StatusCode);

        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        statuses.Skip(3).Should().OnlyContain(s => s == (HttpStatusCode)429);
        (await raw.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "only /auth is limited");
        (await raw.GetAsync("/api/v1/auth/first-run")).StatusCode.Should().Be(HttpStatusCode.OK, "only changes are limited, not reading");
    }

    [Fact]
    public async Task Looking_at_who_is_signed_in_is_never_turned_away_however_often_the_page_asks()
    {
        using var strict = _api.Host.WithWebHostBuilder(b => b.UseSetting("Tideline:AuthRequestsPerMinute", "3"));
        var raw = _api.RawClient(strict);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) statuses.Add((await raw.GetAsync("/api/v1/auth/me")).StatusCode);

        statuses.Should().NotContain((HttpStatusCode)429, "the page asks every time the tab gets focus");
    }

    // ---- who may do what -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_look_but_not_change_a_member_can_change_but_not_see_the_people_and_an_owner_can_do_both()
    {
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);

        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);

        (await viewer.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        var denied = await viewer.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { version, notes = "x" });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(denied)).GetProperty("type").GetString().Should().Be("forbidden");

        (await member.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { version, notes = "x" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/accounts", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await _api.Client.GetAsync("/api/v1/users")).StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "the owner gets past the role check");
    }

    [Fact]
    public async Task A_deactivated_person_cannot_sign_in_and_is_signed_out_everywhere()
    {
        var (id, secret) = _api.AddUser("gone@example.com", "Gone", Role.Member);
        var client = await _api.SignInAsync("gone@example.com", ApiFixture.Password, secret);
        (await client.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var conn = _api.Open())
        {
            new UserRepository(conn, _api.Clock).SetActive(id, false);
        }

        (await client.GetAsync("/api/v1/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _api.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "gone@example.com", password = ApiFixture.Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_authenticator_secret_is_encrypted_in_the_database()
    {
        var stored = _api.Scalar<string>("SELECT totp_secret_enc FROM user_mfa WHERE user_id = @id", new { id = _api.OwnerId });

        stored.Should().NotBeNullOrEmpty().And.NotContain(_api.OwnerSecret);
        Directory.GetFiles(_api.KeysFolder, "key-*.xml").Should().NotBeEmpty("the keys are kept beside the database");
        await Task.CompletedTask;
    }
}
