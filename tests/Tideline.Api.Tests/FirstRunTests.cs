using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

/// <summary>An app with no users: the first person to arrive makes the owner account and then does the same second step as everyone.</summary>
public sealed class FirstRunTests : IDisposable
{
    private const string Email = "first@example.com";
    private const string Password = "a long passphrase of mine";
    private readonly ApiFixture _api = new(withOwner: false);

    public void Dispose() => _api.Dispose();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<JsonElement> Open() => JsonDocument.Parse(await _api.Anonymous.GetStringAsync("/api/v1/auth/first-run")).RootElement.Clone();

    [Fact]
    public async Task An_empty_app_says_it_needs_a_first_account_and_a_used_one_says_it_does_not()
    {
        (await Open()).GetProperty("needed").GetBoolean().Should().BeTrue();

        await Post(_api.Anonymous, "/api/v1/auth/first-run", new { email = Email, displayName = "First Person", password = Password });

        (await Open()).GetProperty("needed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_first_person_becomes_the_owner_and_must_still_do_the_second_step_before_anything_opens()
    {
        var client = _api.NewClient();

        var (status, body) = await Post(client, "/api/v1/auth/first-run", new { email = Email, displayName = "First Person", password = Password });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("mfaRequired").GetBoolean().Should().BeTrue();
        body.GetProperty("mfaEnrolled").GetBoolean().Should().BeFalse();

        var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement;
        me.GetProperty("user").GetProperty("role").GetString().Should().Be("owner");
        me.GetProperty("mfaPassed").GetBoolean().Should().BeFalse("a password is not enough");
        (await client.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // the usual enrolment finishes the job
        client.DefaultRequestHeaders.Add("X-CSRF-Token", me.GetProperty("csrf").GetString());
        var start = await Post(client, "/api/v1/auth/enroll/totp/start", new { });
        start.Status.Should().Be(HttpStatusCode.OK);
        _api.Clock.Advance(TimeSpan.FromSeconds(31));
        var confirm = await Post(client, "/api/v1/auth/enroll/totp/confirm", new { code = Totp.Code(start.Body.GetProperty("secret").GetString()!, Totp.StepOf(_api.Clock.GetUtcNow())) });
        confirm.Status.Should().Be(HttpStatusCode.OK, confirm.Body.ToString());
        confirm.Body.GetProperty("recoveryCodes").GetArrayLength().Should().Be(10);
        (await client.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task It_is_audited_without_the_password()
    {
        await Post(_api.NewClient(), "/api/v1/auth/first-run", new { email = Email, displayName = "First Person", password = Password });

        using var conn = _api.Open();
        var rows = conn.Query<(string Action, string? After)>("SELECT action, after_json FROM audit_log ORDER BY id").ToList();
        rows.Select(r => r.Action).Should().Contain(["user.created", "user.password_set"]);
        string.Join(" ", rows.Select(r => r.After)).Should().Contain("first run").And.NotContain(Password);
    }

    [Fact]
    public async Task Once_there_is_a_user_the_page_is_closed_for_good()
    {
        await Post(_api.NewClient(), "/api/v1/auth/first-run", new { email = Email, displayName = "First Person", password = Password });

        var (status, body) = await Post(_api.NewClient(), "/api/v1/auth/first-run", new { email = "second@example.com", displayName = "Second", password = Password });

        status.Should().Be(HttpStatusCode.Gone);
        body.GetProperty("detail").GetString().Should().Contain("already set up");
        _api.Scalar<long>("SELECT COUNT(*) FROM users").Should().Be(1);
    }

    [Fact]
    public async Task Two_people_arriving_together_make_only_one_owner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Post(_api.NewClient(), "/api/v1/auth/first-run", new { email = $"racer{i}@example.com", displayName = $"Racer {i}", password = Password })));

        results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1);
        results.Where(r => r.Status != HttpStatusCode.OK).Should().OnlyContain(r => r.Status == HttpStatusCode.Gone);
        _api.Scalar<long>("SELECT COUNT(*) FROM users").Should().Be(1);
    }

    [Fact]
    public async Task A_weak_password_or_a_bad_email_is_refused_in_words_and_makes_no_user()
    {
        async Task Refused(object body, string mention)
        {
            var (status, json) = await Post(_api.NewClient(), "/api/v1/auth/first-run", body);
            status.Should().Be(HttpStatusCode.UnprocessableEntity);
            json.GetProperty("detail").GetString().Should().Contain(mention);
        }

        await Refused(new { email = Email, displayName = "First Person", password = "short" }, "at least 12");
        await Refused(new { email = Email, displayName = "First Person", password = "password1234" }, "common");
        await Refused(new { email = Email, displayName = "First Person", password = "first-person-is-here" }, "email address");
        await Refused(new { email = "me@example.com", displayName = "Dana Smith", password = "dana-smith-was-here" }, "your name");
        await Refused(new { email = "not an email", displayName = "First Person", password = Password }, "email address");
        await Refused(new { email = Email, displayName = "", password = Password }, "name");

        _api.Scalar<long>("SELECT COUNT(*) FROM users").Should().Be(0);
        (await Open()).GetProperty("needed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_normal_app_with_users_answers_not_needed()
    {
        using var withOwner = new ApiFixture();

        var body = JsonDocument.Parse(await withOwner.Anonymous.GetStringAsync("/api/v1/auth/first-run")).RootElement;

        body.GetProperty("needed").GetBoolean().Should().BeFalse();
    }
}
