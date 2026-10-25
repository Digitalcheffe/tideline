using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tideline.Api.Auth;
using Tideline.Cli;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Import;
using Tideline.Notify;

namespace Tideline.Api.Tests;

/// <summary>A clock the tests can move. Each authenticator code can be used once, so signing in again needs the next 30 seconds.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for SimpleFIN Bridge: answers the claim and the accounts requests with what the test sets, and remembers what it was asked.</summary>
public sealed class FakeSimpleFin : HttpMessageHandler
{
    public const string Access = "https://demouser:demopass@bridge.example.test/simplefin";
    public string AccountsJson { get; set; } = "{\"errlist\":[],\"accounts\":[]}";
    public HttpStatusCode ClaimStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode AccountsStatus { get; set; } = HttpStatusCode.OK;
    public List<(HttpMethod Method, Uri Url, string? Auth)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
        if (request.Method == HttpMethod.Post)
            return Task.FromResult(new HttpResponseMessage(ClaimStatus) { Content = new StringContent(ClaimStatus == HttpStatusCode.OK ? Access : "") });
        return Task.FromResult(new HttpResponseMessage(AccountsStatus) { Content = new StringContent(AccountsJson) });
    }
}

/// <summary>Stands in for a channel: remembers what it was asked to send, and can be told to fail.</summary>
public sealed class RecordingSender(string kind) : IChannelSender
{
    private readonly IChannelSender _real = kind == "webhook" ? new WebhookSender(new HttpClient()) : new SmtpSender();

    public string Kind => kind;
    public List<NotificationMessage> Sent { get; } = [];
    public List<string> Configs { get; } = [];
    public string? FailWith { get; set; }

    public string? Check(string configJson) => _real.Check(configJson);

