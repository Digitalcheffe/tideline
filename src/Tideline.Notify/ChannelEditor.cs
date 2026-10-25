using System.Data;
using System.Text.Json;
using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Services;

namespace Tideline.Notify;

public sealed record ChannelInput(string? Kind, string? Name, JsonElement? Config = null, bool? IsActive = null);

public sealed record PrefsInput(IReadOnlyDictionary<string, string>? Modes);

/// <summary>
/// Channels and what is sent through them. The settings (a webhook address, a mail password) are sealed before they are stored and are
/// never written to the audit log or shown again: a change says only that they were replaced.
/// </summary>
public sealed class ChannelEditor(IDbConnection conn, TimeProvider clock, Actor? actor, Notifier notifier) : EditorBase(conn, clock, actor)
{
    private static object Describe(string kind, string name, bool active) => new { kind, name, isActive = active };

    private string SealConfig(string kind, JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } c) throw Bad("Enter the channel's settings.");
        var json = c.GetRawText();
        try
        {
            if (notifier.Check(kind, json) is { } problem) throw Bad(problem);
        }
        catch (JsonException)
        {
            throw Bad("The settings are not in the form this kind of channel needs.");
        }
        return notifier.Seal(json);
    }

    public long Create(ChannelInput c) => InTransaction((tx, audit) =>
    {
        var kind = (c.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("webhook" or "email")) throw Bad("A channel is a webhook or email.");
        var name = Name(c.Name, 100, "channel");
        var sealedConfig = SealConfig(kind, c.Config);

        Conn.Execute("INSERT INTO notification_channels (kind, name, config_enc, is_active) VALUES (@kind, @name, @sealedText, @active)",
            new { kind, name, sealedText = sealedConfig, active = (c.IsActive ?? true) ? 1 : 0 }, tx);
        var id = Conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        audit.Write("notify_channel.created", "notify_channel", id, null, Describe(kind, name, c.IsActive ?? true));
        return id;
    });

    public void Update(long id, ChannelInput c) => InTransaction((tx, audit) =>
    {
        var cur = Conn.QuerySingleOrDefault<(string Kind, string Name, bool Active)?>("SELECT kind, name, is_active FROM notification_channels WHERE id = @id", new { id }, tx) ?? throw Missing("channel");
        var name = c.Name is null ? cur.Name : Name(c.Name, 100, "channel");
        var active = c.IsActive ?? cur.Active;

        Conn.Execute("UPDATE notification_channels SET name = @name, is_active = @active WHERE id = @id", new { id, name, active = active ? 1 : 0 }, tx);
        if (c.Config is not null) Conn.Execute("UPDATE notification_channels SET config_enc = @sealedText WHERE id = @id", new { id, sealedText = SealConfig(cur.Kind, c.Config) }, tx);
        audit.Write("notify_channel.updated", "notify_channel", id, Describe(cur.Kind, cur.Name, cur.Active), new { kind = cur.Kind, name, isActive = active, settingsReplaced = c.Config is not null });
    });

    public void Delete(long id) => InTransaction((tx, audit) =>
    {
        var cur = Conn.QuerySingleOrDefault<(string Kind, string Name, bool Active)?>("SELECT kind, name, is_active FROM notification_channels WHERE id = @id", new { id }, tx) ?? throw Missing("channel");
        Conn.Execute("DELETE FROM notifications_sent WHERE channel_id = @id", new { id }, tx);
        Conn.Execute("DELETE FROM notification_channels WHERE id = @id", new { id }, tx);
        audit.Write("notify_channel.deleted", "notify_channel", id, Describe(cur.Kind, cur.Name, cur.Active), null);
    });

    public void SavePrefs(PrefsInput input) => InTransaction((tx, audit) =>
    {
        var modes = input.Modes ?? throw Bad("Send the modes to change.");
        foreach (var (kind, mode) in modes)
        {
            if (!FlagKinds.All.Contains(kind)) throw Bad($"'{kind}' is not a kind of flag.");
            if (mode is not ("immediate" or "summary" or "off")) throw Bad("A kind is sent at once, in the summary, or not at all.");
        }

        var before = Notifier.Modes(Conn).ToDictionary(kv => kv.Key, kv => kv.Value.ToString().ToLowerInvariant());
        foreach (var (kind, mode) in modes)
        {
            Conn.Execute("DELETE FROM notification_prefs WHERE flag_kind = @kind", new { kind }, tx);
            if (FlagKinds.DefaultMode(kind).ToString().ToLowerInvariant() != mode)
                Conn.Execute("INSERT INTO notification_prefs (flag_kind, mode) VALUES (@kind, @mode)", new { kind, mode }, tx);
        }
        var after = Notifier.Modes(Conn).ToDictionary(kv => kv.Key, kv => kv.Value.ToString().ToLowerInvariant());
        audit.Write("notify_prefs.updated", "notify_prefs", (string?)null, before.Where(kv => after[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            after.Where(kv => before[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value));
    });

    public void RecordSummarySent(string month) => InTransaction((tx, audit) =>
    {
        audit.Write("summary.sent", "summary", month, null, new { month });
    });
}
