using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tideline.Api;
using Tideline.Api.Auth;
using Tideline.Api.Endpoints;
using Tideline.Data;
using Tideline.Import;
using Tideline.Notify;

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

// the keys that encrypt authenticator secrets live next to the database unless told otherwise
builder.Services.AddDataProtection().SetApplicationName("Tideline");
builder.Services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((o, sp) =>
{
    var options = sp.GetRequiredService<TidelineOptions>();
    var db = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ConnectionString(sp.GetRequiredService<IConfiguration>())).DataSource;
    var dir = options.DataProtectionKeys ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, "keys");
    Directory.CreateDirectory(dir);
    o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(dir), NullLoggerFactory.Instance);
});
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<Csrf>();
builder.Services.AddSingleton<AuthService>();

// 10 requests a minute per address on everything under /auth (docs/09-security.md)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, _) => await Problems.Write(context.HttpContext, 429, "Slow down", "Too many tries. Wait a minute and try again.", "rate_limited");
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/auth")) return RateLimitPartition.GetNoLimiter("open");
        var limit = ctx.RequestServices.GetRequiredService<TidelineOptions>().AuthRequestsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var options = builder.Configuration.GetSection("Tideline").Get<TidelineOptions>() ?? new TidelineOptions();

if (!string.IsNullOrWhiteSpace(options.InboxPath))
{
    builder.Services.AddSingleton(sp => new InboxProcessor(options.InboxPath, sp.GetRequiredService<ImportPipeline>(),
        sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>(),
        sp.GetService<ILogger<InboxProcessor>>()));
    builder.Services.AddHostedService(sp => new InboxWatcher(sp.GetRequiredService<InboxProcessor>(),
        TimeSpan.FromSeconds(Math.Max(10, options.InboxIntervalSeconds)), sp.GetService<ILogger<InboxWatcher>>()));
}

// flags, notifications and the monthly summary. Notify:Enabled=false leaves the schedule off (the tests drive it by hand).
var notify = builder.Configuration.GetSection("Notify").Get<NotifyOptions>() ?? new NotifyOptions();
builder.Services.AddSingleton(notify);
builder.Services.AddSingleton<ISecretProtector, NotifySecrets>();
builder.Services.AddSingleton<IChannelSender>(_ => new WebhookSender(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
builder.Services.AddSingleton<IChannelSender, SmtpSender>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton(_ => new Evaluator()); // every family; the constructor with a list is for tests
builder.Services.AddSingleton(sp => new NotifyHost(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<Evaluator>(), sp.GetRequiredService<Notifier>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<NotifyOptions>(), () => sp.GetRequiredService<Clock>().LocalNow, () => sp.GetRequiredService<TidelineOptions>().PublicUrl,
    f => Links.For(f.Kind, f.Period), sp.GetService<ILogger<NotifyHost>>()));
if (!string.Equals(builder.Configuration["Notify:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NotifyHost>());

var app = builder.Build();

if (!string.Equals(app.Configuration["Database:AutoMigrate"], "false", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = ConnectionString(app.Configuration);
    var dir = Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    Migrator.MigrateUp(connectionString);
}

var devNoAuth = app.Services.GetRequiredService<TidelineOptions>().DevNoAuth;
if (devNoAuth)
{
    DevAuth.RequireSafe(app.Environment, app.Configuration);
    DevAuth.EnsureOwner(app.Services.GetRequiredService<SqliteConnectionFactory>(), app.Services.GetRequiredService<TimeProvider>());
    app.Logger.LogWarning("SIGN-IN IS SWITCHED OFF (Tideline:DevNoAuth). Everyone on this computer is the owner. Never use this with real data on a shared machine.");
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<AuthMiddleware>();
app.UseMiddleware<AccessMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapAdminEndpoints();
api.MapSetupEndpoints();
api.MapImportEndpoints();
api.MapDataEndpoints();
api.MapPlanningEndpoints();
api.MapFlagEndpoints();
api.MapNotifyEndpoints();
api.MapLookupEndpoints();
api.MapLinesEndpoints();
api.MapScreenEndpoints();
api.MapTransactionEndpoints();

// no data beyond "up"
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
