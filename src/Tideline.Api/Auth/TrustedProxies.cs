using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Tideline.Api.Auth;

/// <summary>
/// Believing a reverse proxy about who the visitor really is (docs/09-security.md, "Behind a reverse proxy"). Without it every request seems to come
/// from the proxy: the sign-in limit is shared by everyone and the audit log shows one address. With it, only the addresses listed in
/// <c>Tideline__TrustedProxies</c> are believed, so a visitor cannot make up an address by sending the header themselves.
/// </summary>
public static class TrustedProxies
{
    public static bool IsSet(string? list) => !string.IsNullOrWhiteSpace(list);

    /// <summary>Reads "172.18.0.5, 10.0.0.0/8" into the proxy list. A mistake stops the app from starting, with the setting named, rather than quietly trusting nothing.</summary>
    public static void Configure(ForwardedHeadersOptions options, string? list)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1; // the one proxy we know; anything a visitor adds in front of it is not read
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (!System.Net.IPNetwork.TryParse(entry, out var network))
                    throw new InvalidOperationException($"Tideline__TrustedProxies has '{entry}', which is not an address range like 172.18.0.0/16.");
                options.KnownIPNetworks.Add(network);
            }
            else
            {
                if (!IPAddress.TryParse(entry, out var address))
                    throw new InvalidOperationException($"Tideline__TrustedProxies has '{entry}', which is not an address like 172.18.0.5.");
                options.KnownProxies.Add(address);
            }
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            throw new InvalidOperationException("Tideline__TrustedProxies is set but lists no addresses.");
    }
}
