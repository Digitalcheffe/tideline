using System.Globalization;
using System.Text.Json;
using Dapper;
using Tideline.Api.Auth;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;
using Tideline.Notify;

namespace Tideline.Api.Endpoints;

/// <summary>Channels, what is sent through them, a test, and the monthly summary. Owner only: it all lives under /notify.</summary>
public static class NotifyEndpoints
{
    public sealed record TestRequest(long ChannelId);
    public sealed record SummaryRequest(string? Month);

    private static string Describe(string kind, string sealedConfig, Notifier notifier)
    {
        try
        {
            var json = JsonDocument.Parse(notifier.Open(sealedConfig)).RootElement;
            return kind == "webhook"
                ? (Uri.TryCreate(json.GetProperty("url").GetString(), UriKind.Absolute, out var u) ? $"posts to {u.Host}" : "posts to a webhook")
                : $"mail from {json.GetProperty("from").GetString()} to {json.GetProperty("to").GetString()} through {json.GetProperty("host").GetString()}";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return "settings cannot be read (replace them)";
        }
    }

    private static string PreviousMonth(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static void MapNotifyEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notify/channels", (SqliteConnectionFactory f, Notifier notifier) =>
        {
            using var conn = f.Open();
            return Results.Ok(conn.Query<(long Id, string Kind, string Name, bool Active, string Config)>("SELECT id, kind, name, is_active, config_enc FROM notification_channels ORDER BY id")
                .Select(c => new { c.Id, c.Kind, c.Name, isActive = c.Active, summary = Describe(c.Kind, c.Config, notifier) }));
        });

        api.MapPost("/notify/channels", (ChannelInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Notifier notifier) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new ChannelEditor(c, clock, who, notifier).Create(input) }, created: true));
        api.MapPatch("/notify/channels/{id:long}", (long id, ChannelInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Notifier notifier) =>
            Edits.Run(http, f, t, (c, clock, who) => { new ChannelEditor(c, clock, who, notifier).Update(id, input); return new { updated = true }; }));
        api.MapDelete("/notify/channels/{id:long}", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Notifier notifier) =>
            Edits.Run(http, f, t, (c, clock, who) => { new ChannelEditor(c, clock, who, notifier).Delete(id); return new { deleted = true }; }));

        api.MapGet("/notify/prefs", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            var modes = Notifier.Modes(conn);
            return Results.Ok(FlagKinds.All.Select(k => new
            {
                kind = k, description = FlagKinds.Describe(k), mode = modes[k].ToString().ToLowerInvariant(), defaultMode = FlagKinds.DefaultMode(k).ToString().ToLowerInvariant(),
            }));
        });
        api.MapPut("/notify/prefs", (PrefsInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Notifier notifier) =>
            Edits.Run(http, f, t, (c, clock, who) => { new ChannelEditor(c, clock, who, notifier).SavePrefs(input); return new { saved = true }; }));

        // sends a message to one channel to see that it works; changes nothing in the ledger or the flags
        api.MapPost("/notify/test", async (TestRequest request, SqliteConnectionFactory f, Notifier notifier, TidelineOptions options, CancellationToken ct) =>
        {
            using var conn = f.Open();
            try
            {
                var error = await notifier.SendTestAsync(conn, request.ChannelId, options.PublicUrl, ct);
                return Results.Ok(new { ok = error is null, error });
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem(title: "Not found", detail: "There is no such channel.", statusCode: 404);
            }
        });

        api.MapGet("/notify/status", (SqliteConnectionFactory f, NotifyOptions options) =>
        {
            TypeHandlers.EnsureRegistered();
            using var conn = f.Open();
            var recent = conn.Query<(DateTimeOffset At, string Channel, string Status, string? Error, string? Message)>("""
                SELECT s.sent_at, c.name, s.status, s.error, f.message FROM notifications_sent s JOIN notification_channels c ON c.id = s.channel_id LEFT JOIN flags f ON f.id = s.flag_id
                ORDER BY s.id DESC LIMIT 20
                """).Select(r => new { at = r.At, channel = r.Channel, r.Status, r.Error, title = r.Message is null ? "Monthly summary" : FlagText.Split(r.Message).Title });
            return Results.Ok(new
            {
                quietStart = options.QuietStart, quietEnd = options.QuietEnd, summaryDay = options.SummaryDay, summaryTime = options.SummaryTime, evaluateAt = options.EvaluateAt,
                lastEvaluationDay = new SettingsRepository(conn, TimeProvider.System).Get(NotifyHost.LastEvaluationDayKey),
                recent,
            });
        });

        // what the summary for a month would say, and sending it now
        api.MapGet("/notify/summary", (string? month, SqliteConnectionFactory f, TimeProvider t, Clock clock) =>
        {
            using var conn = f.Open();
            var m = month ?? PreviousMonth(clock.Today);
            if (!Months.TryParse(m, out _)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["month"] = ["'month' must be YYYY-MM."] });
            var s = MonthlySummary.Build(conn, m, clock.Today, t);
            return Results.Ok(new { s.Month, s.Title, s.Body, s.NeedsYou, link = s.LinkPath });
        });
        api.MapPost("/notify/summary/send", async (SummaryRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Clock clock, Notifier notifier, TidelineOptions options, CancellationToken ct) =>
        {
            using var conn = f.Open();
            var m = request.Month ?? PreviousMonth(clock.Today);
            if (!Months.TryParse(m, out _)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["month"] = ["'month' must be YYYY-MM."] });
            var summary = MonthlySummary.Build(conn, m, clock.Today, t);
            var delivered = await notifier.SendSummaryAsync(conn, summary, options.PublicUrl, ct);
            if (delivered > 0)
            {
                new SettingsRepository(conn, t).Set(NotifyHost.SummaryKey(m), t.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
                new ChannelEditor(conn, t, http.Actor(), notifier).RecordSummarySent(m);
            }
            return Results.Ok(new { month = m, delivered, summary.Title });
        });
    }
}
