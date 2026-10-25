using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Api.Auth;

/// <summary>Who is calling: a person with a session, or an API token acting for the person who made it.</summary>
public sealed record Principal(UserRecord User, SessionRecord? Session, ApiTokenRecord? Token)
{
    public bool IsToken => Token is not null;

    /// <summary>A session only counts once the second step has passed. A token needs no second step: it was made by someone who had.</summary>
    public bool IsFull => IsToken || Session!.IsFullyAuthenticated;

    /// <summary>A read token can read; a write token can do what a member can, but never more than the person who made it.</summary>
    public Role Role => IsToken ? (Token!.Scope == "write" ? (Role)Math.Min((int)User.Role, (int)Role.Member) : Role.Viewer) : User.Role;

    public Actor ToActor(string? ip) => new(User.Id, Token?.Id, ip);
}

public static class HttpContextAuth
{
    private const string Key = "tideline.principal";

    public static Principal? Principal(this HttpContext context) => context.Items.TryGetValue(Key, out var p) ? p as Principal : null;

    public static void SetPrincipal(this HttpContext context, Principal principal) => context.Items[Key] = principal;

    public static string? ClientIp(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    public static Actor Actor(this HttpContext context) => context.Principal()?.ToActor(context.ClientIp()) ?? new Actor(Ip: context.ClientIp());
}

/// <summary>
/// The one table that decides who may call what. Anything not listed here needs a fully signed-in person (second step
/// passed) and, for changes, the member role. Deny by default, so a new endpoint is protected before anyone thinks about it.
/// </summary>
public static class AccessPolicy
{
    private const string Api = "/api/v1";

    /// <summary>Open to anyone: signing in and following a setup link.</summary>
    public static bool IsPublic(string method, string path) =>
        (Is(method, "POST") && Same(path, "/auth/login")) ||
        (Same(path, "/auth/setup") && (Is(method, "GET") || Is(method, "POST")));

    /// <summary>What a session may call before the second step has passed.</summary>
    public static bool IsAllowedBeforeSecondStep(string path) =>
        StartsWith(path, "/auth/mfa") || StartsWith(path, "/auth/enroll") || Same(path, "/auth/logout") || Same(path, "/auth/me") || Same(path, "/auth/csrf");

    /// <summary>People who may read and change these: owners only.</summary>
    public static readonly string[] OwnerOnlyPrefixes =
        ["/users", "/tokens", "/sessions", "/audit", "/system-events", "/settings", "/backups", "/export", "/notify", "/import-batches", "/move-patterns"];

    public static bool IsOwnerOnly(string method, string path) =>
        OwnerOnlyPrefixes.Any(p => StartsWith(path, p)) || (!IsSafe(method) && StartsWith(path, "/accounts"));

    /// <summary>API tokens are for reading the numbers and, with write scope, for editing lines. Never for people, secrets or logs.</summary>
    public static bool IsAllowedForTokens(string method, string path) =>
        !StartsWith(path, "/auth") && !OwnerOnlyPrefixes.Any(p => StartsWith(path, p)) && !(!IsSafe(method) && StartsWith(path, "/accounts"));

    public static Role RequiredRole(string method, string path) =>
        IsOwnerOnly(method, path) ? Role.Owner : IsSafe(method) ? Role.Viewer : Role.Member;

    public static bool IsSafe(string method) => Is(method, "GET") || Is(method, "HEAD") || Is(method, "OPTIONS");

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Relative(string path) => path.StartsWith(Api, StringComparison.OrdinalIgnoreCase) ? path[Api.Length..].TrimEnd('/') : path;

    private static bool Same(string path, string route) => string.Equals(Relative(path), route, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string path, string prefix)
    {
        var p = Relative(path);
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (p.Length == prefix.Length || p[prefix.Length] == '/');
    }
}

public static class Problems
{
    public static Task Write(HttpContext context, int status, string title, string detail, string? type = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type = type ?? "about:blank", title, status, detail });
    }
}

/// <summary>Works out who is calling, from the session cookie or a bearer API token. Does not decide what they may do.</summary>
public sealed class AuthMiddleware(RequestDelegate next, TidelineOptions options)
{
    public async Task InvokeAsync(HttpContext context, SqliteConnectionFactory factory, TimeProvider clock)
    {
        if (options.DevNoAuth && context.Request.Path.StartsWithSegments("/api/v1"))
        {
            context.SetPrincipal(DevAuth.Principal(factory, clock));
        }
        else if (context.Request.Path.StartsWithSegments("/api/v1"))
        {
            var principal = Resolve(context, factory, clock);
            if (principal is not null) context.SetPrincipal(principal);
        }
        await next(context);
    }

