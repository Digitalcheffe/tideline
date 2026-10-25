using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class PasskeyTests : IDisposable
{
    private const string Origin = "http://localhost:5173"; // the public address in the test configuration
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    /// <summary>Signed in with the password only: the second step has not been done.</summary>
    private async Task<HttpClient> PasswordOnly(string email)
    {
        var client = _api.NewClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiFixture.Password })).EnsureSuccessStatusCode();
        var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement;
        client.DefaultRequestHeaders.Add("X-CSRF-Token", me.GetProperty("csrf").GetString());
        return client;
    }

    private static async Task<JsonElement> Post(HttpClient client, string url, object? body = null, HttpStatusCode? expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(url, body ?? new { });
        var text = await response.Content.ReadAsStringAsync();
        if (expected is { } status) response.StatusCode.Should().Be(status, $"POST {url}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A new person who has chosen a password and not yet set up a second step, with their first passkey registered.</summary>
    private async Task<(HttpClient Client, SoftPasskey Key, JsonElement Done, long Id)> NewPersonWithPasskey(string email = "new@example.com")
    {
        var (id, _) = _api.AddUser(email, "New Person", Role.Member, enrolled: false);
        var client = await PasswordOnly(email);
        var key = new SoftPasskey(Origin);
        var options = await Post(client, "/api/v1/auth/enroll/passkey/options");
        var done = await Post(client, "/api/v1/auth/enroll/passkey/verify", new { label = "Phone", credential = key.Create(options) });
        return (client, key, done, id);
    }

    [Fact]
    public async Task A_first_passkey_is_the_second_step_signs_the_person_in_and_gives_recovery_codes()
    {
        var (client, _, done, id) = await NewPersonWithPasskey();

        done.GetProperty("recoveryCodes").GetArrayLength().Should().Be(10);
        (await client.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK, "the session is fully signed in now");

        using var conn = _api.Open();
        var rows = conn.Query<(string Kind, string? CredentialId, string? PublicKey, string? Totp)>("SELECT kind, credential_id, public_key, totp_secret_enc FROM user_mfa WHERE user_id = @id", new { id }).ToList();
        rows.Should().ContainSingle().Which.Kind.Should().Be("passkey");
        rows[0].CredentialId.Should().NotBeNullOrEmpty();
        rows[0].PublicKey.Should().NotBeNullOrEmpty();
        rows[0].Totp.Should().BeNull();
        conn.ExecuteScalar<string?>("SELECT mfa_enrolled_at FROM users WHERE id = @id", new { id }).Should().NotBeNull();
        var audit = conn.ExecuteScalar<string>("SELECT after_json FROM audit_log WHERE action = 'mfa.enrolled' ORDER BY id DESC LIMIT 1");
        audit.Should().Contain("passkey");
    }

    [Fact]
    public async Task A_passkey_signs_in_after_the_password_and_the_counter_moves_on()
    {
        var (_, key, _, id) = await NewPersonWithPasskey();

        var again = await PasswordOnly("new@example.com");
        (await again.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the second step has not been done yet");
        var me = JsonDocument.Parse(await again.GetStringAsync("/api/v1/auth/me")).RootElement;
        me.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).Should().Equal("passkey");

        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        options.GetProperty("allowCredentials").GetArrayLength().Should().Be(1);
        options.GetProperty("userVerification").GetString().Should().Be("required");
        var done = await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = key.Assert(options) });

        done.GetProperty("signedIn").GetBoolean().Should().BeTrue();
        (await again.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Scalar<long>("SELECT sign_count FROM user_mfa WHERE user_id = @id", new { id }).Should().BeGreaterThan(0);
        _api.Scalar<string>("SELECT after_json FROM audit_log WHERE action = 'mfa.success' ORDER BY id DESC LIMIT 1").Should().Contain("passkey");
    }

    [Fact]
    public async Task A_different_key_is_refused_and_counts_as_a_wrong_try()
    {
        var (_, original, _, _) = await NewPersonWithPasskey();
        var impostor = new SoftPasskey(Origin, original.CredentialId); // same credential id, different private key

        var again = await PasswordOnly("new@example.com");
        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = impostor.Assert(options) }, HttpStatusCode.Unauthorized);

        (await again.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.failure'").Should().BeGreaterThan(0);
        _api.Scalar<long>("SELECT failed_logins FROM users WHERE email = 'new@example.com'").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_passkey_from_another_website_does_not_work_here()
    {
        var (_, key, _, _) = await NewPersonWithPasskey();

        var again = await PasswordOnly("new@example.com");
        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = key.Assert(options, originOverride: "https://lookalike.example") }, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_device_that_does_not_check_the_person_is_refused_because_user_verification_is_required()
    {
        var (_, key, _, _) = await NewPersonWithPasskey();

        var again = await PasswordOnly("new@example.com");
        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = key.Assert(options, verified: false) }, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_challenge_works_once_so_a_recorded_answer_cannot_be_replayed()
    {
        var (_, key, _, _) = await NewPersonWithPasskey();

        var again = await PasswordOnly("new@example.com");
        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        var answer = key.Assert(options);
        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = answer });

        var other = await PasswordOnly("new@example.com");
        await Post(other, "/api/v1/auth/mfa/passkey/verify", new { credential = answer }, HttpStatusCode.Conflict);
        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = answer }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_challenge_runs_out_after_five_minutes()
    {
        var (_, key, _, _) = await NewPersonWithPasskey();

        var again = await PasswordOnly("new@example.com");
        var options = await Post(again, "/api/v1/auth/mfa/passkey/options");
        _api.Clock.Advance(TimeSpan.FromMinutes(6));

        await Post(again, "/api/v1/auth/mfa/passkey/verify", new { credential = key.Assert(options) }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Someone_with_only_a_stolen_password_cannot_add_their_own_passkey()
    {
        var thief = await PasswordOnly(ApiFixture.OwnerEmail); // the owner already has an authenticator app

        await Post(thief, "/api/v1/auth/enroll/passkey/options", expected: HttpStatusCode.Forbidden);
        var theirs = new SoftPasskey(Origin);
        await Post(thief, "/api/v1/auth/enroll/passkey/verify", new { credential = theirs.Create(JsonDocument.Parse("{\"challenge\":\"AAAA\",\"rp\":{\"id\":\"localhost\"}}").RootElement) }, HttpStatusCode.Forbidden);

        _api.Scalar<long>("SELECT COUNT(*) FROM user_mfa WHERE kind = 'passkey'").Should().Be(0);
    }

    [Fact]
    public async Task A_signed_in_person_adds_a_second_passkey_and_removes_it_but_never_their_last_way_in()
    {
        // the owner has an authenticator app; a passkey is another way
        var key = new SoftPasskey(Origin);
        var options = await _api.Send(HttpMethod.Post, "/api/v1/auth/enroll/passkey/options");
        options.Status.Should().Be(HttpStatusCode.OK);
        var added = await _api.Send(HttpMethod.Post, "/api/v1/auth/enroll/passkey/verify", new { label = "Laptop", credential = key.Create(options.Body) });
        added.Status.Should().Be(HttpStatusCode.OK);
        added.Body.GetProperty("recoveryCodes").ValueKind.Should().Be(JsonValueKind.Null, "recovery codes were already given");

        var list = await _api.Get("/api/v1/auth/passkeys");
        var passkey = list.EnumerateArray().Should().ContainSingle().Subject;
        passkey.GetProperty("label").GetString().Should().Be("Laptop");
        _api.Scalar<string>("SELECT action FROM audit_log ORDER BY id DESC LIMIT 1").Should().Be("mfa.passkey_added");

        var id = passkey.GetProperty("id").GetInt64();
        (await _api.Send(HttpMethod.Delete, $"/api/v1/auth/passkeys/{id}")).Status.Should().Be(HttpStatusCode.OK);
        (await _api.Get("/api/v1/auth/passkeys")).GetArrayLength().Should().Be(0);
        _api.Scalar<string>("SELECT action FROM audit_log ORDER BY id DESC LIMIT 1").Should().Be("mfa.passkey_removed");
    }

    [Fact]
    public async Task The_only_way_in_cannot_be_removed()
    {
        var (client, _, _, _) = await NewPersonWithPasskey();
        var id = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/passkeys")).RootElement[0].GetProperty("id").GetInt64();

        var response = await client.DeleteAsync($"/api/v1/auth/passkeys/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("only way to sign in");
    }

    [Fact]
    public async Task The_same_passkey_cannot_be_added_twice()
    {
        var (client, key, _, _) = await NewPersonWithPasskey();

        var options = await Post(client, "/api/v1/auth/enroll/passkey/options");
        options.GetProperty("excludeCredentials").GetArrayLength().Should().Be(1, "the device is told which passkeys it already has");
        await Post(client, "/api/v1/auth/enroll/passkey/verify", new { credential = key.Create(options) }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_person_can_only_use_their_own_passkeys()
    {
        var (_, first, _, _) = await NewPersonWithPasskey("first@example.com");
        var (_, _, _, _) = await NewPersonWithPasskey("second@example.com");

        var second = await PasswordOnly("second@example.com");
        var options = await Post(second, "/api/v1/auth/mfa/passkey/options");
        // the first person's key, answering the second person's challenge
        await Post(second, "/api/v1/auth/mfa/passkey/verify", new { credential = first.Assert(options) }, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Someone_with_no_passkey_is_told_so_instead_of_getting_a_challenge()
    {
        var owner = await PasswordOnly(ApiFixture.OwnerEmail);

        await Post(owner, "/api/v1/auth/mfa/passkey/options", expected: HttpStatusCode.Conflict);
    }

    [Fact]
    public void The_relying_party_is_the_host_of_the_public_address()
    {
        _api.Service<Tideline.Api.Auth.PasskeyService>().RelyingPartyId.Should().Be("localhost");
    }
}
