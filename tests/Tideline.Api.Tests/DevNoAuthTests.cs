using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Tideline.Api.Auth;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;

namespace Tideline.Api.Tests;

/// <summary>The development switch is the one exception to "no setting turns MFA off", so it is tested hard.</summary>
public sealed class DevNoAuthTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private WebApplicationFactory<Program> WithSwitch(string environment = "Development", string? urls = null) =>
        _api.Host.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Tideline:DevNoAuth", "true");
            if (urls is not null) b.UseSetting("urls", urls);
        });

    [Fact]
    public async Task It_is_off_unless_asked_for()
    {
        var response = await _api.Anonymous.GetAsync("/api/v1/overview");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var me = await _api.Get("/api/v1/auth/me");
        me.GetProperty("authDisabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task With_the_switch_on_nobody_has_to_sign_in_and_everyone_is_the_owner()
    {
        using var host = WithSwitch();
        var raw = _api.RawClient(host);

        (await raw.GetAsync("/api/v1/overview?from=2026-03-01&to=2026-03-31")).StatusCode.Should().Be(HttpStatusCode.OK);

        var me = JsonDocument.Parse(await raw.GetStringAsync("/api/v1/auth/me")).RootElement;
        me.GetProperty("authDisabled").GetBoolean().Should().BeTrue("the web app uses this to show its red banner");
        me.GetProperty("mfaPassed").GetBoolean().Should().BeTrue();
        me.GetProperty("user").GetProperty("role").GetString().Should().Be("owner");
        me.GetProperty("csrf").ValueKind.Should().Be(JsonValueKind.Null, "nothing to forge: there is no session");
    }

    [Fact]
    public async Task Changes_work_without_the_anti_forgery_header_and_are_audited_as_the_owner()
    {
        using var host = WithSwitch();
        var raw = _api.RawClient(host);
        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);

        var patch = await raw.PatchAsJsonAsync($"/api/v1/transactions/{id}", new { version, notes = "from the test" });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Scalar<long>("SELECT user_id FROM audit_log WHERE action = 'transaction.update' ORDER BY id DESC LIMIT 1").Should().Be(_api.OwnerId, "an existing owner is used");
    }

    [Fact]
    public async Task Without_an_owner_a_made_up_one_is_created_to_act_as()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tideline-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "t.db")};Pooling=False");
            Migrator.MigrateUp(factory.ConnectionString);
            DevAuth.EnsureOwner(factory, TimeProvider.System);
            DevAuth.EnsureOwner(factory, TimeProvider.System); // twice is fine

            using var conn = factory.Open();
            var owners = new UserRepository(conn, TimeProvider.System).All().Where(u => u.Role == Role.Owner).ToList();
            owners.Should().ContainSingle().Which.Email.Should().Be(DevAuth.Email);
            DevAuth.Principal(factory, TimeProvider.System).User.Id.Should().Be(owners[0].Id);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void It_refuses_to_start_outside_the_development_environment()
    {
        using var host = WithSwitch(environment: "Production");

        FluentActions.Invoking(() => host.CreateClient())
            .Should().Throw<Exception>().Where(e => e.ToString().Contains("only works when ASPNETCORE_ENVIRONMENT is Development"));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5130")]
    [InlineData("http://+:5130")]
    [InlineData("http://*:5130")]
    [InlineData("http://192.168.1.20:5130")]
    [InlineData("http://localhost:5130;http://0.0.0.0:5131")]
    public void It_refuses_to_start_if_the_app_could_be_reached_from_another_computer(string urls)
    {
        using var host = WithSwitch(urls: urls);

        FluentActions.Invoking(() => host.CreateClient())
            .Should().Throw<Exception>().Where(e => e.ToString().Contains("may only listen on this computer"));
    }

    [Theory]
    [InlineData("http://localhost:5130")]
    [InlineData("http://127.0.0.1:5130")]
    [InlineData("http://[::1]:5130")]
    public void Localhost_addresses_are_fine(string urls)
    {
        using var host = WithSwitch(urls: urls);

        FluentActions.Invoking(() => host.CreateClient()).Should().NotThrow();
    }

    [Fact]
    public async Task A_request_from_another_address_is_turned_away_even_with_the_switch_on()
    {
        using var host = WithSwitch();
        _ = host.CreateClient(); // starts the host

        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/v1/overview";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        });

        context.Response.StatusCode.Should().Be(403);
    }
}