    public Task SendAsync(string configJson, NotificationMessage message, CancellationToken ct)
    {
        if (FailWith is { } why) throw new InvalidOperationException(why);
        Configs.Add(configJson);
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The whole app on a temporary database, loaded from the made-up fixtures: the household in tests/fixtures/household.json
/// and the checking, savings and card exports for March 2026. "Today" is 1 April 2026.
/// </summary>
public sealed class ApiFixture : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 4, 1, 14, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-api-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public const string OwnerEmail = "owner@example.com";
    public const string Password = "a long passphrase of mine";

    public string ConnectionString { get; }

    /// <summary>Signed in as the owner, second step passed, with the anti-forgery header set. What most tests use.</summary>
    public HttpClient Client { get; }

    /// <summary>Nobody is signed in.</summary>
    public HttpClient Anonymous { get; }

    public TestClock Clock { get; }
    public FakeSimpleFin SimpleFin { get; } = new();
    public RecordingSender Webhook { get; } = new("webhook");
    public RecordingSender Email { get; } = new("email");
    public string OwnerSecret { get; }
    public long OwnerId { get; }
    public string KeysFolder => Path.Combine(_dir, "keys");

    public ApiFixture(bool importFixtures = true, int? demoLines = null)
    {
        Directory.CreateDirectory(_dir);
        ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";

        Migrator.MigrateUp(ConnectionString);
        var clock = new TestClock(Now);
        Clock = clock;
        var demoFolder = Path.Combine(_dir, "demo");
        var json = demoLines is { } n
            ? File.ReadAllText(DemoData.Generate(demoFolder, DemoOptions.Default with { Lines = n }).HouseholdFile)
            : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "household.json"));
        HouseholdSetup.Apply(HouseholdSetup.Parse(json), new SqliteConnectionFactory(ConnectionString), clock);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:ConnectionString", ConnectionString);
            b.UseSetting("Tideline:Timezone", "UTC");
            b.UseSetting("Notify:Enabled", "false"); // the tests run the schedule by hand
            b.UseSetting("SimpleFin:Enabled", "false");
            b.UseSetting("Tideline:SecureCookie", "false"); // the test host is plain http; one test checks the real attributes
            b.UseSetting("Tideline:AuthRequestsPerMinute", "100000");
            b.UseSetting("Tideline:DataProtectionKeys", KeysFolder);
            b.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                s.RemoveAll<Tideline.Import.SimpleFin.SimpleFinClient>();
                s.AddSingleton(new Tideline.Import.SimpleFin.SimpleFinClient(new HttpClient(SimpleFin)));
                s.RemoveAll<IChannelSender>();
                s.AddSingleton<IChannelSender>(Webhook);
                s.AddSingleton<IChannelSender>(Email);
            });
        });
        Anonymous = _factory.CreateClient();

        // the owner: set up the way a real one would be, then signed in through the real endpoints
        OwnerSecret = Totp.NewSecret();
        using (var conn = new SqliteConnectionFactory(ConnectionString).Open())
        {
            var users = new UserRepository(conn, clock);
            OwnerId = users.Create(OwnerEmail, "Test Owner", Role.Owner);
            users.SetPassword(OwnerId, AuthService.HashPassword(Password));
            new MfaRepository(conn, clock).AddTotp(OwnerId, "Authenticator app", _factory.Services.GetRequiredService<SecretBox>().Protect(OwnerSecret));
            users.SetMfaEnrolled(OwnerId, clock.GetUtcNow());
        }
        Client = SignInAsync(OwnerEmail, Password, OwnerSecret).GetAwaiter().GetResult();

        if (demoLines is not null)
        {
            foreach (var path in ImportOrder.OldestFirst(Directory.GetFiles(demoFolder, "acct_*.csv")))
            {
                var result = Pipeline.ImportTruistAsync(Path.GetFileName(path), File.ReadAllText(path)).GetAwaiter().GetResult();
                if (!result.Succeeded) throw new InvalidOperationException($"demo file {path} failed: {result.Error}");
            }
        }
        else if (importFixtures)
        {
            foreach (var name in new[] { "acct_1111_03_01_2026_to_03_31_2026.csv", "acct_3333_03_01_2026_to_03_31_2026.csv", "acct_2222_03_01_2026_to_03_31_2026.csv" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", name);
                var result = Pipeline.ImportTruistAsync(name, File.ReadAllText(path)).GetAwaiter().GetResult();
                if (!result.Succeeded) throw new InvalidOperationException($"fixture {name} failed: {result.Error}");
            }
        }
    }

    public HttpClient NewClient() => _factory.CreateClient();

    /// <summary>A client that keeps no cookies, so a test can hold and replay them by hand.</summary>
    public HttpClient RawClient(WebApplicationFactory<Program>? host = null) =>
        (host ?? _factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>A person already set up: password chosen and, unless told otherwise, the authenticator enrolled.</summary>
    public (long Id, string Secret) AddUser(string email, string name, Role role, bool enrolled = true, string? password = Password)
    {
        using var conn = new SqliteConnectionFactory(ConnectionString).Open();
        var users = new UserRepository(conn, Clock);
        var id = users.Create(email, name, role);
        if (password is not null) users.SetPassword(id, AuthService.HashPassword(password));
        var secret = Totp.NewSecret();
        if (enrolled)
        {
            new MfaRepository(conn, Clock).AddTotp(id, "Authenticator app", _factory.Services.GetRequiredService<SecretBox>().Protect(secret));
            users.SetMfaEnrolled(id, Clock.GetUtcNow());
        }
        return (id, secret);
    }

    /// <summary>The code an authenticator would show right now, one 30 second step later than the last one given.</summary>
    public string NextCode(string secret)
    {
        Clock.Advance(TimeSpan.FromSeconds(31));
        return Totp.Code(secret, Totp.StepOf(Clock.GetUtcNow()));
    }

    public T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public WebApplicationFactory<Program> Host => _factory;

    /// <summary>Password, then the authenticator code: the whole sign-in, as a person does it.</summary>
    public async Task<HttpClient> SignInAsync(string email, string password, string totpSecret)
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        if (!login.IsSuccessStatusCode) throw new InvalidOperationException($"sign-in failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement;
        client.DefaultRequestHeaders.Add("X-CSRF-Token", me.GetProperty("csrf").GetString());

        Clock.Advance(TimeSpan.FromSeconds(31)); // a code is good once; this is the next one
        var code = Totp.Code(totpSecret, Totp.StepOf(Clock.GetUtcNow()));
        var second = await client.PostAsJsonAsync("/api/v1/auth/mfa/totp", new { code });
        if (!second.IsSuccessStatusCode) throw new InvalidOperationException($"second step failed: {(int)second.StatusCode} {await second.Content.ReadAsStringAsync()}");
        return client;
    }

    public ImportPipeline Pipeline => _factory.Services.GetRequiredService<ImportPipeline>();

    public SqliteConnection Open() => new SqliteConnectionFactory(ConnectionString).Open();

    public T Scalar<T>(string sql, object? args = null)
    {
        using var c = Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    public void Execute(string sql, object? args = null)
    {
        using var c = Open();
        c.Execute(sql, args);
    }

    public async Task<JsonElement> Get(string url)
    {
        var response = await Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length == 0) return (response.StatusCode, default);
        try { return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone()); }
        catch (JsonException) { throw new InvalidOperationException($"{method} {url} -> {(int)response.StatusCode}, not JSON: {text}"); }
    }

    public long IdOf(string description) => Scalar<long>("SELECT id FROM transactions WHERE original_description = @description", new { description });

    public async Task<string> VersionOf(long id) => (await Get($"/api/v1/transactions/{id}")).GetProperty("version").GetString()!;

    /// <summary>The signed sum of the lines a drill-down opens, straight from /lines.</summary>
    public async Task<(long Count, long SumCents)> Drill(string? drill)
    {
        Assert.False(string.IsNullOrEmpty(drill), "expected a drill query");
        var lines = await Get("/api/v1/lines?" + drill + "&pageSize=500");
        var summary = lines.GetProperty("summary");
        return (summary.GetProperty("count").GetInt64(), summary.GetProperty("sumCents").GetInt64());
    }

    public void Dispose()
    {
        Client.Dispose();
        Anonymous.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public static class JsonExtensions
{
    public static long Cents(this JsonElement e) => e.GetProperty("cents").GetInt64();
    public static string? Drill(this JsonElement e) => e.GetProperty("drill").GetString();
    public static JsonElement.ArrayEnumerator Items(this JsonElement e) => e.EnumerateArray();
}