    private Principal? Resolve(HttpContext context, SqliteConnectionFactory factory, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        using var conn = factory.Open();

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = new ApiTokenRepository(conn, clock);
            var token = tokens.ByHash(Tokens.Hash(header["Bearer ".Length..].Trim()));
            if (token is null || token.RevokedAt is not null || (token.ExpiresAt is not null && token.ExpiresAt <= now)) return null;

            var owner = new UserRepository(conn, clock).Get(token.CreatedBy);
            if (owner is null || !owner.IsActive) return null;

            if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1)) tokens.Touch(token.Id);
            return new Principal(owner, null, token);
        }

        var cookie = context.Request.Cookies[options.CookieName];
        if (string.IsNullOrEmpty(cookie)) return null;

        var sessions = new SessionRepository(conn, clock);
        var session = sessions.ByTokenHash(Tokens.Hash(cookie));
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now || now - session.LastSeenAt > TimeSpan.FromHours(options.IdleHours)) return null;

        var user = new UserRepository(conn, clock).Get(session.UserId);
        if (user is null || !user.IsActive) return null;

        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1)) sessions.Touch(session.Id);
        return new Principal(user, session, null);
    }
}

/// <summary>Applies <see cref="AccessPolicy"/>: signed in, second step passed, anti-forgery on changes, and the right role.</summary>
public sealed class AccessMiddleware(RequestDelegate next, TidelineOptions options)
{
    public Task InvokeAsync(HttpContext context, Csrf csrf)
    {
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/api/v1")) return next(context);

        // the development switch: everyone is the owner, but only people at this computer
        if (options.DevNoAuth)
        {
            var ip = context.Connection.RemoteIpAddress;
            return ip is not null && !System.Net.IPAddress.IsLoopback(ip)
                ? Problems.Write(context, 403, "Local only", "Sign-in is switched off for testing, so only this computer may connect.", "forbidden")
                : next(context);
        }

        var method = context.Request.Method;
        if (AccessPolicy.IsPublic(method, path)) return next(context);

        var who = context.Principal();
        if (who is null) return Problems.Write(context, 401, "Sign in", "Sign in to continue.", "not_signed_in");

        if (who.IsToken)
        {
            if (!AccessPolicy.IsAllowedForTokens(method, path)) return Problems.Write(context, 403, "Not for tokens", "API tokens cannot do that.", "token_not_allowed");
            if (!AccessPolicy.IsSafe(method) && who.Token!.Scope != "write")
                return Problems.Write(context, 403, "Read-only token", "This token can only read. Make one with write access to change things.", "token_read_only");
        }
        else
        {
            if (!who.IsFull && !AccessPolicy.IsAllowedBeforeSecondStep(path))
                return Problems.Write(context, 403, "Second step needed", "Finish signing in with your authenticator first.", "mfa_required");

            if (!AccessPolicy.IsSafe(method) && !csrf.IsValid(context.Request.Headers["X-CSRF-Token"].ToString(), who.Session!))
                return Problems.Write(context, 403, "Check failed", "The page is out of date. Reload it and try again.", "csrf");
        }

        if (who.IsFull && who.Role < AccessPolicy.RequiredRole(method, path))
            return Problems.Write(context, 403, "No access", "Your role does not allow that.", "forbidden");

        return next(context);
    }
}

/// <summary>Headers from docs/09-security.md. The API sets them on everything it serves, including the web app.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "same-origin";
            h["Cache-Control"] = context.Request.Path.StartsWithSegments("/api") ? "no-store" : h.CacheControl.Count > 0 ? h.CacheControl : "no-cache";
            if (context.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// The development switch (Tideline:DevNoAuth). Not a setting for real use: it refuses to run outside Development or when the
/// app could be reached from another computer, and the web app shows a banner while it is on.
/// </summary>
public static class DevAuth
{
    public const string Email = "dev-owner@localhost";

    /// <summary>Throws unless it is safe to switch sign-in off. Called once, before the app starts listening.</summary>
    public static void RequireSafe(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Tideline:DevNoAuth switches sign-in off, so it only works when ASPNETCORE_ENVIRONMENT is Development. Remove it, or run in Development.");

        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urls)) return; // the default is localhost only

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = url.Replace("+", "0.0.0.0").Replace("*", "0.0.0.0");
            if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || !(uri.IsLoopback || uri.Host == "localhost"))
                throw new InvalidOperationException($"Tideline:DevNoAuth switches sign-in off, so the app may only listen on this computer (localhost), not on {url}.");
        }
    }

    /// <summary>Someone to act as: the first active owner, or a made-up one if the database has none yet.</summary>
    public static void EnsureOwner(SqliteConnectionFactory factory, TimeProvider clock)
    {
        using var conn = factory.Open();
        var users = new UserRepository(conn, clock);
        if (users.All().Any(u => u.Role == Role.Owner && u.IsActive)) return;
        if (users.ByEmail(Email) is null) users.Create(Email, "Dev owner (sign-in off)", Role.Owner);
    }

    public static Principal Principal(SqliteConnectionFactory factory, TimeProvider clock)
    {
        using var conn = factory.Open();
        var owner = new UserRepository(conn, clock).All().First(u => u.Role == Role.Owner && u.IsActive);
        var now = clock.GetUtcNow();
        var session = new SessionRecord { Id = 0, UserId = owner.Id, CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(1), MfaPassedAt = now };
        return new Principal(owner, session, null);
    }
}
