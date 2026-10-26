using System.Data;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using QRCoder;
using Tideline.Core;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Api.Auth;

/// <summary>Encrypts what must not sit in the database in the clear (authenticator secrets). Keys live beside the database.</summary>
public sealed class SecretBox(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tideline.Totp.v1");

    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string sealedText) => _protector.Unprotect(sealedText);
}

/// <summary>Seals notification channel settings (a webhook address, mail passwords) with the same keys, for a different purpose.</summary>
public sealed class NotifySecrets(IDataProtectionProvider provider) : Tideline.Data.Security.ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tideline.Notify.v1");

    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string sealedText) => _protector.Unprotect(sealedText);
}

/// <summary>Anti-forgery for cookie sessions: a token only this session can have been given (docs/09-security.md, "Sessions").</summary>
public sealed class Csrf(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tideline.Csrf.v1");

    public string Issue(SessionRecord session) => _protector.Protect($"{session.Id}:{session.CreatedAt.ToUnixTimeSeconds()}");

    public bool IsValid(string? token, SessionRecord session)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return _protector.Unprotect(token) == $"{session.Id}:{session.CreatedAt.ToUnixTimeSeconds()}"; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }
}

public enum LoginOutcome { Ok, WrongDetails }

public sealed record LoginResult(LoginOutcome Outcome, string? SessionToken = null, UserRecord? User = null);

public sealed record TotpEnrollment(string Secret, string OtpAuthUri, string QrSvg);

public sealed record MfaSuccess(string SessionToken, IReadOnlyList<string>? RecoveryCodes = null, int RecoveryCodesLeft = 0);

/// <summary>Raised for things a person can fix; the message is written for them.</summary>
public sealed class AuthException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Signing in: password, then a second step that is not optional (docs/09-security.md). Every attempt, good or bad, is audited.
/// </summary>
public sealed class AuthService(SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options, SecretBox box)
{
    private static readonly PasswordHasher<UserRecord> Hasher = new();
    private static readonly string TimingHash = Hasher.HashPassword(null!, "never-matches-anything-real");

    public static string HashPassword(string password) => Hasher.HashPassword(null!, password);

    private TimeSpan Lock => TimeSpan.FromMinutes(options.LockoutMinutes);

    private DateTimeOffset SessionEnd => clock.GetUtcNow().AddDays(options.AbsoluteDays);

    // ---- password ------------------------------------------------------------------------------------------

    public LoginResult Login(string? email, string? password, string? ip, string? userAgent)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var users = new UserRepository(conn, clock, tx);
        var audit = new AuditWriter(conn, clock, tx, new Actor(Ip: ip));

        var user = string.IsNullOrWhiteSpace(email) ? null : users.ByEmail(email);
        var usable = user is { IsActive: true, HasPassword: true };

        // always do the same amount of hashing, so how long this takes does not say whether the email exists
        var verdict = Hasher.VerifyHashedPassword(user!, usable ? user!.PasswordHash : TimingHash, password ?? "");

        if (!usable)
        {
            audit.Write("login.failure", "login", null, null, new { email = email?.Trim().ToLowerInvariant(), reason = "unknown or unusable account" });
            tx.Commit();
            return new LoginResult(LoginOutcome.WrongDetails);
        }

        if (user!.LockedUntil > clock.GetUtcNow())
        {
            // while locked nothing is counted and nothing is tried
            audit.Write("login.refused_locked", "user", user.Id.ToString(), null, new { until = user.LockedUntil });
            tx.Commit();
            return new LoginResult(LoginOutcome.WrongDetails);
        }

        if (verdict == PasswordVerificationResult.Failed)
        {
            var (_, lockedUntil) = users.RecordFailedLogin(user.Id, options.MaxFailedLogins, Lock);
            audit.Write("login.failure", "user", user.Id.ToString(), null, new { reason = "wrong password" });
            if (lockedUntil is not null) audit.Write("login.locked", "user", user.Id.ToString(), null, new { until = lockedUntil });
            tx.Commit();
            return new LoginResult(LoginOutcome.WrongDetails);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded) users.SetPassword(user.Id, Hasher.HashPassword(user, password!));
        users.RecordLogin(user.Id);

