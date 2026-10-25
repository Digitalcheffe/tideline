using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Tideline.Data.Services;

/// <summary>What the editors share: one transaction per change with its audit entry, and plain-words errors.</summary>
public abstract class EditorBase(IDbConnection conn, TimeProvider clock, Actor? actor)
{
    protected IDbConnection Conn => conn;
    protected TimeProvider Clock => clock;
    protected long? UserId => actor?.UserId;

    protected static EditException Bad(string message) => new(EditError.Invalid, message);
    protected static EditException Missing(string what) => new(EditError.NotFound, $"There is no such {what}.");

    protected T InTransaction<T>(Func<IDbTransaction, AuditWriter, T> work)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        try
        {
            var result = work(tx, new AuditWriter(conn, clock, tx, actor));
            tx.Commit();
            return result;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            throw new EditException(EditError.Conflict, "That name is already used by something else.");
        }
    }

    protected void InTransaction(Action<IDbTransaction, AuditWriter> work) => InTransaction<int>((tx, audit) =>
    {
        work(tx, audit);
        return 0;
    });

    protected static string Name(string? name, int max, string what)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) throw Bad($"Give the {what} a name.");
        if (n.Length > max) throw Bad($"The {what} name is too long ({max} characters at most).");
        return n;
    }

    protected static DateOnly? Day(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw Bad($"{what} must be a date like 2026-03-31.");
    }

    protected static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    protected bool Exists(string table, long id, IDbTransaction tx) =>
        Dapper.SqlMapper.ExecuteScalar<long>(conn, $"SELECT COUNT(*) FROM {table} WHERE id = @id", new { id }, tx) > 0;
}
