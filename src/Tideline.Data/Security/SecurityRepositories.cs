using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Tideline.Core.Security;
using Tideline.Data.Repositories;

namespace Tideline.Data.Security;

/// <summary>Random secrets and their hashes. Only the hash of a session or API token is ever stored.</summary>
public static class Tokens
{
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>256 random bits, URL-safe: the value in the session cookie.</summary>
    public static string NewSessionToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string NewSetupToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary><c>tdl_</c> plus 40 random base62 characters (docs/09-security.md).</summary>
    public static string NewApiToken()
    {
        var chars = new char[40];
        for (var i = 0; i < chars.Length; i++) chars[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        return "tdl_" + new string(chars);
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The first characters, shown in the UI so a token can be recognised without being revealed.</summary>
    public static string Prefix(string apiToken) => apiToken[..Math.Min(12, apiToken.Length)];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class UserRecord
{
    public long Id { get; init; }
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public Role Role { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset? MfaEnrolledAt { get; init; }
    public int FailedLogins { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }

    /// <summary>A person invited but not yet set up has no usable password.</summary>
    public bool HasPassword => PasswordHash.Length > 1;
}

public sealed class UserRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public const string NoPassword = "!";

    private const string Select = """
        SELECT id AS Id, email AS Email, display_name AS DisplayName, password_hash AS PasswordHash, role AS Role, is_active AS IsActive,
               mfa_enrolled_at AS MfaEnrolledAt, failed_logins AS FailedLogins, locked_until AS LockedUntil, created_at AS CreatedAt,
               last_login_at AS LastLoginAt
        FROM users
        """;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public long Create(string email, string displayName, Role role)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("""
            INSERT INTO users (email, display_name, password_hash, role, is_active, failed_logins, created_at)
            VALUES (@email, @name, @hash, @role, 1, 0, @now)
            """, new { email = NormalizeEmail(email), name = displayName.Trim(), hash = NoPassword, role = role.ToDb(), now = clock.GetUtcNow() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public UserRecord? Get(long id)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<UserRecord>($"{Select} WHERE id = @id", new { id }, tx);
    }

    public UserRecord? ByEmail(string email)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<UserRecord>($"{Select} WHERE email = @email", new { email = NormalizeEmail(email) }, tx);
    }

    public IReadOnlyList<UserRecord> All()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<UserRecord>($"{Select} ORDER BY display_name, id", transaction: tx).ToList();
    }

    public long ActiveOwners() => conn.ExecuteScalar<long>("SELECT COUNT(*) FROM users WHERE role = 'owner' AND is_active = 1", transaction: tx);

    public void SetPassword(long id, string hash) =>
        conn.Execute("UPDATE users SET password_hash = @hash, failed_logins = 0, locked_until = NULL WHERE id = @id", new { id, hash }, tx);

    public void SetRole(long id, Role role) => conn.Execute("UPDATE users SET role = @role WHERE id = @id", new { id, role = role.ToDb() }, tx);

    public void SetActive(long id, bool active) => conn.Execute("UPDATE users SET is_active = @active WHERE id = @id", new { id, active = active ? 1 : 0 }, tx);

    public void SetDisplayName(long id, string name) => conn.Execute("UPDATE users SET display_name = @name WHERE id = @id", new { id, name = name.Trim() }, tx);

    public void SetMfaEnrolled(long id, DateTimeOffset? at)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE users SET mfa_enrolled_at = @at WHERE id = @id", new { id, at }, tx);
    }

    /// <summary>Counts a wrong password. At <paramref name="maxFailures"/> the account locks for <paramref name="lockFor"/>.</summary>
    public (int Failures, DateTimeOffset? LockedUntil) RecordFailedLogin(long id, int maxFailures, TimeSpan lockFor)
    {
        TypeHandlers.EnsureRegistered();
        var failures = conn.ExecuteScalar<int>("SELECT failed_logins FROM users WHERE id = @id", new { id }, tx) + 1;
        DateTimeOffset? until = null;
        if (failures >= maxFailures)
        {
            until = clock.GetUtcNow().Add(lockFor);
            failures = 0; // the lock is the penalty; counting starts again afterwards
        }
        conn.Execute("UPDATE users SET failed_logins = @failures, locked_until = @until WHERE id = @id", new { id, failures, until }, tx);
        return (failures, until);
    }

    public void RecordLogin(long id)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE users SET failed_logins = 0, locked_until = NULL, last_login_at = @now WHERE id = @id", new { id, now = clock.GetUtcNow() }, tx);
    }
}

