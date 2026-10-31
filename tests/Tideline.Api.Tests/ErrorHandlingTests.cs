using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tideline.Api.Tests;

/// <summary>What happens when something nobody planned for goes wrong: a clean answer for the person, the whole story in the log.</summary>
public sealed class ErrorHandlingTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private sealed record Entry(LogLevel Level, string Text, Exception? Error);

    private sealed class Capture : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(List<Entry> into) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (into) into.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private HttpClient SignInOn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var login = client.PostAsync("/api/v1/auth/login", new StringContent(JsonSerializer.Serialize(new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        var me = JsonDocument.Parse(client.GetStringAsync("/api/v1/auth/me").GetAwaiter().GetResult()).RootElement;
        client.DefaultRequestHeaders.Add("X-CSRF-Token", me.GetProperty("csrf").GetString());
        _api.Clock.Advance(TimeSpan.FromSeconds(31));
        var code = Tideline.Core.Security.Totp.Code(_api.OwnerSecret, Tideline.Core.Security.Totp.StepOf(_api.Clock.GetUtcNow()));
        client.PostAsync("/api/v1/auth/mfa/totp", new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult().EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task An_unexpected_failure_is_a_clean_500_that_leaks_nothing_and_is_logged_with_its_reference()
    {
        var log = new Capture();
        var host = _api.Host.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(log))));
        var client = SignInOn(host);
        _api.Execute("DROP TABLE flags"); // something the app cannot work around

        var response = await client.GetAsync("/api/v1/flags");
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("status").GetInt32().Should().Be(500);
        body.GetProperty("title").GetString().Should().Be("Something went wrong");
        text.Should().NotContainEquivalentOf("no such table").And.NotContainEquivalentOf("sqlite").And.NotContain("flags").And.NotContain(" at Tideline");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff"); // the security headers still ride on an error

        // the reference in the answer finds the entry in the log, and the entry has the real cause
        var reference = System.Text.RegularExpressions.Regex.Match(body.GetProperty("detail").GetString()!, "reference (\\S+) in").Groups[1].Value;
        reference.Should().NotBeEmpty();
        Entry[] entries;
        lock (log.Entries) entries = log.Entries.Where(e => e.Level == LogLevel.Error && e.Text.Contains(reference)).ToArray();
        entries.Should().ContainSingle();
        entries[0].Error!.Message.Should().Contain("no such table");
        entries[0].Text.Should().Contain("GET").And.Contain("/api/v1/flags");
    }

    [Fact]
    public async Task The_log_line_holds_the_method_and_path_but_not_the_query()
    {
        var log = new Capture();
        var host = _api.Host.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(log))));
        var client = SignInOn(host);
        _api.Execute("DROP TABLE flags");

        (await client.GetAsync("/api/v1/flags?state=SECRET-LOOKING-VALUE")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        Entry[] errors;
        lock (log.Entries) errors = log.Entries.Where(e => e.Level == LogLevel.Error).ToArray();
        errors.Should().NotBeEmpty();
        errors.Select(e => e.Text).Should().OnlyContain(t => !t.Contains("SECRET-LOOKING-VALUE"));
    }

    [Fact]
    public async Task A_body_that_is_not_the_shape_the_route_wants_is_a_400_problem_not_a_crash()
    {
        var response = await _api.Client.PostAsync("/api/v1/flags/1/acknowledge", new StringContent("this is not json", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_plain_words_errors_and_unknown_routes_are_left_as_they_were()
    {
        (await _api.Client.GetAsync("/api/v1/transactions/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _api.Anonymous.GetAsync("/api/v1/flags")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
