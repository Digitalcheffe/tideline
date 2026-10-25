using System.Net;
using System.Globalization;
using System.Text;
using Tideline.Core;

namespace Tideline.Api;

public sealed class TidelineOptions
{
    public string Timezone { get; set; } = "America/New_York";
    public string? InboxPath { get; set; }
    public int InboxIntervalSeconds { get; set; } = 300;

    /// <summary>Until sign-in with MFA exists (Phase 3) the API only answers this computer. Setting this turns that off.</summary>
    public bool AllowRemote { get; set; }
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

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);
}

/// <summary>
/// There is no sign-in until Phase 3, so the API refuses anything that is not this computer. A finished ledger
/// must never be readable by the rest of the network.
/// </summary>
public sealed class LocalOnlyMiddleware(RequestDelegate next, TidelineOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (options.AllowRemote || ip is null || IPAddress.IsLoopback(ip)) return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "Local only",
            status = 403,
            detail = "Sign-in is not built yet (Phase 3), so Tideline only answers the computer it runs on.",
        });
    }
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