public sealed class SessionRecord
{
    public long Id { get; init; }
    public string TokenHash { get; init; } = "";
    public long UserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? MfaPassedAt { get; init; }
    public string? Ip { get; init; }
    public string? UserAgent { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? UserName { get; init; }
    public string? UserEmail { get; init; }

    public bool IsFullyAuthenticated => MfaPassedAt is not null;
}

public sealed class SessionRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    private const string Select = """
        SELECT s.id AS Id, s.token_hash AS TokenHash, s.user_id AS UserId, s.created_at AS CreatedAt, s.last_seen_at AS LastSeenAt,
               s.expires_at AS ExpiresAt, s.mfa_passed_at AS MfaPassedAt, s.ip AS Ip, s.user_agent AS UserAgent, s.revoked_at AS RevokedAt,
               u.display_name AS UserName, u.email AS UserEmail
        FROM sessions s JOIN users u ON u.id = s.user_id
        """;

    public long Create(long userId, string tokenHash, DateTimeOffset expiresAt, string? ip, string? userAgent, bool mfaPassed = false)
    {
        TypeHandlers.EnsureRegistered();
        var now = clock.GetUtcNow();
        conn.Execute("""
            INSERT INTO sessions (token_hash, user_id, created_at, last_seen_at, expires_at, mfa_passed_at, ip, user_agent)
            VALUES (@tokenHash, @userId, @now, @now, @expiresAt, @passed, @ip, @ua)
            """, new { tokenHash, userId, now, expiresAt, passed = mfaPassed ? now : (DateTimeOffset?)null, ip, ua = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public SessionRecord? ByTokenHash(string tokenHash)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<SessionRecord>($"{Select} WHERE s.token_hash = @tokenHash", new { tokenHash }, tx);
    }

    public SessionRecord? Get(long id)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<SessionRecord>($"{Select} WHERE s.id = @id", new { id }, tx);
    }

    /// <summary>The second factor passed: the session becomes a full one, with a new token (so an old one cannot be reused).</summary>
    public void MarkMfaPassed(long id, string newTokenHash)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE sessions SET mfa_passed_at = @now, last_seen_at = @now, token_hash = @newTokenHash WHERE id = @id",
            new { id, newTokenHash, now = clock.GetUtcNow() }, tx);
    }

    public void Touch(long id)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE sessions SET last_seen_at = @now WHERE id = @id", new { id, now = clock.GetUtcNow() }, tx);
    }

    public void Revoke(long id)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE sessions SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL", new { id, now = clock.GetUtcNow() }, tx);
    }

    public int RevokeAllForUser(long userId, long? exceptSessionId = null)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Execute("UPDATE sessions SET revoked_at = @now WHERE user_id = @userId AND revoked_at IS NULL AND (@except IS NULL OR id <> @except)",
            new { userId, except = exceptSessionId, now = clock.GetUtcNow() }, tx);
    }

    /// <summary>Sessions that can still be used: not revoked, not past their end, and not idle too long.</summary>
    public IReadOnlyList<SessionRecord> Active(long? userId, TimeSpan idleTimeout)
    {
        TypeHandlers.EnsureRegistered();
        var now = clock.GetUtcNow();
        return conn.Query<SessionRecord>($"""
            {Select}
            WHERE s.revoked_at IS NULL AND s.expires_at > @now AND s.last_seen_at > @idleAfter AND (@userId IS NULL OR s.user_id = @userId)
            ORDER BY s.last_seen_at DESC
            """, new { now, idleAfter = now - idleTimeout, userId }, tx).ToList();
    }
}

