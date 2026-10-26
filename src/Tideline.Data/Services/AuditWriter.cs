using System.Data;
using System.Text.Json;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>Who did something: a signed-in person, an API token, or the system (an import, the evaluator).</summary>
public sealed record Actor(long? UserId = null, long? ApiTokenId = null, string? Ip = null)
{
    public static readonly Actor System = new();
}

/// <summary>
/// Everything a person changes is in the audit log, with before and after, written in the same database
/// transaction as the change (docs/ARCHITECTURE.md). The log is append-only.
/// </summary>
public sealed class AuditWriter(IDbConnection conn, TimeProvider clock, IDbTransaction tx, Actor? actor = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Write(string action, string entity, long entityId, object? before, object? after) =>
        Write(action, entity, entityId.ToString(), before, after);

    public void Write(string action, string entity, string? entityId, object? before, object? after)
    {
        TypeHandlers.EnsureRegistered();
        var who = actor ?? Actor.System;
        conn.Execute("""
            INSERT INTO audit_log (at, user_id, api_token_id, action, entity, entity_id, before_json, after_json, ip)
            VALUES (@at, @userId, @tokenId, @action, @entity, @entityId, @before, @after, @ip)
            """, new
        {
            at = clock.GetUtcNow(),
            userId = who.UserId,
            tokenId = who.ApiTokenId,
            action,
            entity,
            entityId,
            before = before is null ? null : JsonSerializer.Serialize(before, Json),
            after = after is null ? null : JsonSerializer.Serialize(after, Json),
            ip = who.Ip,
        }, tx);
    }
}
