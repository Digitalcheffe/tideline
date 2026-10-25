using System.Globalization;
using System.Text;
using Tideline.Core;

namespace Tideline.Api;

public sealed class TidelineOptions
{
    public string Timezone { get; set; } = "America/New_York";
    public string? InboxPath { get; set; }
    public int InboxIntervalSeconds { get; set; } = 300;

    /// <summary>Where backups are written. Empty means a "backups" folder beside the database.</summary>
    public string? BackupPath { get; set; }

    /// <summary>The address people use, put in setup links. In development that is the web app's port.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5173";

    /// <summary>Where the keys that encrypt authenticator secrets live. Empty means a "keys" folder beside the database.</summary>
    public string? DataProtectionKeys { get; set; }

    /// <summary>The cookie is Secure and named with the __Host- prefix. Browsers accept that on localhost, so this stays on.</summary>
    public bool SecureCookie { get; set; } = true;

    public int IdleHours { get; set; } = 12;
    public int AbsoluteDays { get; set; } = 14;
    public int AuthRequestsPerMinute { get; set; } = 10;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// For testing on your own computer only: everyone is treated as the owner with the second step passed. It does nothing
    /// unless the environment is Development and the app listens on this computer alone, and the app refuses to start otherwise.
    /// This is the one exception to "no setting turns MFA off" (docs/09-security.md, "Development switch").
    /// </summary>
    public bool DevNoAuth { get; set; }

    public string CookieName => SecureCookie ? "__Host-tideline" : "tideline";
}

/// <summary>"Today" and month boundaries in the household's time zone (docs/04-domain-rules.md).</summary>
public sealed class Clock(TidelineOptions options, TimeProvider time)
{
    private readonly TimeZoneInfo _zone = Find(options.Timezone);

    private static TimeZoneInfo Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _zone).DateTime);

    /// <summary>The time on the household's clock right now.</summary>
    public DateTime LocalNow => TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime;
}

/// <summary>Builds the query string of a drill-down link (docs/07-api.md, "Drill-down contract").</summary>
public static class Drill
{
    public static string Of(params (string Key, object? Value)[] parts)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parts)
        {
            if (value is null) continue;
            var text = value switch
            {
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LineType t => t.ToDb(),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(text));
        }
        return sb.ToString();
    }

    public static string Range(DateOnly from, DateOnly to, params (string Key, object? Value)[] more) =>
        Of([("from", from), ("to", to), .. more]);
}

/// <summary>A number on a screen together with the query that opens the lines behind it.</summary>
public sealed record Figure(long Cents, string? Drill);

public static class Months
{
    public static DateOnly First(string month) => DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Last(DateOnly first) => new(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
    public static string Key(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateOnly first) =>
        DateOnly.TryParseExact((text ?? "") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first);
}
