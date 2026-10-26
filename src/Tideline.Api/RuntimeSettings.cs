using System.Data;
using System.Globalization;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Api;

/// <summary>One option that can be changed in the app.</summary>
public sealed record AppSettingInfo(string Key, string Label, string Help, string Kind, int Min, int Max, string Unit);

/// <summary>
/// Options whose value comes from the compose file (or environment) and can be overridden in the app. The compose value is the default;
/// a value saved in the app wins over it until it is reset. Everything here is read each time it is used, so a change applies at once with no
/// restart. Owner only. Things that are decided at start-up (the listening address, trusted proxies, where keys live) stay in compose.
/// </summary>
public sealed class RuntimeSettings(TidelineOptions live, SqliteConnectionFactory factory, TimeProvider clock)
{
    public const string Prefix = "app.";

    /// <summary>What compose or the environment said, taken before anything saved in the app is applied.</summary>
    private readonly TidelineOptions _compose = new()
    {
        InboxPath = live.InboxPath,
        InboxIntervalSeconds = live.InboxIntervalSeconds,
        IdleHours = live.IdleHours,
        AbsoluteDays = live.AbsoluteDays,
        AuthRequestsPerMinute = live.AuthRequestsPerMinute,
        MaxFailedLogins = live.MaxFailedLogins,
        LockoutMinutes = live.LockoutMinutes,
    };

    private sealed record Def(AppSettingInfo Info, Func<TidelineOptions, string> Read, Action<TidelineOptions, string> Write);

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static Def Number(string key, string label, string help, int min, int max, string unit, Func<TidelineOptions, int> read, Action<TidelineOptions, int> write) =>
        new(new AppSettingInfo(key, label, help, "number", min, max, unit), o => N(read(o)), (o, v) => write(o, int.Parse(v, CultureInfo.InvariantCulture)));

    private static readonly Def[] Defs =
    [
        Number("idle_hours", "Sign out after being idle", "Someone who has not used the app for this long has to sign in again.", 1, 168, "hours", o => o.IdleHours, (o, v) => o.IdleHours = v),
        Number("absolute_days", "Sign out after, whatever they do", "Even someone who keeps using the app signs in again after this long.", 1, 90, "days", o => o.AbsoluteDays, (o, v) => o.AbsoluteDays = v),
        Number("max_failed_logins", "Wrong tries before an account is locked", "Wrong passwords or codes in a row. A right one starts the count again.", 3, 20, "tries", o => o.MaxFailedLogins, (o, v) => o.MaxFailedLogins = v),
        Number("lockout_minutes", "How long a lock lasts", "A locked account cannot sign in at all for this long. Someone with access to the server can lift it sooner.", 1, 1440, "minutes", o => o.LockoutMinutes, (o, v) => o.LockoutMinutes = v),
        Number("auth_requests_per_minute", "Sign-in attempts allowed per minute", "Counted per address, for sign-in, codes and setup links. More than this is turned away for the rest of the minute.", 3, 10_000, "a minute", o => o.AuthRequestsPerMinute, (o, v) => o.AuthRequestsPerMinute = v),
        new(new AppSettingInfo("inbox_path", "Inbox folder", "A folder, as the app sees it, that is looked at for CSV exports. Leave empty to turn the inbox off.", "text", 0, 500, ""),
            o => o.InboxPath ?? "", (o, v) => o.InboxPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim()),
        Number("inbox_interval_seconds", "How often the inbox is looked at", "Files still being copied are left until the next time.", 10, 86_400, "seconds", o => o.InboxIntervalSeconds, (o, v) => o.InboxIntervalSeconds = v),
    ];

    public static IReadOnlyList<AppSettingInfo> Known => Defs.Select(d => d.Info).ToList();

    private static Def Find(string key) =>
        Defs.FirstOrDefault(d => d.Info.Key == key) ?? throw new EditException(EditError.Invalid, $"'{key}' is not a setting that can be changed here.");