        var token = Tokens.NewSessionToken();
        new SessionRepository(conn, clock, tx).Create(user.Id, Tokens.Hash(token), SessionEnd, ip, userAgent);
        audit.Write("login.success", "user", user.Id.ToString(), null, new { secondStep = "pending" });
        var signedIn = users.Get(user.Id);
        tx.Commit();
        return new LoginResult(LoginOutcome.Ok, token, signedIn);
    }

    // ---- first run ---------------------------------------------------------------------------------------------

    private static readonly object FirstRunLock = new();

    /// <summary>True only while the app has no users at all. The moment there is one, the first-run page is gone for good.</summary>
    public bool FirstRunOpen()
    {
        using var conn = factory.Open();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM users") == 0;
    }

    /// <summary>
    /// The first person to arrive in an empty app makes the owner account (docs/09-security.md, "First run"). They then go on to the same second
    /// step as everyone: nothing is open until the authenticator is set up. Only works while there are no users.
    /// </summary>
    public LoginResult FirstRun(string? email, string? displayName, string? password, string? ip, string? userAgent)
    {
        email = email?.Trim() ?? "";
        displayName = displayName?.Trim() ?? "";
        if (email.Length is 0 or > 254 || !email.Contains('@') || email.Contains(' ')) throw new AuthException("That does not look like an email address.", 422);
        if (displayName.Length is 0 or > 100) throw new AuthException("Give a name (100 characters at most).", 422);
        if (PasswordPolicy.Check(password, email, displayName) is { } problem) throw new AuthException(problem, 422);

        // two people arriving together cannot both become the owner: the check and the insert happen under one lock
        lock (FirstRunLock)
        {
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var users = new UserRepository(conn, clock, tx);
            var audit = new AuditWriter(conn, clock, tx, new Actor(Ip: ip));

            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM users", transaction: tx) > 0)
                throw new AuthException("This app is already set up. Sign in instead.", 410);

            var id = users.Create(email, displayName, Role.Owner);
            users.SetPassword(id, Hasher.HashPassword(users.Get(id)!, password!));
            var session = Tokens.NewSessionToken();
            new SessionRepository(conn, clock, tx).Create(id, Tokens.Hash(session), SessionEnd, ip, userAgent);
            audit.Write("user.created", "user", id.ToString(), null, new { email = UserRepository.NormalizeEmail(email), displayName, role = "owner", via = "first run" });
            audit.Write("user.password_set", "user", id.ToString(), null, new { via = "first run" });
            var created = users.Get(id);
            tx.Commit();
            return new LoginResult(LoginOutcome.Ok, session, created);
        }
    }

    // ---- setup link ------------------------------------------------------------------------------------------

    public (UserRecord User, DateTimeOffset ExpiresAt)? SetupInfo(string? token)
    {
        using var conn = factory.Open();
        var userId = new SetupLinks(conn, clock).Resolve(token);
        if (userId is null) return null;
        var user = new UserRepository(conn, clock).Get(userId.Value);
        return user is null || !user.IsActive ? null : (user, clock.GetUtcNow().Add(SetupLinks.Lifetime));
    }

    public LoginResult CompleteSetup(string? token, string? password, string? ip, string? userAgent)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var links = new SetupLinks(conn, clock, tx);
        var users = new UserRepository(conn, clock, tx);

        var userId = links.Resolve(token) ?? throw new AuthException("This setup link has been used or has run out. Ask the owner for a new one.", 410);
        var user = users.Get(userId);
        if (user is null || !user.IsActive) throw new AuthException("This setup link has been used or has run out. Ask the owner for a new one.", 410);

        var problem = PasswordPolicy.Check(password, user.Email, user.DisplayName);
        if (problem is not null) throw new AuthException(problem, 422);

        users.SetPassword(user.Id, Hasher.HashPassword(user, password!));
        links.Consume(token!);

        var session = Tokens.NewSessionToken();
        new SessionRepository(conn, clock, tx).Create(user.Id, Tokens.Hash(session), SessionEnd, ip, userAgent);
        new AuditWriter(conn, clock, tx, new Actor(user.Id, Ip: ip)).Write("user.password_set", "user", user.Id.ToString(), null, new { via = "setup link" });
        var ready = users.Get(user.Id);
        tx.Commit();
        return new LoginResult(LoginOutcome.Ok, session, ready);
    }

    // ---- second step: enrolment ---------------------------------------------------------------------------------

    public TotpEnrollment StartTotp(UserRecord user)
    {
        if (user.MfaEnrolledAt is not null)
            throw new AuthException("This account already has a second step. Ask the owner to reset it if you lost it.", 409);

        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var secret = Totp.NewSecret();
        var mfa = new MfaRepository(conn, clock, tx);
        mfa.DeleteAll(user.Id); // a half-finished earlier attempt is thrown away
        mfa.AddTotp(user.Id, "Authenticator app", box.Protect(secret));
        new AuditWriter(conn, clock, tx, new Actor(user.Id)).Write("mfa.enroll_started", "user", user.Id.ToString(), null, new { kind = "totp" });
        tx.Commit();

        var uri = Totp.OtpAuthUri("Tideline", user.Email, secret);
        return new TotpEnrollment(secret, uri, Qr(uri));
    }

    /// <summary>The first code proves the authenticator works. The account is then enrolled and signed in fully, with its recovery codes.</summary>
    public MfaSuccess ConfirmTotp(SessionRecord session, UserRecord user, string? code, string? ip)
    {
        if (user.MfaEnrolledAt is not null) throw new AuthException("This account already has a second step.", 409);

        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var mfa = new MfaRepository(conn, clock, tx);
        var method = mfa.Active(user.Id).FirstOrDefault(m => m.Kind == "totp") ?? throw new AuthException("Start again: there is no authenticator waiting to be confirmed.", 409);

        var step = Totp.Verify(box.Unprotect(method.TotpSecretEnc!), code ?? "", clock.GetUtcNow(), method.SignCount);
        if (step is null || !mfa.TryUseStep(method.Id, step.Value))
        {
            new AuditWriter(conn, clock, tx, new Actor(user.Id, Ip: ip)).Write("mfa.enroll_failed", "user", user.Id.ToString(), null, new { kind = "totp" });
            tx.Commit();
            throw new AuthException("That code did not match. Check the clock on your phone and try the next one.", 401);
        }

        var codes = RecoveryCodes.Generate();
        mfa.ReplaceRecoveryCodes(user.Id, codes.Select(RecoveryCodes.Hash));
        new UserRepository(conn, clock, tx).SetMfaEnrolled(user.Id, clock.GetUtcNow());

        var token = Tokens.NewSessionToken();
        new SessionRepository(conn, clock, tx).MarkMfaPassed(session.Id, Tokens.Hash(token));
        new AuditWriter(conn, clock, tx, new Actor(user.Id, Ip: ip)).Write("mfa.enrolled", "user", user.Id.ToString(), null, new { kind = "totp", recoveryCodes = codes.Count });
        tx.Commit();
        return new MfaSuccess(token, codes, codes.Count);
    }

    // ---- second step: signing in ---------------------------------------------------------------------------------

    public MfaSuccess VerifyTotp(SessionRecord session, UserRecord user, string? code, string? ip) =>
        Verify(session, user, ip, "totp", (conn, tx, mfa) =>
        {
            foreach (var m in mfa.Active(user.Id).Where(m => m.Kind == "totp" && m.TotpSecretEnc is not null))
            {
                var step = Totp.Verify(box.Unprotect(m.TotpSecretEnc!), code ?? "", clock.GetUtcNow(), m.SignCount);
                if (step is not null && mfa.TryUseStep(m.Id, step.Value)) return true;
            }
            return false;
        });

    public MfaSuccess VerifyRecoveryCode(SessionRecord session, UserRecord user, string? code, string? ip) =>
        Verify(session, user, ip, "recovery", (conn, tx, mfa) =>
            !string.IsNullOrWhiteSpace(code) && mfa.TryUseRecoveryCode(user.Id, RecoveryCodes.Hash(code)));

    private MfaSuccess Verify(SessionRecord session, UserRecord user, string? ip, string kind, Func<IDbConnection, IDbTransaction, MfaRepository, bool> check)
    {
        if (user.MfaEnrolledAt is null) throw new AuthException("Set up your authenticator first.", 409);

        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var mfa = new MfaRepository(conn, clock, tx);
        var users = new UserRepository(conn, clock, tx);
        var audit = new AuditWriter(conn, clock, tx, new Actor(user.Id, Ip: ip));

        var fresh = users.Get(user.Id)!;
        if (fresh.LockedUntil > clock.GetUtcNow())
        {
            audit.Write("mfa.refused_locked", "user", user.Id.ToString(), null, new { kind });
            tx.Commit();
            throw new AuthException("Too many wrong tries. Wait a few minutes and sign in again.", 429);
        }

        if (!check(conn, tx, mfa))
        {
            // wrong codes count towards the same lock as wrong passwords
            var (_, lockedUntil) = users.RecordFailedLogin(user.Id, options.MaxFailedLogins, Lock);
            audit.Write("mfa.failure", "user", user.Id.ToString(), null, new { kind });
            if (lockedUntil is not null)
            {
                audit.Write("login.locked", "user", user.Id.ToString(), null, new { until = lockedUntil, during = "second step" });
                new SessionRepository(conn, clock, tx).Revoke(session.Id);
            }
            tx.Commit();
            throw new AuthException("That code did not match.", 401);
        }

        users.RecordLogin(user.Id);
        var token = Tokens.NewSessionToken();
        new SessionRepository(conn, clock, tx).MarkMfaPassed(session.Id, Tokens.Hash(token));
        audit.Write(kind == "recovery" ? "mfa.recovery_code_used" : "mfa.success", "user", user.Id.ToString(), null, new { kind });
        var left = mfa.RecoveryCodesLeft(user.Id);
        tx.Commit();
        return new MfaSuccess(token, null, left);
    }

    /// <summary>Ten new recovery codes. Needs a current authenticator code, since it replaces the old ones.</summary>
    public IReadOnlyList<string> RegenerateRecoveryCodes(UserRecord user, string? totpCode, string? ip)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var mfa = new MfaRepository(conn, clock, tx);

        var ok = false;
        foreach (var m in mfa.Active(user.Id).Where(m => m.Kind == "totp" && m.TotpSecretEnc is not null))
        {
            var step = Totp.Verify(box.Unprotect(m.TotpSecretEnc!), totpCode ?? "", clock.GetUtcNow(), m.SignCount);
            if (step is not null && mfa.TryUseStep(m.Id, step.Value)) { ok = true; break; }
        }
        if (!ok) throw new AuthException("Type a current code from your authenticator to do that.", 401);

        var codes = RecoveryCodes.Generate();
        mfa.ReplaceRecoveryCodes(user.Id, codes.Select(RecoveryCodes.Hash));
        new AuditWriter(conn, clock, tx, new Actor(user.Id, Ip: ip)).Write("mfa.recovery_codes_regenerated", "user", user.Id.ToString(), null, new { count = codes.Count });
        tx.Commit();
        return codes;
    }

    // ---- signing out ------------------------------------------------------------------------------------------

    public void Logout(SessionRecord session, string? ip)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        new SessionRepository(conn, clock, tx).Revoke(session.Id);
        new AuditWriter(conn, clock, tx, new Actor(session.UserId, Ip: ip)).Write("logout", "user", session.UserId.ToString(), null, null);
        tx.Commit();
    }

    private static string Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4);
    }
}
