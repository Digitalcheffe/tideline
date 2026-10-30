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
using Tideline.Data.Security;
using Tideline.Data.Services;
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

// 10 requests a minute per address on what can be guessed at under /auth: every change (sign-in, codes, setup, first run). Reading
// who is signed in (GET /auth/me, and the page asking it when the tab gets focus) is not a guess, so it is not counted (docs/ARCHITECTURE.md)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, _) => await Problems.Write(context.HttpContext, 429, "Slow down", "Too many tries. Wait a minute and try again.", "rate_limited");
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/auth") || HttpMethods.IsGet(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter("open");
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

// behind a reverse proxy: believe it about the real visitor, but only the addresses listed (Tideline__TrustedProxies)
var behindProxy = TrustedProxies.IsSet(options.TrustedProxies);
if (behindProxy) builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o => TrustedProxies.Configure(o, options.TrustedProxies));

// the address the app listens on: TIDELINE_URL, in the compose file. (The framework's own ASPNETCORE_URLS and --urls still work when it is not set.)
if (builder.Configuration["TIDELINE_URL"] is { Length: > 0 } listenOn)
    builder.WebHost.UseUrls(listenOn.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

// options that start from compose and can be changed in the app (Settings): sign-in timing and limits, the inbox folder
builder.Services.AddSingleton<RuntimeSettings>();

// the inbox folder and how often it is looked at are asked for each time round, so changing them in the app needs no restart; with no folder a run does nothing
builder.Services.AddSingleton(sp => new InboxProcessor(() => sp.GetRequiredService<TidelineOptions>().InboxPath, sp.GetRequiredService<ImportPipeline>(),
    sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetService<ILogger<InboxProcessor>>()));
builder.Services.AddHostedService(sp => new InboxWatcher(sp.GetRequiredService<InboxProcessor>(),
    () => TimeSpan.FromSeconds(Math.Max(10, sp.GetRequiredService<TidelineOptions>().InboxIntervalSeconds)), sp.GetService<ILogger<InboxWatcher>>()));

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
    f => Links.For(f.Kind, f.Period, f.SubjectType, f.SubjectId), sp.GetService<ILogger<NotifyHost>>()));
if (!string.Equals(builder.Configuration["Notify:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NotifyHost>());

// SimpleFIN: the second door in. SimpleFin:Enabled=false leaves the six-hourly schedule off (the tests drive it by hand).
builder.Services.AddSingleton(_ => new Tideline.Import.SimpleFin.SimpleFinClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
builder.Services.AddSingleton(sp => new Tideline.Import.SimpleFin.SimpleFinSync(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<ImportPipeline>(),
    sp.GetRequiredService<Tideline.Import.SimpleFin.SimpleFinClient>(), sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<TimeProvider>(), d => sp.GetRequiredService<Clock>().LocalDate(d)));
builder.Services.AddSingleton(sp => new Tideline.Import.SimpleFin.SimpleFinHost(sp.GetRequiredService<Tideline.Import.SimpleFin.SimpleFinSync>(), sp.GetRequiredService<TimeProvider>(),
    () => sp.GetRequiredService<Clock>().LocalNow, sp.GetService<ILogger<Tideline.Import.SimpleFin.SimpleFinHost>>()));
if (!string.Equals(builder.Configuration["SimpleFin:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Tideline.Import.SimpleFin.SimpleFinHost>());

var app = builder.Build();

// account numbers: a keyed hash and a sealed last 4, with the key file beside the other keys
AccountNumbers.ConfigureOnce(() =>
{
    var db = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ConnectionString(app.Configuration)).DataSource;
    var keys = options.DataProtectionKeys ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, "keys");
    return AccountNumberKeys.LoadOrCreate(Path.Combine(keys, "account-numbers.key"));
});

if (!string.Equals(app.Configuration["Database:AutoMigrate"], "false", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = ConnectionString(app.Configuration);
    var dir = Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    Migrator.MigrateUp(connectionString);
    using var secure = new SqliteConnectionFactory(connectionString).Open();
    AccountNumbers.Verify(secure, app.Services.GetRequiredService<TimeProvider>());
    AccountNumbers.SecureExisting(secure, app.Services.GetRequiredService<TimeProvider>());
}

// the suggestions the app ships: one row each, the words brought up to date, every switch left as it was
if (!string.Equals(app.Configuration["Database:AutoMigrate"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var sync = new SqliteConnectionFactory(ConnectionString(app.Configuration)).Open();
    SuggestionSync.Run(sync, app.Services.GetRequiredService<TimeProvider>());
}

// take the compose values first, then lay what was saved in the app over them
{
    var runtime = app.Services.GetRequiredService<RuntimeSettings>();
    if (!string.Equals(app.Configuration["Database:AutoMigrate"], "false", StringComparison.OrdinalIgnoreCase)) runtime.Apply();
}

var devNoAuth = app.Services.GetRequiredService<TidelineOptions>().DevNoAuth;
if (devNoAuth)
{
    DevAuth.RequireSafe(app.Environment, app.Configuration);
    DevAuth.EnsureOwner(app.Services.GetRequiredService<SqliteConnectionFactory>(), app.Services.GetRequiredService<TimeProvider>());
    app.Logger.LogWarning("SIGN-IN IS SWITCHED OFF (Tideline:DevNoAuth). Everyone on this computer is the owner. Never use this with real data on a shared machine.");
}

if (behindProxy)
{
    // first of all, so everything after it (the sign-in limit, the audit log, the security headers) sees the real visitor and whether it was https
    app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>(); // reads the setting now, so a mistake stops the app at start
    app.UseForwardedHeaders();
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
api.MapBillEndpoints();
api.MapAppSettingsEndpoints();
api.MapImportEndpoints();
api.MapDataEndpoints();
api.MapPlanningEndpoints();
api.MapAssetEndpoints();
api.MapRecommendationEndpoints();
api.MapFlagEndpoints();
api.MapNotifyEndpoints();
api.MapSimpleFinEndpoints();
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
