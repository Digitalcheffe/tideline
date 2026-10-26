using System.Data;
using System.Globalization;
using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;

namespace Tideline.Notify;

public sealed record ChannelRow(long Id, string Kind, string Name, bool IsActive);

public sealed record NotifierSettings(TimeOnly QuietStart, TimeOnly QuietEnd, string BaseUrl)
{
    public static readonly TimeOnly DefaultQuietStart = new(21, 0);
    public static readonly TimeOnly DefaultQuietEnd = new(7, 0);
}

public sealed record NotifyRun(int Sent, int Failed, int WaitingForQuietHours, int Digested);

/// <summary>
/// Tells people about flags (docs/ARCHITECTURE.md). Each kind is sent at once, left for the monthly summary, or
/// switched off. Urgent ones wait out quiet hours. Every attempt is written down, and a failed one is tried again on the next run.
/// </summary>
public sealed class Notifier(IEnumerable<IChannelSender> senders, ISecretProtector protector, TimeProvider clock)
{
    public const int MaxTries = 3;

    /// <summary>More new urgent flags than this in one run are sent as one message, so a first run on old data does not flood a phone.</summary>
    public const int DigestAbove = 5;

    private readonly Dictionary<string, IChannelSender> _senders = senders.ToDictionary(s => s.Kind);

    // ---- channels ----------------------------------------------------------------------------------------------------

    public static IReadOnlyList<ChannelRow> Channels(IDbConnection conn) =>
        conn.Query<(long Id, string Kind, string Name, bool Active)>("SELECT id, kind, name, is_active FROM notification_channels ORDER BY id")
            .Select(c => new ChannelRow(c.Id, c.Kind, c.Name, c.Active)).ToList();

    public string? Check(string kind, string configJson) =>
        _senders.TryGetValue(kind, out var s) ? s.Check(configJson) : "That kind of channel is not one of webhook or email.";

    public string Seal(string configJson) => protector.Protect(configJson);

    public string Open(string sealedText) => protector.Unprotect(sealedText);

    // ---- what to send ------------------------------------------------------------------------------------------------

    public static NotifyMode ModeFor(IDbConnection conn, string kind)
    {
        var chosen = conn.ExecuteScalar<string?>("SELECT mode FROM notification_prefs WHERE flag_kind = @kind", new { kind });
        return chosen switch { "immediate" => NotifyMode.Immediate, "summary" => NotifyMode.Summary, "off" => NotifyMode.Off, _ => FlagKinds.DefaultMode(kind) };
    }

    public static IReadOnlyDictionary<string, NotifyMode> Modes(IDbConnection conn) => FlagKinds.All.ToDictionary(k => k, k => ModeFor(conn, k));

    private static NotificationMessage For(FlagRow f, string baseUrl, string link, DateTimeOffset now)
    {
        var (title, detail) = FlagText.Split(f.Message);
        return new NotificationMessage(f.Kind, f.Severity, title, detail, baseUrl.TrimEnd('/') + link, f.Id, now);
    }

    /// <summary>
    /// Sends each open urgent flag to each active channel that has not had it, unless it is quiet hours. <paramref name="linkFor"/> says where
    /// in the app a flag points (a path), so this project does not need to know the screens.
    /// </summary>
    public async Task<NotifyRun> RunAsync(IDbConnection conn, TimeOnly localNow, NotifierSettings settings, Func<FlagRow, string> linkFor, CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        var now = clock.GetUtcNow();
        var channels = Channels(conn).Where(c => c.IsActive).ToList();
        if (channels.Count == 0) return new NotifyRun(0, 0, 0, 0);

        var urgent = new FlagRepository(conn, clock).Open().Where(f => ModeFor(conn, f.Kind) == NotifyMode.Immediate).ToList();
        if (urgent.Count == 0) return new NotifyRun(0, 0, 0, 0);

        if (QuietHours.IsQuiet(localNow, settings.QuietStart, settings.QuietEnd))
            return new NotifyRun(0, 0, urgent.Count, 0);

        int sent = 0, failed = 0, digested = 0;
        foreach (var channel in channels)
        {
            var sealedConfig = conn.ExecuteScalar<string>("SELECT config_enc FROM notification_channels WHERE id = @id", new { id = channel.Id })!;
            var pending = urgent.Where(f => NeedsSending(conn, f.Id, channel.Id)).OrderByDescending(f => SeverityExtensions.Rank(f.Severity)).ThenBy(f => f.Id).ToList();
            if (pending.Count == 0) continue;

            if (pending.Count > DigestAbove)
            {
                var lines = pending.Select(f => "- " + FlagText.Split(f.Message).Title);
                var digest = new NotificationMessage("digest", pending.Max(f => f.Severity) == "alert" ? "alert" : "warn", $"{pending.Count} things need you",
                    string.Join("\n", lines), settings.BaseUrl.TrimEnd('/') + "/", null, now);
                var ok = await Deliver(conn, channel, sealedConfig, digest, pending.Select(f => (long?)f.Id), ct);
                if (ok) { sent++; digested += pending.Count; } else failed++;
                continue;
            }

            foreach (var flag in pending)
            {
                var ok = await Deliver(conn, channel, sealedConfig, For(flag, settings.BaseUrl, linkFor(flag), now), [flag.Id], ct);
                if (ok) sent++; else failed++;
            }
        }
        return new NotifyRun(sent, failed, 0, digested);
    }

