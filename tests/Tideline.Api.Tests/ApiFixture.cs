using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tideline.Cli;
using Tideline.Data;
using Tideline.Import;

namespace Tideline.Api.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
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

    public string ConnectionString { get; }
    public HttpClient Client { get; }

    public ApiFixture(bool importFixtures = true, int? demoLines = null)
    {
        Directory.CreateDirectory(_dir);
        ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";

        Migrator.MigrateUp(ConnectionString);
        var clock = new FixedClock(Now);
        var demoFolder = Path.Combine(_dir, "demo");
        var json = demoLines is { } n
            ? File.ReadAllText(DemoData.Generate(demoFolder, DemoOptions.Default with { Lines = n }).HouseholdFile)
            : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "household.json"));
        HouseholdSetup.Apply(HouseholdSetup.Parse(json), new SqliteConnectionFactory(ConnectionString), clock);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:ConnectionString", ConnectionString);
            b.UseSetting("Tideline:Timezone", "UTC");
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        });
        Client = _factory.CreateClient();

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
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
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
