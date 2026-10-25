using System.Data;
using Dapper;

namespace Tideline.Data.Repositories;

public enum EventLevel { Info, Warn, Error }

/// <summary>System log lines shown on the Imports page and in Settings > Logs (<c>system_events</c>).</summary>
public sealed class SystemEventRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    /// <summary>Messages are capped at the column width; put the long text in <paramref name="detail"/>.</summary>
    public void Add(EventLevel level, string source, string message, string? detail = null)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("INSERT INTO system_events (at, level, source, message, detail) VALUES (@now, @level, @source, @message, @detail)",
            new { now = clock.GetUtcNow(), level = level.ToDb(), source, message = message.Length > 400 ? message[..400] : message, detail }, tx);
    }

    public long Count() => conn.ExecuteScalar<long>("SELECT COUNT(*) FROM system_events", transaction: tx);
}
