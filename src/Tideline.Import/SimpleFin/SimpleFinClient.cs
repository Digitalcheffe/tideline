using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tideline.Import.SimpleFin;

/// <summary>A problem with SimpleFIN, in words a person can act on. Never carries the access address.</summary>
public sealed class SimpleFinException(string message) : Exception(message);

public sealed record SfTransaction(string Id, DateTimeOffset Posted, long AmountCents, string Description, bool Pending, string RawJson);

public sealed record SfAccount(string Id, string Name, string Organization, string Currency, long BalanceCents, DateTimeOffset? BalanceDate, IReadOnlyList<SfTransaction> Transactions);

public sealed record SfResponse(IReadOnlyList<SfAccount> Accounts, IReadOnlyList<string> Errors);

/// <summary>
/// SimpleFIN Bridge (docs/05-import.md, "SimpleFIN"). A setup token is claimed once for an access address; after that each request is the address
/// plus /accounts with the window wanted. The credentials in the address go in a header, never in logs or messages.
/// </summary>
public sealed class SimpleFinClient(HttpClient http)
{
    /// <summary>The most a single request may cover, and how much the next one overlaps the last, so a late posting is not missed.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(5);

    /// <summary>Turns the setup token into the access address. A token works once.</summary>
    public async Task<string> ClaimAsync(string setupToken, CancellationToken ct = default)
    {
        string claimUrl;
        try
        {
            var cleaned = new string(setupToken.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
            cleaned = cleaned.PadRight(cleaned.Length + (4 - cleaned.Length % 4) % 4, '=');
            claimUrl = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
        }
        catch (FormatException)
        {
            throw new SimpleFinException("That does not look like a SimpleFIN setup token. Copy the whole token from SimpleFIN Bridge.");
        }
        if (!Uri.TryCreate(claimUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new SimpleFinException("That does not look like a SimpleFIN setup token. Copy the whole token from SimpleFIN Bridge.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new SimpleFinException("That setup token does not point to a secure address, so it was not used.");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(uri, new StringContent(""), ct);
        }
        catch (HttpRequestException)
        {
            throw new SimpleFinException("Could not reach SimpleFIN. Check the internet connection and try again.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new SimpleFinException("This token was already claimed (or never existed). If you did not claim it yourself, someone else may have: disable it at SimpleFIN and make a new one.");
            if (!response.IsSuccessStatusCode) throw new SimpleFinException($"SimpleFIN answered {(int)response.StatusCode} when the token was claimed.");

            var access = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (!Uri.TryCreate(access, UriKind.Absolute, out var a) || a.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(a.UserInfo))
                throw new SimpleFinException("SimpleFIN answered with something that is not an access address.");
            return access;
        }
    }

    public async Task<SfResponse> GetAccountsAsync(string accessUrl, DateTimeOffset? start, DateTimeOffset? end, bool balancesOnly, CancellationToken ct = default)
    {
        var access = new Uri(accessUrl);
        var query = new StringBuilder("version=2");
        if (balancesOnly) query.Append("&balances-only=1");
        if (start is { } s) query.Append("&start-date=").Append(s.ToUnixTimeSeconds());
        if (end is { } e) query.Append("&end-date=").Append(e.ToUnixTimeSeconds());

        var plain = new UriBuilder(access) { UserName = "", Password = "" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{plain.GetLeftPart(UriPartial.Path).TrimEnd('/')}/accounts?{query}");
        var creds = Uri.UnescapeDataString(access.UserInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(creds)));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new SimpleFinException("Could not reach SimpleFIN. It will be tried again at the next sync.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
                throw new SimpleFinException("SimpleFIN says payment is needed. Check the SimpleFIN Bridge subscription.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SimpleFinException("SimpleFIN no longer accepts this connection (access revoked, or the address is wrong). Make a new setup token and connect again. If you did not revoke it yourself, disable it at SimpleFIN.");
            if (!response.IsSuccessStatusCode) throw new SimpleFinException($"SimpleFIN answered {(int)response.StatusCode}.");
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    public static SfResponse Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { throw new SimpleFinException("SimpleFIN answered with something that is not data."); }

        var errors = new List<string>();
        if (root.TryGetProperty("errlist", out var errlist) && errlist.ValueKind == JsonValueKind.Array)
            foreach (var e in errlist.EnumerateArray())
                errors.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : e.GetRawText());
        else if (root.TryGetProperty("errors", out var plain) && plain.ValueKind == JsonValueKind.Array)
            errors.AddRange(plain.EnumerateArray().Select(e => e.GetString() ?? ""));

        var connections = new Dictionary<string, string>();
        if (root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
            foreach (var c in conns.EnumerateArray()) connections[Text(c, "conn_id")] = Text(c, "name");

        var accounts = new List<SfAccount>();
        if (root.TryGetProperty("accounts", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
            {
                var txs = new List<SfTransaction>();
                if (a.TryGetProperty("transactions", out var tl) && tl.ValueKind == JsonValueKind.Array)
                    foreach (var t in tl.EnumerateArray())
                    {
                        // a pending line may have no posting time yet (posted is 0)
                        var posted = Unix(t, "posted");
                        txs.Add(new SfTransaction(Text(t, "id"), posted ?? DateTimeOffset.UnixEpoch, Cents(Text(t, "amount")), Text(t, "description").Trim(),
                            posted is null || (t.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True), t.GetRawText()));
                    }
                accounts.Add(new SfAccount(Text(a, "id"), Text(a, "name"),
                    connections.GetValueOrDefault(Text(a, "conn_id")) ?? (a.TryGetProperty("org", out var org) ? Text(org, "name") : ""), Text(a, "currency"),
                    Cents(Text(a, "balance")), Unix(a, "balance-date"), txs));
            }
        return new SfResponse(accounts, errors.Where(e => e.Length > 0).ToList());
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static DateTimeOffset? Unix(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>"-12.34" to -1234. Positive is into the account, as in the ledger, so there is no flipping.</summary>
    public static long Cents(string amount) =>
        decimal.TryParse(amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero)
            : throw new SimpleFinException($"SimpleFIN sent an amount that is not a number: {amount}");

    /// <summary>The windows to ask for between two instants: none longer than 90 days, each starting 5 days before the last one ended.</summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Windows(DateTimeOffset start, DateTimeOffset end)
    {
        var windows = new List<(DateTimeOffset, DateTimeOffset)>();
        var from = start;
        while (true)
        {
            var to = from + MaxWindow < end ? from + MaxWindow : end;
            windows.Add((from, to));
            if (to >= end) break;
            from = to - Overlap;
        }
        return windows;
    }
}
