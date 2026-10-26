using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Tideline.Data.Security;

namespace Tideline.Notify;

/// <summary>One message, whatever it travels by. The webhook sends it as JSON; mail turns it into a subject and a body.</summary>
public sealed record NotificationMessage(string Kind, string Severity, string Title, string Detail, string? Link, long? FlagId, DateTimeOffset At);

public sealed record WebhookConfig(string Url, string? Secret = null);

public sealed record EmailConfig(string Host, int Port, bool UseSsl, string From, string To, string? Username = null, string? Password = null);

/// <summary>Sends to one kind of channel. Throws with a short plain reason if it cannot; the notifier records that and tries again later.</summary>
public interface IChannelSender
{
    string Kind { get; }

    /// <summary>Checks a channel's settings without sending anything. Returns a plain-words problem, or null when they look usable.</summary>
    string? Check(string configJson);

    Task SendAsync(string configJson, NotificationMessage message, CancellationToken ct);
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>A POST of the JSON in docs/ARCHITECTURE.md, normally to an automation that posts on to chat.</summary>
public sealed class WebhookSender(HttpClient http) : IChannelSender
{
    public string Kind => "webhook";

    public string? Check(string configJson)
    {
        var c = JsonSerializer.Deserialize<WebhookConfig>(configJson, Json.Options);
        if (c is null || !Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "The webhook address must be a full http or https address.";
        return null;
    }

    public async Task SendAsync(string configJson, NotificationMessage m, CancellationToken ct)
    {
        var c = JsonSerializer.Deserialize<WebhookConfig>(configJson, Json.Options) ?? throw new InvalidOperationException("The webhook has no settings.");
        using var request = new HttpRequestMessage(HttpMethod.Post, c.Url)
        {
            Content = JsonContent.Create(new { kind = m.Kind, severity = m.Severity, title = m.Title, detail = m.Detail, link = m.Link, flagId = m.FlagId, at = m.At }, options: Json.Options),
        };
        if (!string.IsNullOrEmpty(c.Secret)) request.Headers.Add("X-Tideline-Secret", c.Secret);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The webhook answered {(int)response.StatusCode}.");
    }
}

/// <summary>Mail by SMTP. The password is only ever read from the sealed settings, never logged.</summary>
public sealed class SmtpSender : IChannelSender
{
    public string Kind => "email";

    public string? Check(string configJson)
    {
        var c = JsonSerializer.Deserialize<EmailConfig>(configJson, Json.Options);
        if (c is null || string.IsNullOrWhiteSpace(c.Host)) return "Enter the mail server.";
        if (c.Port is < 1 or > 65535) return "The mail port must be between 1 and 65535.";
        if (!c.From.Contains('@') || !c.To.Contains('@')) return "Enter the from and to addresses.";
        return null;
    }

    public async Task SendAsync(string configJson, NotificationMessage m, CancellationToken ct)
    {
        var c = JsonSerializer.Deserialize<EmailConfig>(configJson, Json.Options) ?? throw new InvalidOperationException("The mail channel has no settings.");
        using var client = new SmtpClient(c.Host, c.Port) { EnableSsl = c.UseSsl, Timeout = 20_000 };
        if (!string.IsNullOrEmpty(c.Username)) client.Credentials = new NetworkCredential(c.Username, c.Password);

        using var mail = new MailMessage(c.From, c.To, m.Title, string.Join("\n\n", new[] { m.Detail, m.Link }.Where(s => !string.IsNullOrWhiteSpace(s))));
        await client.SendMailAsync(mail, ct);
    }
}
