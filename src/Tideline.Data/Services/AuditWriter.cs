using System.Data;
using System.Text.Json;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>
/// Everything a person changes is in the audit log, with before and after, written in the same database
/// transaction as the change (docs/09-security.md, "Audit"). The log is append-only.
/// </summary>
public sealed class AuditWriter(IDbConnection conn, TimeProvider clock, IDbTransaction tx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="userId">Null until sign-in exists (Phase 3), which records the change as made by the system.</param>
    public void Write(string action, string entity, long entityId, object? before, object? after, long? userId = null)
    {
        conn.Execute("""
            INSERT INTO audit_log (at, user_id, action, entity, entity_id, before_json, after_json)
            VALUES (@at, @userId, @action, @entity, @entityId, @before, @after)
            """, new
        {
            at = clock.GetUtcNow(),
            userId,
            action,
            entity,
            entityId = entityId.ToString(),
            before = before is null ? null : JsonSerializer.Serialize(before, Json),
            after = after is null ? null : JsonSerializer.Serialize(after, Json),
        }, tx);
    }
}
