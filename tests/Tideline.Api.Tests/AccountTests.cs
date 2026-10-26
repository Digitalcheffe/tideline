using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

/// <summary>A person managing their own sign-in: name, password, a new phone, recovery codes, sessions.</summary>
public sealed class AccountTests : IDisposable
{
    private const string Email = "dana@example.com";
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<(HttpClient Client, string Secret, long Id)> Member(Role role = Role.Member, string email = Email)
    {
        var (id, secret) = _api.AddUser(email, "Dana Smith", role);
        return (await _api.SignInAsync(email, ApiFixture.Password, secret), secret, id);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    // ---- name ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_changes_their_own_name_and_it_is_audited()
    {
        var (client, _, _) = await Member();

        var (status, _) = await Call(client, HttpMethod.Patch, "/api/v1/auth/profile", new { displayName = "  Dana S. Smith " });

        status.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement.GetProperty("user").GetProperty("displayName").GetString().Should().Be("Dana S. Smith");
        using var conn = _api.Open();
        var row = conn.QuerySingle<(string? Before, string? After)>("SELECT before_json, after_json FROM audit_log WHERE action = 'user.updated' ORDER BY id DESC LIMIT 1");
        row.Before.Should().Contain("Dana Smith");
        row.After.Should().Contain("Dana S. Smith");

        (await Call(client, HttpMethod.Patch, "/api/v1/auth/profile", new { displayName = "  " })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---- password --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_password_needs_the_current_one_and_signs_every_other_device_out()
    {
        var (here, secret, _) = await Member();
        var elsewhere = await _api.SignInAsync(Email, ApiFixture.Password, secret);
        const string next = "a different long passphrase";

        var (status, body) = await Call(here, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = next });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await here.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK, "this device stays signed in");
        (await elsewhere.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the other one is signed out");

        var fresh = _api.NewClient();
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = ApiFixture.Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old password is gone");
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = next })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var conn = _api.Open();
        var audit = string.Join(" ", conn.Query<string>("SELECT COALESCE(after_json, '') FROM audit_log"));
        audit.Should().Contain("otherSessionsEnded").And.NotContain(next).And.NotContain(ApiFixture.Password);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'user.password_changed'").Should().Be(1);
    }

    [Fact]
    public async Task A_wrong_current_password_or_a_weak_new_one_is_refused_in_words()
    {
        var (client, _, id) = await Member();

        var wrong = await Call(client, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = "not my password at all", newPassword = "a different long passphrase" });
        wrong.Status.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Body.GetProperty("detail").GetString().Should().Contain("current password");
        _api.Scalar<long>("SELECT failed_logins FROM users WHERE id = @id", new { id }).Should().Be(1);

        var weak = await Call(client, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = "short" });
        weak.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        weak.Body.GetProperty("detail").GetString().Should().Contain("at least 12");

        var same = await Call(client, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = ApiFixture.Password });
        same.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        same.Body.GetProperty("detail").GetString().Should().Contain("different");
    }

    [Fact]
    public async Task Guessing_the_current_password_locks_the_account_like_guessing_at_sign_in()
    {
        var (client, _, id) = await Member();

        for (var i = 0; i < 5; i++)
            await Call(client, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = $"wrong guess number {i} here", newPassword = "a different long passphrase" });

        _api.Scalar<string?>("SELECT locked_until FROM users WHERE id = @id", new { id }).Should().NotBeNull();
        (await client.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the guessing session is ended");
    }

    // ---- a new phone -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_authenticator_replaces_the_old_after_both_codes_check_out()
    {
        var (here, oldSecret, _) = await Member();
        var elsewhere = await _api.SignInAsync(Email, ApiFixture.Password, oldSecret);

        var start = await Call(here, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { code = _api.NextCode(oldSecret) });
        start.Status.Should().Be(HttpStatusCode.OK, start.Body.ToString());
        var newSecret = start.Body.GetProperty("secret").GetString()!;
        start.Body.GetProperty("qrSvg").GetString().Should().Contain("<svg");
        newSecret.Should().NotBe(oldSecret);

        var confirm = await Call(here, HttpMethod.Post, "/api/v1/auth/authenticator/confirm", new { code = _api.NextCode(newSecret) });

        confirm.Status.Should().Be(HttpStatusCode.OK, confirm.Body.ToString());
        confirm.Body.GetProperty("recoveryCodes").GetArrayLength().Should().Be(10);
        (await here.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await elsewhere.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "other devices are signed out");

        // from now on only the new phone signs in
        var old = async () => await _api.SignInAsync(Email, ApiFixture.Password, oldSecret);
        await old.Should().ThrowAsync<InvalidOperationException>("the old authenticator no longer works");
        (await _api.SignInAsync(Email, ApiFixture.Password, newSecret)).Should().NotBeNull();
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.authenticator_replaced'").Should().Be(1);
    }

    [Fact]
    public async Task A_wrong_code_at_either_step_changes_nothing()
    {
        var (client, secret, _) = await Member();

        (await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { code = "000000" })).Status.Should().Be(HttpStatusCode.Unauthorized);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.failure'").Should().BeGreaterThan(0);
        (await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/confirm", new { code = "123456" })).Status.Should().Be(HttpStatusCode.Conflict, "nothing was started");

        var start = await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { code = _api.NextCode(secret) });
        start.Status.Should().Be(HttpStatusCode.OK);
        (await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/confirm", new { code = "000000" })).Status.Should().Be(HttpStatusCode.Unauthorized);

        (await _api.SignInAsync(Email, ApiFixture.Password, secret)).Should().NotBeNull("the old authenticator still works");
    }

    [Fact]
    public async Task With_the_phone_gone_a_recovery_code_proves_it_is_you_and_is_spent()
    {
        var (client, secret, _) = await Member();
        var codes = (await Call(client, HttpMethod.Post, "/api/v1/auth/recovery-codes", new { code = _api.NextCode(secret) })).Body.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();

        var start = await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { recoveryCode = codes[0] });

        start.Status.Should().Be(HttpStatusCode.OK, start.Body.ToString());
        var again = await Call(client, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { recoveryCode = codes[0] });
        again.Status.Should().Be(HttpStatusCode.Unauthorized, "a recovery code works once");
    }

    // ---- who may -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Even_a_viewer_can_manage_their_own_sign_in_and_sign_out()
    {
        var (client, _, _) = await Member(Role.Viewer, "viewer@example.com");

        (await Call(client, HttpMethod.Patch, "/api/v1/auth/profile", new { displayName = "Viewer Person" })).Status.Should().Be(HttpStatusCode.OK);
        (await Call(client, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = "a different long passphrase" })).Status.Should().Be(HttpStatusCode.OK);
        (await Call(client, HttpMethod.Post, "/api/v1/auth/logout")).Status.Should().Be(HttpStatusCode.OK, "a viewer can sign out");
    }

    [Fact]
    public async Task A_password_alone_cannot_change_the_account()
    {
        _api.AddUser(Email, "Dana Smith", Role.Member);
        var half = _api.NewClient();
        await half.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = ApiFixture.Password });
        half.DefaultRequestHeaders.Add("X-CSRF-Token", JsonDocument.Parse(await half.GetStringAsync("/api/v1/auth/me")).RootElement.GetProperty("csrf").GetString());

        (await Call(half, HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = "a different long passphrase" })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Call(half, HttpMethod.Patch, "/api/v1/auth/profile", new { displayName = "Hijack" })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Call(half, HttpMethod.Post, "/api/v1/auth/authenticator/start", new { recoveryCode = "x" })).Status.Should().Be(HttpStatusCode.Forbidden);
    }
}
