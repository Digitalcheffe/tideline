using System.Globalization;
using Tideline.Api.Auth;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>
/// Owner-only: the people, every session, API tokens and the logs (docs/ARCHITECTURE.md). The access table has already
/// checked the role by the time any of this runs. Each change is audited in the same transaction.
/// </summary>
public static class AdminEndpoints
{
    public sealed record NewUserRequest(string? Email, string? DisplayName, string? Role);
    public sealed record UserPatch(string? DisplayName, string? Role, bool? IsActive);
    public sealed record NewTokenRequest(string? Name, string? Scope, int? ExpiresInDays);

    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        MapUsers(api.MapGroup("/users"));
        MapSessions(api.MapGroup("/sessions"));
        MapTokens(api.MapGroup("/tokens"));
        MapLogs(api);
    }

    private static object Describe(UserRecord u, int sessions) => new
    {
        u.Id, u.Email, u.DisplayName, role = u.Role.ToDb(), u.IsActive, mfaEnrolled = u.MfaEnrolledAt is not null, hasPassword = u.HasPassword,
        u.LastLoginAt, u.LockedUntil, activeSessions = sessions,
    };

    private static string SetupUrl(TidelineOptions options, string token) => $"{options.PublicUrl.TrimEnd('/')}/setup?token={token}";

    private static IResult Problem(int status, string title, string detail) => Results.Problem(title: title, detail: detail, statusCode: status);

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("", (SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            using var conn = factory.Open();
            var sessions = new SessionRepository(conn, clock).Active(null, TimeSpan.FromHours(options.IdleHours)).ToLookup(s => s.UserId);
            return Results.Ok(new UserRepository(conn, clock).All().Select(u => Describe(u, sessions[u.Id].Count())));
        });

        users.MapPost("", (NewUserRequest request, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            var email = request.Email?.Trim() ?? "";
            var name = request.DisplayName?.Trim() ?? "";
            if (email.Length is 0 or > 254 || !email.Contains('@') || email.Contains(' ')) return Problem(400, "Check the request", "That does not look like an email address.");
            if (name.Length is 0 or > 100) return Problem(400, "Check the request", "Give a name (100 characters at most).");
            if (!Enum.TryParse<Role>(request.Role ?? "member", ignoreCase: true, out var role)) return Problem(400, "Check the request", "The role must be owner, member or viewer.");

            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var repo = new UserRepository(conn, clock, tx);
            if (repo.ByEmail(email) is not null) return Problem(409, "Already there", $"{email} is already a user. Make them a new setup link instead.");

            var id = repo.Create(email, name, role);
            var audit = new AuditWriter(conn, clock, tx, http.Actor());
            audit.Write("user.created", "user", id.ToString(), null, new { email = UserRepository.NormalizeEmail(email), displayName = name, role = role.ToDb() });
            var link = new SetupLinks(conn, clock, tx).Create(id);
            audit.Write("user.setup_link_created", "user", id.ToString(), null, new { link.ExpiresAt });
            var created = repo.Get(id)!;
            tx.Commit();
            return Results.Json(new { user = Describe(created, 0), setupUrl = SetupUrl(options, link.Token), expiresAt = link.ExpiresAt }, statusCode: 201);
        });

        users.MapPatch("/{id:long}", (long id, UserPatch patch, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var repo = new UserRepository(conn, clock, tx);
            var user = repo.Get(id);
            if (user is null) return Problem(404, "Not found", "There is no such person.");

            Role? role = null;
            if (patch.Role is not null)
            {
                if (!Enum.TryParse<Role>(patch.Role, ignoreCase: true, out var r)) return Problem(400, "Check the request", "The role must be owner, member or viewer.");
                role = r;
            }
            var name = patch.DisplayName?.Trim();
            if (name is { Length: 0 } or { Length: > 100 }) return Problem(400, "Check the request", "Give a name (100 characters at most).");

            var losesOwner = user.Role == Role.Owner && user.IsActive && ((role is not null && role != Role.Owner) || patch.IsActive == false);
            if (losesOwner && repo.ActiveOwners() <= 1) return Problem(422, "Cannot do that", "That would leave no owner. Make someone else an owner first.");

            var before = new { displayName = user.DisplayName, role = user.Role.ToDb(), user.IsActive };
            if (name is not null) repo.SetDisplayName(id, name);
            if (role is not null) repo.SetRole(id, role.Value);
            if (patch.IsActive is { } active)
            {
                repo.SetActive(id, active);
                if (!active) new SessionRepository(conn, clock, tx).RevokeAllForUser(id);
            }

            var after = repo.Get(id)!;
            new AuditWriter(conn, clock, tx, http.Actor()).Write("user.updated", "user", id.ToString(), before, new { displayName = after.DisplayName, role = after.Role.ToDb(), after.IsActive });
            tx.Commit();
            return Results.Ok(Describe(after, 0));
        });

        users.MapPost("/{id:long}/setup-link", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
            WithUser(id, factory, clock, (conn, tx, user) =>
            {
                var link = new SetupLinks(conn, clock, tx).Create(user.Id);
                new AuditWriter(conn, clock, tx, http.Actor()).Write("user.setup_link_created", "user", user.Id.ToString(), null, new { link.ExpiresAt });
                return Results.Ok(new { setupUrl = SetupUrl(options, link.Token), expiresAt = link.ExpiresAt });
            }));

        // forces the person to enrol again: they lost the phone and the recovery codes
        users.MapPost("/{id:long}/reset-mfa", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
            WithUser(id, factory, clock, (conn, tx, user) =>
            {
                new MfaRepository(conn, clock, tx).DeleteAll(user.Id);
                new UserRepository(conn, clock, tx).SetMfaEnrolled(user.Id, null);
                var ended = new SessionRepository(conn, clock, tx).RevokeAllForUser(user.Id);
                var audit = new AuditWriter(conn, clock, tx, http.Actor());
                audit.Write("mfa.reset", "user", user.Id.ToString(), new { enrolled = user.MfaEnrolledAt is not null }, new { enrolled = false, sessionsEnded = ended });
                var link = new SetupLinks(conn, clock, tx).Create(user.Id);
                audit.Write("user.setup_link_created", "user", user.Id.ToString(), null, new { link.ExpiresAt });
                return Results.Ok(new { setupUrl = SetupUrl(options, link.Token), expiresAt = link.ExpiresAt, sessionsEnded = ended });
            }));

        users.MapPost("/{id:long}/sessions/revoke-all", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
            WithUser(id, factory, clock, (conn, tx, user) =>
            {
                var n = new SessionRepository(conn, clock, tx).RevokeAllForUser(user.Id);
                new AuditWriter(conn, clock, tx, http.Actor()).Write("session.revoked_all", "user", user.Id.ToString(), null, new { count = n });
                return Results.Ok(new { revoked = n });
            }));
    }

    private static IResult WithUser(long id, SqliteConnectionFactory factory, TimeProvider clock, Func<System.Data.IDbConnection, System.Data.IDbTransaction, UserRecord, IResult> action)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var user = new UserRepository(conn, clock, tx).Get(id);
        if (user is null) return Problem(404, "Not found", "There is no such person.");
        var result = action(conn, tx, user);
        tx.Commit();
        return result;
    }

    private static void MapSessions(RouteGroupBuilder sessions)
    {
        sessions.MapGet("", (HttpContext http, SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            var current = http.Principal()?.Session?.Id;
            using var conn = factory.Open();
            return Results.Ok(new SessionRepository(conn, clock).Active(null, TimeSpan.FromHours(options.IdleHours)).Select(s => new
            {
                s.Id, s.UserId, s.UserName, s.UserEmail, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.Ip, s.UserAgent,
                secondStepPassed = s.MfaPassedAt is not null, isCurrent = s.Id == current,
            }));
        });

        sessions.MapDelete("/{id:long}", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var repo = new SessionRepository(conn, clock, tx);
            var target = repo.Get(id);
            if (target is null || target.RevokedAt is not null) return Problem(404, "Not found", "That session is not active.");
            repo.Revoke(id);
            new AuditWriter(conn, clock, tx, http.Actor()).Write("session.revoked", "session", id.ToString(), new { target.UserId, target.Ip, target.UserAgent }, null);
            tx.Commit();
            return Results.Ok(new { revoked = true });
        });
    }

    private static object Describe(ApiTokenRecord t) => new
    {
        t.Id, t.Name, t.Prefix, t.Scope, createdBy = t.CreatedByName, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.RevokedAt,
        isActive = t.RevokedAt is null && (t.ExpiresAt is null || t.ExpiresAt > DateTimeOffset.UtcNow),
    };

    private static void MapTokens(RouteGroupBuilder tokens)
    {
        tokens.MapGet("", (SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            using var conn = factory.Open();
            return Results.Ok(new ApiTokenRepository(conn, clock).All().Select(Describe));
        });

        // the token itself is in this one response and never again: only its hash is kept
        tokens.MapPost("", (NewTokenRequest request, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) return Problem(400, "Check the request", "Give the token a name that says what uses it (100 characters at most).");
            var scope = (request.Scope ?? "read").Trim().ToLowerInvariant();
            if (scope is not ("read" or "write")) return Problem(400, "Check the request", "The scope must be read or write.");
            if (request.ExpiresInDays is <= 0 or > 3650) return Problem(400, "Check the request", "Expiry must be between 1 day and 10 years, or left empty for none.");

            var secret = Tokens.NewApiToken();
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var repo = new ApiTokenRepository(conn, clock, tx);
            var expires = request.ExpiresInDays is { } d ? clock.GetUtcNow().AddDays(d) : (DateTimeOffset?)null;
            var id = repo.Create(name, Tokens.Hash(secret), Tokens.Prefix(secret), scope, http.Principal()!.User.Id, expires);
            new AuditWriter(conn, clock, tx, http.Actor()).Write("token.created", "api_token", id.ToString(), null, new { name, scope, expires });
            var created = repo.Get(id)!;
            tx.Commit();
            return Results.Json(new { token = secret, record = Describe(created) }, statusCode: 201);
        });

        tokens.MapDelete("/{id:long}", (long id, HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            var repo = new ApiTokenRepository(conn, clock, tx);
            var token = repo.Get(id);
            if (token is null) return Problem(404, "Not found", "There is no such token.");
            repo.Revoke(id);
            new AuditWriter(conn, clock, tx, http.Actor()).Write("token.revoked", "api_token", id.ToString(), new { token.Name, token.Scope }, null);
            tx.Commit();
            return Results.Ok(new { revoked = true });
        });
    }

    private static void MapLogs(RouteGroupBuilder api)
    {
        api.MapGet("/audit", (HttpRequest request, SqliteConnectionFactory factory) =>
        {
            var q = request.Query;
            DateOnly? Day(string key) => DateOnly.TryParseExact(q[key].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var filter = new AuditFilter(
                Blank(q["entity"]), Blank(q["entityId"]), Blank(q["action"]),
                long.TryParse(q["user"], out var user) ? user : null, Day("from"), Day("to"));

            using var conn = factory.Open();
            var page = new LogReader(conn).Audit(filter, Number(q["page"], 1), Number(q["pageSize"], 100));
            return Results.Ok(new
            {
                items = page.Items.Select(a => new { a.Id, a.At, a.UserId, a.UserName, a.ApiTokenId, a.ApiTokenName, a.Action, a.Entity, a.EntityId, a.BeforeJson, a.AfterJson, a.Ip }),
                page = page.PageNumber, pageSize = page.PageSize, total = page.Total,
            });
        });

        api.MapGet("/system-events", (HttpRequest request, SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            var page = new LogReader(conn).SystemEvents(Blank(request.Query["level"]), Blank(request.Query["source"]), Number(request.Query["page"], 1), Number(request.Query["pageSize"], 100));
            return Results.Ok(new { items = page.Items, page = page.PageNumber, pageSize = page.PageSize, total = page.Total });
        });
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static int Number(string? s, int fallback) => int.TryParse(s, out var n) ? n : fallback;
}