    /// <summary>Checks a value the way the screen would, and returns it ready to store. Throws a plain-words error.</summary>
    internal static string Normalize(string key, string? value)
    {
        var d = Find(key);
        var text = (value ?? "").Trim();
        if (d.Info.Kind == "text")
        {
            if (text.Length > d.Info.Max) throw new EditException(EditError.Invalid, $"{d.Info.Label}: that is too long ({d.Info.Max} characters at most).");
            if (text.Any(char.IsControl)) throw new EditException(EditError.Invalid, $"{d.Info.Label}: that has characters a folder cannot have.");
            if (text.Length > 0 && !Path.IsPathRooted(text)) throw new EditException(EditError.Invalid, $"{d.Info.Label}: give the whole path, starting from the top (such as /inbox).");
            return text;
        }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new EditException(EditError.Invalid, $"{d.Info.Label}: type a whole number.");
        if (n < d.Info.Min || n > d.Info.Max)
            throw new EditException(EditError.Invalid, $"{d.Info.Label}: use a number from {d.Info.Min} to {d.Info.Max}.");
        return N(n);
    }

    /// <summary>Puts the saved values over the compose ones, in the live options everything reads. Run at start and after every change.</summary>
    public void Apply(IDbConnection? connection = null)
    {
        TypeHandlers.EnsureRegistered();
        var owned = connection is null;
        var conn = connection ?? factory.Open();
        try
        {
            var stored = new SettingsRepository(conn, clock);
            foreach (var d in Defs)
            {
                var raw = stored.Get(Prefix + d.Info.Key);
                string? use = null;
                if (raw is not null)
                {
                    try { use = Normalize(d.Info.Key, raw); }
                    catch (EditException) { /* a bad saved value (edited by hand) is ignored: the compose value stays */ }
                }
                d.Write(live, use ?? d.Read(_compose));
            }
        }
        finally
        {
            if (owned) conn.Dispose();
        }
    }

    public object Describe(IDbConnection conn)
    {
        var stored = new SettingsRepository(conn, clock);
        return Defs.Select(d => new
        {
            d.Info.Key, d.Info.Label, d.Info.Help, d.Info.Kind, d.Info.Min, d.Info.Max, d.Info.Unit,
            effective = d.Read(live),
            compose = d.Read(_compose),
            overridden = stored.Get(Prefix + d.Info.Key) is not null,
        }).ToList();
    }

    internal string Current(string key) => Find(key).Read(live);

    internal string ComposeValue(string key) => Find(key).Read(_compose);
}

/// <summary>Saving or resetting the options above, audited.</summary>
public sealed class AppSettingsEditor(IDbConnection conn, TimeProvider clock, Actor? actor, RuntimeSettings settings) : EditorBase(conn, clock, actor)
{
    public void Save(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> reset)
    {
        if (values.Count == 0 && reset.Count == 0) throw Bad("Say what to change.");

        // check everything before writing anything
        var set = values.ToDictionary(v => v.Key, v => RuntimeSettings.Normalize(v.Key, v.Value));
        foreach (var key in reset) settings.Current(key);   // refuses a name that is not a setting

        InTransaction((tx, audit) =>
        {
            var store = new SettingsRepository(Conn, Clock, tx);
            var before = new Dictionary<string, string>();
            var after = new Dictionary<string, string>();
            foreach (var (key, value) in set)
            {
                before[key] = settings.Current(key);
                store.Set(RuntimeSettings.Prefix + key, value);
                after[key] = value;
            }
            foreach (var key in reset.Where(k => !set.ContainsKey(k)))
            {
                before[key] = settings.Current(key);
                store.Delete(RuntimeSettings.Prefix + key);
                after[key] = settings.ComposeValue(key);
            }
            audit.Write("settings.app_updated", "setting", "app", before, after);
        });
        settings.Apply(Conn);
    }
}
