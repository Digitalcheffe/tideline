using Tideline.Api.Auth;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);
    public sealed record SetupRequest(string? Token, string? Password);
    public sealed record CodeRequest(string? Code, string? RecoveryCode);
    public sealed record PasskeyEnrollRequest(string? Label, Fido2NetLib.AuthenticatorAttestationRawResponse? Credential);
    public sealed record PasskeySignInRequest(Fido2NetLib.AuthenticatorAssertionRawResponse? Credential);

    private static IResult Json(string json) => Results.Content(json, "application/json");

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/login", (LoginRequest request, HttpContext http, AuthService service, TidelineOptions options, TimeProvider clock) =>
        {
            var result = service.Login(request.Email, request.Password, http.ClientIp(), http.Request.Headers.UserAgent.ToString());
            if (result.Outcome != LoginOutcome.Ok)
                return Results.Problem(title: "Could not sign in", detail: "Wrong email or password, or the account is locked for a few minutes.", statusCode: 401);

            SetCookie(http, options, clock, result.SessionToken!);
            return Results.Ok(new { mfaRequired = true, mfaEnrolled = result.User!.MfaEnrolledAt is not null, methods = service.Methods(result.User!) });
        });

        // a setup link: the person chooses a password, then sets up the authenticator
        auth.MapGet("/setup", (string? token, AuthService service) =>
        {
            var info = service.SetupInfo(token);
            return info is null
                ? Results.Problem(title: "Link not valid", detail: "This setup link has been used or has run out. Ask the owner for a new one.", statusCode: 410)
                : Results.Ok(new { email = info.Value.User.Email, displayName = info.Value.User.DisplayName, minPasswordLength = PasswordPolicy.MinLength });
        });

        auth.MapPost("/setup", (SetupRequest request, HttpContext http, AuthService service, TidelineOptions options, TimeProvider clock) =>
            Guard(() =>
            {
                var result = service.CompleteSetup(request.Token, request.Password, http.ClientIp(), http.Request.Headers.UserAgent.ToString());
                SetCookie(http, options, clock, result.SessionToken!);
                return Results.Ok(new { mfaRequired = true, mfaEnrolled = result.User!.MfaEnrolledAt is not null, methods = service.Methods(result.User!) });
            }));

        auth.MapGet("/me", (HttpContext http, Csrf csrf, AuthService service, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            var who = http.Principal()!;
            using var conn = factory.Open();
            var left = who.IsToken ? 0 : new MfaRepository(conn, clock).RecoveryCodesLeft(who.User.Id);
            return Results.Ok(new
            {
                user = new { id = who.User.Id, email = who.User.Email, displayName = who.User.DisplayName, role = who.User.Role.ToDb() },
                authDisabled = options.DevNoAuth,
                mfaPassed = who.IsFull,
                mfaEnrolled = who.User.MfaEnrolledAt is not null,
                methods = who.IsToken ? [] : service.Methods(who.User),
                sessionExpires = who.Session?.ExpiresAt,
                recoveryCodesLeft = left,
                csrf = who.Session is null || options.DevNoAuth ? null : csrf.Issue(who.Session),
            });
        });

        auth.MapGet("/csrf", (HttpContext http, Csrf csrf) => Results.Ok(new { token = csrf.Issue(http.Principal()!.Session!) }));

        auth.MapPost("/logout", (HttpContext http, AuthService service, TidelineOptions options) =>
        {
            var who = http.Principal()!;
            if (who.Session is not null) service.Logout(who.Session, http.ClientIp());
            http.Response.Cookies.Delete(options.CookieName, new CookieOptions { Path = "/", Secure = options.SecureCookie, HttpOnly = true, SameSite = SameSiteMode.Strict });
            return Results.Ok(new { signedOut = true });
        });

        // ---- enrolling the second step ----
        auth.MapPost("/enroll/totp/start", (HttpContext http, AuthService service) =>
            Guard(() =>
            {
                var enrollment = service.StartTotp(http.Principal()!.User);
                return Results.Ok(new { secret = enrollment.Secret, otpauthUri = enrollment.OtpAuthUri, qrSvg = enrollment.QrSvg });
            }));

        auth.MapPost("/enroll/totp/confirm", (CodeRequest request, HttpContext http, AuthService service, Csrf csrf, TidelineOptions options, TimeProvider clock) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                var done = service.ConfirmTotp(who.Session!, who.User, request.Code, http.ClientIp());
                SetCookie(http, options, clock, done.SessionToken);
                return Results.Ok(new { recoveryCodes = done.RecoveryCodes, recoveryCodesLeft = done.RecoveryCodesLeft });
            }));

        // ---- the second step when signing in ----
        auth.MapPost("/mfa/totp", (CodeRequest request, HttpContext http, AuthService service, TidelineOptions options, TimeProvider clock) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                var done = string.IsNullOrWhiteSpace(request.RecoveryCode)
                    ? service.VerifyTotp(who.Session!, who.User, request.Code, http.ClientIp())
                    : service.VerifyRecoveryCode(who.Session!, who.User, request.RecoveryCode, http.ClientIp());
                SetCookie(http, options, clock, done.SessionToken);
                return Results.Ok(new { signedIn = true, recoveryCodesLeft = done.RecoveryCodesLeft });
            }));

        // ---- passkeys (WebAuthn) ----
        // the browser asks for the challenge, its device signs it, and the answer comes back to be checked
        auth.MapPost("/enroll/passkey/options", (HttpContext http, AuthService service) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                return Json(service.StartPasskey(who.Session!, who.User).ToJson());
            }));

        auth.MapPost("/enroll/passkey/verify", (PasskeyEnrollRequest request, HttpContext http, AuthService service, TidelineOptions options, TimeProvider clock) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                if (request.Credential is null) throw new AuthException("The passkey was not sent.", 400);
                var done = service.ConfirmPasskey(who.Session!, who.User, request.Credential, request.Label, http.ClientIp());
                if (done.SessionToken is not null) SetCookie(http, options, clock, done.SessionToken);
                return Results.Ok(new { recoveryCodes = done.RecoveryCodes, recoveryCodesLeft = done.RecoveryCodesLeft });
            }));

        auth.MapPost("/mfa/passkey/options", (HttpContext http, AuthService service) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                return Json(service.StartPasskeySignIn(who.Session!, who.User).ToJson());
            }));

        auth.MapPost("/mfa/passkey/verify", (PasskeySignInRequest request, HttpContext http, AuthService service, TidelineOptions options, TimeProvider clock) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                if (request.Credential is null) throw new AuthException("The passkey was not sent.", 400);
                var done = service.VerifyPasskey(who.Session!, who.User, request.Credential, http.ClientIp());
                SetCookie(http, options, clock, done.SessionToken);
                return Results.Ok(new { signedIn = true, recoveryCodesLeft = done.RecoveryCodesLeft });
            }));

        auth.MapGet("/passkeys", (HttpContext http, AuthService service) => Results.Ok(service.Passkeys(http.Principal()!.User)));

        auth.MapDelete("/passkeys/{id:long}", (long id, HttpContext http, AuthService service) =>
            Guard(() =>
            {
                var who = http.Principal()!;
                service.RemovePasskey(who.Session!, who.User, id, http.ClientIp());
                return Results.Ok(new { removed = true });
            }));

        auth.MapPost("/recovery-codes", (CodeRequest request, HttpContext http, AuthService service) =>
            Guard(() => Results.Ok(new { recoveryCodes = service.RegenerateRecoveryCodes(http.Principal()!.User, request.Code, http.ClientIp()) })));

        // ---- your own sessions ----
        auth.MapGet("/sessions", (HttpContext http, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            var who = http.Principal()!;
            using var conn = factory.Open();
            var sessions = new SessionRepository(conn, clock).Active(who.User.Id, TimeSpan.FromHours(options.IdleHours));
            return Results.Ok(sessions.Select(s => new
            {
                s.Id, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.Ip, s.UserAgent,
                isCurrent = s.Id == who.Session?.Id,
            }));
        });

        auth.MapDelete("/sessions/{id:long}", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            var who = http.Principal()!;
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var sessions = new SessionRepository(conn, clock, tx);
            var target = sessions.Get(id);
            if (target is null || target.UserId != who.User.Id || target.RevokedAt is not null) return Results.Problem(title: "Not found", detail: "That is not one of your sessions.", statusCode: 404);
            sessions.Revoke(id);
            new AuditWriter(conn, clock, tx, who.ToActor(http.ClientIp())).Write("session.revoked", "session", id.ToString(), new { target.Ip, target.UserAgent }, null);
            tx.Commit();
            return Results.Ok(new { revoked = true });
        });

        auth.MapPost("/sessions/revoke-others", (HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            var who = http.Principal()!;
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var n = new SessionRepository(conn, clock, tx).RevokeAllForUser(who.User.Id, who.Session?.Id);
            new AuditWriter(conn, clock, tx, who.ToActor(http.ClientIp())).Write("session.revoked_others", "user", who.User.Id.ToString(), null, new { count = n });
            tx.Commit();
            return Results.Ok(new { revoked = n });
        });
    }

    private static void SetCookie(HttpContext http, TidelineOptions options, TimeProvider clock, string token) =>
        http.Response.Cookies.Append(options.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.SecureCookie,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(options.AbsoluteDays),
        });

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (AuthException e) { return Results.Problem(title: "Could not do that", detail: e.Message, statusCode: e.Status); }
    }
}
