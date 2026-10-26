using System.Data;
using System.Text.Json;

namespace Tideline.Data.Security;

/// <summary>
/// One-time links that let a new person choose a password and set up their authenticator (docs/09-security.md). They last
/// 15 minutes and work once; making another cancels the old one. Only a hash of the link is stored, in the settings table.
/// </summary>
public sealed class SetupLinks(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public const string Prefix = "setup_link:";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private sealed record Stored(long UserId, DateTimeOffset ExpiresAt);

    private SettingsRepository Settings => new(conn, clock, tx);

    private static string KeyFor(string token) => Prefix + Tokens.Hash(token);

    /// <summary>A new link for the person. Any earlier link for them stops working.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Create(long userId)
    {
        foreach (var (key, value) in Settings.WithPrefix(Prefix))
            if (JsonSerializer.Deserialize<Stored>(value)?.UserId == userId) Settings.Delete(key);

        var token = Tokens.NewSetupToken();
        var expires = clock.GetUtcNow().Add(Lifetime);
        Settings.Set(KeyFor(token), JsonSerializer.Serialize(new Stored(userId, expires)));
        return (token, expires);
    }

    /// <returns>Who the link is for, or null if it is unknown, used or past its time.</returns>
    public long? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var raw = Settings.Get(KeyFor(token));
        if (raw is null) return null;
        var stored = JsonSerializer.Deserialize<Stored>(raw);
        return stored is not null && stored.ExpiresAt > clock.GetUtcNow() ? stored.UserId : null;
    }

    public void Consume(string token) => Settings.Delete(KeyFor(token));
}
