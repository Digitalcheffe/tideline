using System.Data;
using System.Text;
using Dapper;

namespace Tideline.Data.Security;

public sealed record AuditFilter(
    string? Entity = null,
    string? EntityId = null,
    string? Action = null,
    long? UserId = null,
    DateOnly? From = null,
    DateOnly? To = null);

public sealed class AuditRow
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public long? UserId { get; init; }
    public string? UserName { get; init; }
    public long? ApiTokenId { get; init; }
    public string? ApiTokenName { get; init; }
    public string Action { get; init; } = "";
    public string Entity { get; init; } = "";
    public string? EntityId { get; init; }
    public string? BeforeJson { get; init; }
    public string? AfterJson { get; init; }
    public string? Ip { get; init; }
}

public sealed class SystemEventRow
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public string Level { get; init; } = "";
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long Total);

/// <summary>Reads the two logs for Settings > Logs. The audit log is append-only; nothing here changes it.</summary>
public sealed class LogReader(IDbConnection conn, IDbTransaction? tx = null)
{
    public const int MaxPageSize = 500;

    public Page<AuditRow> Audit(AuditFilter f, int page = 1, int pageSize = 100)
    {
        TypeHandlers.EnsureRegistered();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var where = new StringBuilder("1 = 1");
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(f.Entity)) { where.Append(" AND l.entity = @entity"); args.Add("entity", f.Entity); }
        if (!string.IsNullOrWhiteSpace(f.EntityId)) { where.Append(" AND l.entity_id = @entityId"); args.Add("entityId", f.EntityId); }
        if (!string.IsNullOrWhiteSpace(f.Action)) { where.Append(" AND l.action LIKE @action"); args.Add("action", f.Action.Replace("%", "") + "%"); }
        if (f.UserId is { } u) { where.Append(" AND l.user_id = @userId"); args.Add("userId", u); }
        if (f.From is { } from) { where.Append(" AND l.at >= @from"); args.Add("from", new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)); }
        if (f.To is { } to) { where.Append(" AND l.at < @to"); args.Add("to", new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)); }

        var total = conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM audit_log l WHERE {where}", args, tx);
        args.Add("take", pageSize);
        args.Add("skip", (page - 1) * pageSize);
        var rows = conn.Query<AuditRow>($"""
            SELECT l.id AS Id, l.at AS At, l.user_id AS UserId, u.display_name AS UserName, l.api_token_id AS ApiTokenId, t.name AS ApiTokenName,
                   l.action AS Action, l.entity AS Entity, l.entity_id AS EntityId, l.before_json AS BeforeJson, l.after_json AS AfterJson, l.ip AS Ip
            FROM audit_log l
            LEFT JOIN users u ON u.id = l.user_id
            LEFT JOIN api_tokens t ON t.id = l.api_token_id
            WHERE {where}
            ORDER BY l.id DESC LIMIT @take OFFSET @skip
            """, args, tx).ToList();
        return new Page<AuditRow>(rows, page, pageSize, total);
    }

    public Page<SystemEventRow> SystemEvents(string? level = null, string? source = null, int page = 1, int pageSize = 100)
    {
        TypeHandlers.EnsureRegistered();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var where = "1 = 1" + (string.IsNullOrWhiteSpace(level) ? "" : " AND level = @level") + (string.IsNullOrWhiteSpace(source) ? "" : " AND source = @source");
        var total = conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM system_events WHERE {where}", new { level, source }, tx);
        var rows = conn.Query<SystemEventRow>($"""
            SELECT id AS Id, at AS At, level AS Level, source AS Source, message AS Message, detail AS Detail
            FROM system_events WHERE {where} ORDER BY id DESC LIMIT @take OFFSET @skip
            """, new { level, source, take = pageSize, skip = (page - 1) * pageSize }, tx).ToList();
        return new Page<SystemEventRow>(rows, page, pageSize, total);
    }
}