public sealed class MfaMethod
{
    public long Id { get; init; }
    public long UserId { get; init; }
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string? TotpSecretEnc { get; init; }
    public string? CredentialId { get; init; }
    public string? PublicKey { get; init; }
    public long SignCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class MfaRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public long AddTotp(long userId, string label, string secretEnc)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("INSERT INTO user_mfa (user_id, kind, label, totp_secret_enc, sign_count, created_at) VALUES (@userId, 'totp', @label, @secretEnc, 0, @now)",
            new { userId, label, secretEnc, now = clock.GetUtcNow() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public IReadOnlyList<MfaMethod> Active(long userId)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<MfaMethod>("""
            SELECT id AS Id, user_id AS UserId, kind AS Kind, label AS Label, totp_secret_enc AS TotpSecretEnc,
                   credential_id AS CredentialId, public_key AS PublicKey, sign_count AS SignCount,
                   created_at AS CreatedAt, last_used_at AS LastUsedAt, revoked_at AS RevokedAt
            FROM user_mfa WHERE user_id = @userId AND revoked_at IS NULL ORDER BY id
            """, new { userId }, tx).ToList();
    }

    /// <summary>A passkey: the credential id and the public key (never a secret; the private key stays on the person's device).</summary>
    public long AddPasskey(long userId, string label, string credentialId, string publicKey, long signCount)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("""
            INSERT INTO user_mfa (user_id, kind, label, credential_id, public_key, sign_count, created_at)
            VALUES (@userId, 'passkey', @label, @credentialId, @publicKey, @signCount, @now)
            """, new { userId, label, credentialId, publicKey, signCount, now = clock.GetUtcNow() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    /// <summary>Remember the counter the device reported, so a cloned passkey that reports an older one is refused next time.</summary>
    public void RecordPasskeyUse(long methodId, long signCount) =>
        conn.Execute("UPDATE user_mfa SET sign_count = @signCount, last_used_at = @now WHERE id = @methodId",
            new { methodId, signCount, now = clock.GetUtcNow() }, tx);

    /// <summary>Takes a method away from a person. Returns false if it was not theirs or was already gone.</summary>
    public bool Revoke(long userId, long methodId) =>
        conn.Execute("UPDATE user_mfa SET revoked_at = @now WHERE id = @methodId AND user_id = @userId AND revoked_at IS NULL",
            new { methodId, userId, now = clock.GetUtcNow() }, tx) == 1;

    /// <summary>An enrolment that was started but never confirmed is thrown away when a new one starts.</summary>
    public void DeleteAll(long userId)
    {
        conn.Execute("DELETE FROM user_mfa WHERE user_id = @userId", new { userId }, tx);
        conn.Execute("DELETE FROM recovery_codes WHERE user_id = @userId", new { userId }, tx);
    }

    /// <summary>Record the step a code was accepted for. False if that step (or a later one) was already used, which is a replay.</summary>
    public bool TryUseStep(long methodId, long step)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Execute("UPDATE user_mfa SET sign_count = @step, last_used_at = @now WHERE id = @methodId AND sign_count < @step",
            new { methodId, step, now = clock.GetUtcNow() }, tx) == 1;
    }

    public void ReplaceRecoveryCodes(long userId, IEnumerable<string> hashes)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("DELETE FROM recovery_codes WHERE user_id = @userId", new { userId }, tx);
        foreach (var hash in hashes)
            conn.Execute("INSERT INTO recovery_codes (user_id, code_hash, created_at) VALUES (@userId, @hash, @now)", new { userId, hash, now = clock.GetUtcNow() }, tx);
    }

    /// <summary>Spend a recovery code. True once; the same code never works twice.</summary>
    public bool TryUseRecoveryCode(long userId, string hash)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Execute("UPDATE recovery_codes SET used_at = @now WHERE user_id = @userId AND code_hash = @hash AND used_at IS NULL",
            new { userId, hash, now = clock.GetUtcNow() }, tx) == 1;
    }

    public int RecoveryCodesLeft(long userId) =>
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM recovery_codes WHERE user_id = @userId AND used_at IS NULL", new { userId }, tx);
}

public sealed class ApiTokenRecord
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string TokenHash { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Scope { get; init; } = "read";
    public long CreatedBy { get; init; }
    public string? CreatedByName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class ApiTokenRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    private const string Select = """
        SELECT t.id AS Id, t.name AS Name, t.token_hash AS TokenHash, t.prefix AS Prefix, t.scope AS Scope, t.created_by AS CreatedBy,
               u.display_name AS CreatedByName, t.created_at AS CreatedAt, t.last_used_at AS LastUsedAt, t.expires_at AS ExpiresAt, t.revoked_at AS RevokedAt
        FROM api_tokens t JOIN users u ON u.id = t.created_by
        """;

    public long Create(string name, string tokenHash, string prefix, string scope, long createdBy, DateTimeOffset? expiresAt)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("""
            INSERT INTO api_tokens (name, token_hash, prefix, scope, created_by, created_at, expires_at)
            VALUES (@name, @tokenHash, @prefix, @scope, @createdBy, @now, @expiresAt)
            """, new { name, tokenHash, prefix, scope, createdBy, now = clock.GetUtcNow(), expiresAt }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public ApiTokenRecord? ByHash(string tokenHash)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<ApiTokenRecord>($"{Select} WHERE t.token_hash = @tokenHash", new { tokenHash }, tx);
    }

    public ApiTokenRecord? Get(long id)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<ApiTokenRecord>($"{Select} WHERE t.id = @id", new { id }, tx);
    }

    public IReadOnlyList<ApiTokenRecord> All()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<ApiTokenRecord>($"{Select} ORDER BY t.created_at DESC, t.id DESC", transaction: tx).ToList();
    }

    public void Revoke(long id)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE api_tokens SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL", new { id, now = clock.GetUtcNow() }, tx);
    }

    public void Touch(long id)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE api_tokens SET last_used_at = @now WHERE id = @id", new { id, now = clock.GetUtcNow() }, tx);
    }
}

/// <summary>The small key and value table (<c>settings</c>). Used for setup links and the few app settings.</summary>
public sealed class SettingsRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public string? Get(string key) => conn.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key }, tx);

    public void Set(string key, string value)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("DELETE FROM settings WHERE key = @key", new { key }, tx);
        conn.Execute("INSERT INTO settings (key, value, updated_at) VALUES (@key, @value, @now)", new { key, value, now = clock.GetUtcNow() }, tx);
    }

    public void Delete(string key) => conn.Execute("DELETE FROM settings WHERE key = @key", new { key }, tx);

    public IReadOnlyList<(string Key, string Value)> WithPrefix(string prefix) =>
        conn.Query<(string Key, string Value)>("SELECT key, value FROM settings WHERE key LIKE @like ORDER BY key", new { like = prefix + "%" }, tx).ToList();
}
