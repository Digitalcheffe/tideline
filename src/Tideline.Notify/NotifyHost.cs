using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;

namespace Tideline.Notify;

/// <summary>Times the evaluator, notifier and monthly summary run at. All local, in the household's time zone.</summary>
public sealed class NotifyOptions
{
    public int SummaryDay { get; set; } = 1;
    public string SummaryTime { get; set; } = "08:00";
    public string QuietStart { get; set; } = "21:00";
    public string QuietEnd { get; set; } = "07:00";
    public string EvaluateAt { get; set; } = "06:00";

    public static TimeOnly Time(string text, TimeOnly fallback) => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

    public TimeOnly SummaryAt => Time(SummaryTime, new TimeOnly(8, 0));
    public TimeOnly QuietFrom => Time(QuietStart, NotifierSettings.DefaultQuietStart);
    public TimeOnly QuietTo => Time(QuietEnd, NotifierSettings.DefaultQuietEnd);
    public TimeOnly EvaluateTime => Time(EvaluateAt, new TimeOnly(6, 0));
}

/// <summary>
/// Runs the evaluators after every import and every day at 06:00, hands what they find to the notifier, and sends the monthly summary
/// (docs/10-notifications-logging.md). One tick does everything that is due, so a test can drive it with a clock.
/// </summary>
public sealed class NotifyHost(
    SqliteConnectionFactory factory, Evaluator evaluator, Notifier notifier, TimeProvider clock, NotifyOptions options,
    Func<DateTime> localNow, Func<string> baseUrl, Func<FlagRow, string> linkFor, ILogger<NotifyHost>? logger = null) : BackgroundService
{
    public const string LastEvaluationDayKey = "notify_last_evaluation_day";
    public const string LastBatchKey = "notify_last_batch";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger?.LogError(e, "Notification run failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static string SummaryKey(string month) => $"summary_sent:{month}";
    public static string SummaryFailedKey(string month) => $"summary_failed:{month}";

    public async Task TickAsync(CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        var local = localNow();
        var today = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);

        using var conn = factory.Open();
        var settings = new SettingsRepository(conn, clock);
        var events = new SystemEventRepository(conn, clock);

        // 1. the evaluators: after an import, or once a day at the set time
        var lastBatch = conn.ExecuteScalar<long?>("SELECT MAX(id) FROM import_batches") ?? 0;
        var importedSince = settings.Get(LastBatchKey) != lastBatch.ToString(CultureInfo.InvariantCulture);
        var dailyDue = time >= options.EvaluateTime && settings.Get(LastEvaluationDayKey) != today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (importedSince || dailyDue)
        {
            var result = evaluator.Run(conn, today, clock);
            settings.Set(LastBatchKey, lastBatch.ToString(CultureInfo.InvariantCulture));
            if (dailyDue) settings.Set(LastEvaluationDayKey, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            events.Add(EventLevel.Info, "evaluator", $"Checked everything: {result.Found} things found, {result.Created} new, {result.Resolved} cleared.");
        }

        // 2. tell people about what is open and urgent
        await notifier.RunAsync(conn, time, new NotifierSettings(options.QuietFrom, options.QuietTo, baseUrl()), linkFor, ct);

        // 3. the summary of the month that just ended, once, from the set day and time
        var previous = new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var failedTries = int.TryParse(settings.Get(SummaryFailedKey(previous)), out var tries) ? tries : 0;
        if (today.Day >= options.SummaryDay && time >= options.SummaryAt && settings.Get(SummaryKey(previous)) is null && failedTries < Notifier.MaxTries && Notifier.Channels(conn).Any(c => c.IsActive))
        {
            var summary = MonthlySummary.Build(conn, previous, today, clock);
            if (await notifier.SendSummaryAsync(conn, summary, baseUrl(), ct) > 0)
            {
                settings.Set(SummaryKey(previous), clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
                events.Add(EventLevel.Info, "notify", $"Sent the summary for {previous}.");
            }
            else
            {
                settings.Set(SummaryFailedKey(previous), (failedTries + 1).ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
