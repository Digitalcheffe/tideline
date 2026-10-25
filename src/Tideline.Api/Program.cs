using Tideline.Api;
using Tideline.Api.Endpoints;
using Tideline.Data;
using Tideline.Import;

var builder = WebApplication.CreateBuilder(args);

// Settings are read lazily from IConfiguration so a test host can override them.
const string DefaultConnection = "Data Source=tideline.db";
string ConnectionString(IConfiguration c) => c["Database:ConnectionString"] ?? DefaultConnection;

builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Tideline").Get<TidelineOptions>() ?? new TidelineOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SqliteConnectionFactory(ConnectionString(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<WriteGate>();
builder.Services.AddSingleton<ImportPipeline>();
builder.Services.AddSingleton<Clock>();

var options = builder.Configuration.GetSection("Tideline").Get<TidelineOptions>() ?? new TidelineOptions();

if (!string.IsNullOrWhiteSpace(options.InboxPath))
{
    builder.Services.AddSingleton(sp => new InboxProcessor(options.InboxPath, sp.GetRequiredService<ImportPipeline>(),
        sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>(),
        sp.GetService<ILogger<InboxProcessor>>()));
    builder.Services.AddHostedService(sp => new InboxWatcher(sp.GetRequiredService<InboxProcessor>(),
        TimeSpan.FromSeconds(Math.Max(10, options.InboxIntervalSeconds)), sp.GetService<ILogger<InboxWatcher>>()));
}

var app = builder.Build();

if (!string.Equals(app.Configuration["Database:AutoMigrate"], "false", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = ConnectionString(app.Configuration);
    var dir = Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    Migrator.MigrateUp(connectionString);
}

app.UseMiddleware<LocalOnlyMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/v1");
api.MapLookupEndpoints();
api.MapLinesEndpoints();
api.MapScreenEndpoints();
api.MapTransactionEndpoints();

app.MapGet("/health", (SqliteConnectionFactory factory) =>
{
    using var conn = factory.Open();
    return Results.Ok(new { status = "ok", db = "ok" });
});

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html")))
    app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Visible to the test project (WebApplicationFactory).</summary>
public partial class Program;
