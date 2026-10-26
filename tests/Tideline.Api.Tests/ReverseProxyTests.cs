using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tideline.Api.Tests;

/// <summary>Behind a reverse proxy: the real visitor is believed only from the proxy addresses that were listed.</summary>
public sealed class ReverseProxyTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    /// <summary>Makes every request seem to arrive from this address, as it would from a real machine.</summary>
    private sealed class FixedSender(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, run) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return run();
            });
            next(app);
        };
    }

    private WebApplicationFactory<Program> Host(string sender, string? trusted, int? limit = null) =>
        _api.Host.WithWebHostBuilder(b =>
        {
            if (trusted is not null) b.UseSetting("Tideline:TrustedProxies", trusted);
            if (limit is { } n) b.UseSetting("Tideline:AuthRequestsPerMinute", n.ToString());
            b.ConfigureServices(s => s.AddTransient<IStartupFilter>(_ => new FixedSender(sender)));
        });

    private static async Task<HttpResponseMessage> BadLogin(HttpClient client, string? forwardedFor = null, string? proto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = "nobody@example.com", password = "whatever it is" }) };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        return await client.SendAsync(request);
    }

    private string LastLoginAddress() => _api.Scalar<string>("SELECT ip FROM audit_log WHERE action = 'login.failure' ORDER BY id DESC LIMIT 1");

    [Fact]
    public async Task A_trusted_proxy_is_believed_about_who_the_visitor_is()
    {
        using var host = Host("10.0.0.5", "10.0.0.5");

        await BadLogin(_api.RawClient(host), forwardedFor: "203.0.113.9");

        LastLoginAddress().Should().Be("203.0.113.9", "the audit log shows the real visitor, not the proxy");
    }

    [Fact]
    public async Task A_range_of_addresses_can_be_trusted_and_the_list_can_have_several()
    {
        using var host = Host("172.18.4.7", "10.0.0.5, 172.18.0.0/16");

        await BadLogin(_api.RawClient(host), forwardedFor: "198.51.100.20");

        LastLoginAddress().Should().Be("198.51.100.20");
    }

    [Fact]
    public async Task Anyone_else_who_sends_the_header_is_not_believed()
    {
        using var host = Host("10.0.0.9", "10.0.0.5"); // this sender is not the proxy

        await BadLogin(_api.RawClient(host), forwardedFor: "203.0.113.9");

        LastLoginAddress().Should().Be("10.0.0.9", "a visitor cannot make up an address");
    }

    [Fact]
    public async Task With_no_proxy_set_the_header_is_ignored()
    {
        using var host = Host("10.0.0.5", trusted: null);

        await BadLogin(_api.RawClient(host), forwardedFor: "203.0.113.9");

        LastLoginAddress().Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task Only_the_one_proxy_in_front_is_read_so_an_address_a_visitor_adds_does_not_count()
    {
        using var host = Host("10.0.0.5", "10.0.0.5");

        await BadLogin(_api.RawClient(host), forwardedFor: "6.6.6.6, 203.0.113.9"); // the visitor wrote 6.6.6.6; the proxy added the real one last

        LastLoginAddress().Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task The_sign_in_limit_is_counted_per_real_visitor_not_for_the_whole_proxy()
    {
        using var host = Host("10.0.0.5", "10.0.0.5", limit: 2);
        var client = _api.RawClient(host);

        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) first.Add((await BadLogin(client, forwardedFor: "203.0.113.1")).StatusCode);
        var other = (await BadLogin(client, forwardedFor: "203.0.113.2")).StatusCode;

        first.Take(2).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        first.Skip(2).Should().OnlyContain(s => s == (HttpStatusCode)429, "the first visitor used up their tries");
        other.Should().Be(HttpStatusCode.Unauthorized, "a different visitor behind the same proxy is not locked out by them");
    }

    [Fact]
    public async Task A_visitor_who_came_in_over_https_gets_the_strict_transport_header()
    {
        using var host = Host("10.0.0.5", "10.0.0.5");

        var secure = await BadLogin(_api.RawClient(host), forwardedFor: "203.0.113.9", proto: "https");
        var plain = await BadLogin(_api.RawClient(host), forwardedFor: "203.0.113.9");

        secure.Headers.Contains("Strict-Transport-Security").Should().BeTrue("the proxy said it was https");
        plain.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Theory]
    [InlineData("not-an-address", "not an address")]
    [InlineData("10.0.0.0/99", "not an address range")]
    [InlineData(" , ", "lists no addresses")]
    public void A_mistake_in_the_setting_stops_the_app_and_names_the_setting(string setting, string mention)
    {
        using var host = _api.Host.WithWebHostBuilder(b => b.UseSetting("Tideline:TrustedProxies", setting));

        var start = () => host.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Tideline__TrustedProxies").And.Contain(mention);
    }
}