    private static bool NeedsSending(IDbConnection conn, long flagId, long channelId)
    {
        var rows = conn.Query<string>("SELECT status FROM notifications_sent WHERE flag_id = @flagId AND channel_id = @channelId", new { flagId, channelId }).ToList();
        return !rows.Contains("sent") && rows.Count(s => s == "failed") < MaxTries;
    }

    private async Task<bool> Deliver(IDbConnection conn, ChannelRow channel, string sealedConfig, NotificationMessage message, IEnumerable<long?> flagIds, CancellationToken ct)
    {
        string? error = null;
        try
        {
            if (!_senders.TryGetValue(channel.Kind, out var sender)) throw new InvalidOperationException("No sender for this kind of channel.");
            await sender.SendAsync(protector.Unprotect(sealedConfig), message, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = Short(e);
        }

        foreach (var flagId in flagIds)
            conn.Execute("INSERT INTO notifications_sent (channel_id, flag_id, sent_at, status, error) VALUES (@channel, @flag, @now, @status, @error)",
                new { channel = channel.Id, flag = flagId, now = clock.GetUtcNow(), status = error is null ? "sent" : "failed", error });

        var events = new SystemEventRepository(conn, clock);
        if (error is null) events.Add(EventLevel.Info, "notify", $"Sent \"{message.Title}\" to {channel.Name}.");
        else events.Add(EventLevel.Warn, "notify", $"Could not send to {channel.Name}: {error}");
        return error is null;
    }

    /// <summary>A reason fit to show and store: the kind of failure and the first line, never a stack or a secret.</summary>
    private static string Short(Exception e)
    {
        var text = e is HttpRequestException h ? $"Could not reach the server ({h.Message})" : e.Message;
        text = text.Split('\n')[0].Trim();
        return text.Length > 300 ? text[..300] : text;
    }

    // ---- a test, and the monthly summary --------------------------------------------------------------------------------

    public async Task<string?> SendTestAsync(IDbConnection conn, long channelId, string baseUrl, CancellationToken ct = default)
    {
        var channel = Channels(conn).FirstOrDefault(c => c.Id == channelId) ?? throw new KeyNotFoundException();
        var sealedConfig = conn.ExecuteScalar<string>("SELECT config_enc FROM notification_channels WHERE id = @id", new { id = channelId })!;
        try
        {
            await _senders[channel.Kind].SendAsync(protector.Unprotect(sealedConfig),
                new NotificationMessage("test", "info", "Tideline test message", "If you can read this, the channel works.", baseUrl.TrimEnd('/') + "/", null, clock.GetUtcNow()), ct);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Short(e);
        }
    }

    public async Task<int> SendSummaryAsync(IDbConnection conn, SummaryMessage summary, string baseUrl, CancellationToken ct = default)
    {
        TypeHandlers.EnsureRegistered();
        var delivered = 0;
        var message = new NotificationMessage("monthly_summary", summary.NeedsYou > 0 ? "warn" : "info", summary.Title, summary.Body, baseUrl.TrimEnd('/') + summary.LinkPath, null, clock.GetUtcNow());
        foreach (var channel in Channels(conn).Where(c => c.IsActive))
        {
            var sealedConfig = conn.ExecuteScalar<string>("SELECT config_enc FROM notification_channels WHERE id = @id", new { id = channel.Id })!;
            if (await Deliver(conn, channel, sealedConfig, message, [null], ct)) delivered++;
        }
        return delivered;
    }

    public static string FormatTime(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
