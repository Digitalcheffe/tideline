using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

/// <summary>Options that start from the compose file and can be changed in the app (Settings).</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<JsonElement> Row(string key) =>
        (await _api.Get("/api/v1/settings/app")).EnumerateArray().Single(r => r.GetProperty("key").GetString() == key);

    private TidelineOptions Live => _api.Service<TidelineOptions>();

    [Fact]
    public async Task Every_option_is_listed_with_the_compose_value_until_it_is_changed()
    {
        var rows = (await _api.Get("/api/v1/settings/app")).EnumerateArray().ToList();

        rows.Select(r => r.GetProperty("key").GetString()).Should().BeEquivalentTo(
            "idle_hours", "absolute_days", "max_failed_logins", "lockout_minutes", "auth_requests_per_minute", "inbox_path", "inbox_interval_seconds");
        rows.Should().OnlyContain(r => !r.GetProperty("overridden").GetBoolean());
        rows.Should().OnlyContain(r => r.GetProperty("effective").GetString() == r.GetProperty("compose").GetString());
    }

    [Fact]
    public async Task A_saved_value_applies_at_once_beats_compose_and_can_be_reset()
    {
        var composeIdle = (await Row("idle_hours")).GetProperty("compose").GetString();

        var saved = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["idle_hours"] = "2", ["lockout_minutes"] = "30", ["max_failed_logins"] = "3" } });

        saved.Status.Should().Be(HttpStatusCode.OK, saved.Body.ToString());
        Live.IdleHours.Should().Be(2, "it applies without a restart");
        Live.LockoutMinutes.Should().Be(30);
        Live.MaxFailedLogins.Should().Be(3);
        var row = await Row("idle_hours");
        row.GetProperty("effective").GetString().Should().Be("2");
        row.GetProperty("compose").GetString().Should().Be(composeIdle, "the compose value is still shown");
        row.GetProperty("overridden").GetBoolean().Should().BeTrue();
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'settings.app_updated' AND before_json IS NOT NULL AND after_json IS NOT NULL").Should().Be(1);

        // a restart: the compose value is read again, then what was saved is laid over it
        Live.IdleHours = 99;
        _api.Service<RuntimeSettings>().Apply();
        Live.IdleHours.Should().Be(2);

        var reset = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { reset = new[] { "idle_hours" } });
        reset.Status.Should().Be(HttpStatusCode.OK);
        Live.IdleHours.Should().Be(int.Parse(composeIdle!), "back to what compose says");
        (await Row("idle_hours")).GetProperty("overridden").GetBoolean().Should().BeFalse();
        Live.LockoutMinutes.Should().Be(30, "the others are left alone");
    }

    [Theory]
    [InlineData("idle_hours", "0")]
    [InlineData("idle_hours", "169")]
    [InlineData("idle_hours", "soon")]
    [InlineData("idle_hours", "")]
    [InlineData("max_failed_logins", "2")]
    [InlineData("lockout_minutes", "-5")]
    [InlineData("auth_requests_per_minute", "1")]
    [InlineData("inbox_interval_seconds", "5")]
    [InlineData("inbox_path", "inbox/relative")]
    [InlineData("nothing_like_it", "5")]
    public async Task A_value_that_makes_no_sense_is_refused_in_words_and_changes_nothing(string key, string value)
    {
        var before = Live.IdleHours;

        var (status, body) = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { [key] = value } });

        status.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        Live.IdleHours.Should().Be(before);
        _api.Scalar<long>("SELECT COUNT(*) FROM settings WHERE key LIKE 'app.%'").Should().Be(0);
    }

    [Fact]
    public async Task One_bad_value_in_a_save_stops_the_whole_save()
    {
        var (status, _) = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["idle_hours"] = "3", ["lockout_minutes"] = "0" } });

        status.Should().Be(HttpStatusCode.BadRequest);
        _api.Scalar<long>("SELECT COUNT(*) FROM settings WHERE key LIKE 'app.%'").Should().Be(0, "the good one was not kept either");
    }

    [Fact]
    public async Task The_inbox_folder_can_be_set_and_turned_off_by_saving_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tideline-inbox-" + Guid.NewGuid().ToString("N"));
        var set = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["inbox_path"] = folder } });
        set.Status.Should().Be(HttpStatusCode.OK, set.Body.ToString());
        Live.InboxPath.Should().Be(folder);
        (await _api.Get("/api/v1/imports")).GetProperty("inbox").GetProperty("path").GetString().Should().Be(folder);

        var off = await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["inbox_path"] = "" } });
        off.Status.Should().Be(HttpStatusCode.OK);
        Live.InboxPath.Should().BeNull();
        (await Row("inbox_path")).GetProperty("overridden").GetBoolean().Should().BeTrue("an empty folder saved on purpose is different from not set");
        (await _api.Get("/api/v1/imports")).GetProperty("inbox").GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_lower_number_of_tries_locks_an_account_sooner()
    {
        var (_, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        await _api.Send(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["max_failed_logins"] = "3" } });

        var anonymous = _api.NewClient();
        for (var i = 0; i < 3; i++)
            await anonymous.PostAsync("/api/v1/auth/login", JsonContent.Create(new { email = "sam@example.com", password = "wrong password here" }));

        var locked = await anonymous.PostAsync("/api/v1/auth/login", JsonContent.Create(new { email = "sam@example.com", password = ApiFixture.Password }));
        locked.StatusCode.Should().NotBe(HttpStatusCode.OK, "three wrong tries locked it, even with the right password");
        _ = secret;
    }

    [Fact]
    public async Task Only_the_owner_can_see_or_change_them()
    {
        var (_, secret) = _api.AddUser("sam@example.com", "Sam", Role.Member);
        var sam = await _api.SignInAsync("sam@example.com", ApiFixture.Password, secret);

        (await sam.GetAsync("/api/v1/settings/app")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sam.PutAsJsonAsync("/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["idle_hours"] = "1" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
