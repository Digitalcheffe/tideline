using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Repositories;

/// <summary>What an evaluator found: one thing that needs a person, identified so that finding it twice is one flag.</summary>
public sealed record FlagCandidate(string Kind, string SubjectType, long SubjectId, string Period, Severity Severity, string Message, string? AutoAckNote = null);

public sealed record FlagRow(
    long Id, string Kind, string SubjectType, long SubjectId, string Period, string Severity, string Message,
    DateTimeOffset CreatedAt, DateTimeOffset? AcknowledgedAt, string? AckNote, DateTimeOffset? ResolvedAt)
{
    public bool IsOpen => ResolvedAt is null && AcknowledgedAt is null;
}

public static class SeverityExtensions
{
    public static string ToDb(this Severity s) => s switch { Severity.Alert => "alert", Severity.Warn => "warn", _ => "info" };
    public static int Rank(string severity) => severity switch { "alert" => 2, "warn" => 1, _ => 0 };
}

/// <summary>
/// Flags (docs/ARCHITECTURE.md). A flag is unique on kind, subject and period, so finding the same thing again changes
/// nothing. A flag whose condition clears is resolved, not deleted; one a person has acknowledged stays acknowledged.
/// </summary>
public sealed class FlagRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "";
        public string SubjectType { get; set; } = "";
        public long SubjectId { get; set; }
        public string Period { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public string? AckNote { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
    }

    private const string Select = """
        SELECT id AS Id, kind AS Kind, subject_type AS SubjectType, subject_id AS SubjectId, period AS Period, severity AS Severity, message AS Message,
               created_at AS CreatedAt, acknowledged_at AS AcknowledgedAt, ack_note AS AckNote, resolved_at AS ResolvedAt
        FROM flags
        """;

    private static FlagRow Map(Row r) => new(r.Id, r.Kind, r.SubjectType, r.SubjectId, r.Period, r.Severity, r.Message, r.CreatedAt, r.AcknowledgedAt, r.AckNote, r.ResolvedAt);

    public static string KeyOf(string kind, string subjectType, long subjectId, string period) => $"{kind}|{subjectType}|{subjectId}|{period}";

    /// <summary>Adds the flag, or refreshes its text and reopens it if it had been resolved. Returns the id and whether it is new.</summary>
    public (long Id, bool Created) Upsert(FlagCandidate c)
    {
        TypeHandlers.EnsureRegistered();
        var message = c.Message.Length > 400 ? c.Message[..400] : c.Message;
        var existing = conn.QueryFirstOrDefault<(long Id, DateTimeOffset? Resolved)?>(
            "SELECT id, resolved_at FROM flags WHERE kind = @Kind AND subject_type = @SubjectType AND subject_id = @SubjectId AND period = @Period", c, tx);

        if (existing is { } e)
        {
            conn.Execute("UPDATE flags SET message = @message, severity = @severity, resolved_at = NULL WHERE id = @id",
                new { id = e.Id, message, severity = c.Severity.ToDb() }, tx);
            return (e.Id, false);
        }

        var now = clock.GetUtcNow();
        conn.Execute("""
            INSERT INTO flags (kind, subject_type, subject_id, period, severity, message, created_at, acknowledged_at, ack_note)
            VALUES (@Kind, @SubjectType, @SubjectId, @Period, @severity, @message, @now, @ackAt, @ackNote)
            """, new
        {
            c.Kind, c.SubjectType, c.SubjectId, c.Period, severity = c.Severity.ToDb(), message, now,
            ackAt = c.AutoAckNote is null ? (DateTimeOffset?)null : now, ackNote = c.AutoAckNote,
        }, tx);
        return (conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx), true);
    }

    /// <summary>Resolves every unresolved flag of these kinds that was not found this time.</summary>
    public int ResolveNotIn(IReadOnlyCollection<string> kinds, ISet<string> foundKeys)
    {
        TypeHandlers.EnsureRegistered();
        var open = conn.Query<(long Id, string Kind, string SubjectType, long SubjectId, string Period)>(
            "SELECT id, kind, subject_type, subject_id, period FROM flags WHERE resolved_at IS NULL AND kind IN @kinds", new { kinds }, tx);
        var now = clock.GetUtcNow();
        var resolved = 0;
        foreach (var f in open.Where(f => !foundKeys.Contains(KeyOf(f.Kind, f.SubjectType, f.SubjectId, f.Period))))
        {
            conn.Execute("UPDATE flags SET resolved_at = @now WHERE id = @id", new { id = f.Id, now }, tx);
            resolved++;
        }
        return resolved;
    }

    /// <summary>
    /// Closes the open "what was this check?" alerts whose line now has a real category (or was left out, or became a transfer), without waiting
    /// for tonight's run. The evaluator's own pass for the kind decides the same way, so the two never disagree.
    /// </summary>
    public int ResolveAnsweredChecks()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Execute($"""
            UPDATE flags SET resolved_at = @now
            WHERE kind = @kind AND resolved_at IS NULL AND subject_type = 'transaction'
              AND subject_id NOT IN (SELECT t.id FROM transactions t WHERE {UnnamedCheckWhere})
            """, new { now = clock.GetUtcNow(), kind = FlagKinds.CheckUnknown }, tx);
    }

    /// <summary>
    /// A check nobody has named yet: the bank gave a check number (or, from SimpleFIN, only "Check #1042" in the description), it is not a transfer
    /// or left out, and it has no category or is still in the catch-all. Over the <c>transactions</c> table as <c>t</c>.
    /// </summary>
    public const string UnnamedCheckWhere = """
        (t.check_number IS NOT NULL OR LOWER(t.original_description) LIKE 'check #%')
        AND t.move_id IS NULL AND t.left_out_at IS NULL
        AND (t.category_id IS NULL OR t.category_id IN (SELECT id FROM categories WHERE name = 'Uncategorized'))
        """;

    public FlagRow? Get(long id) => conn.QuerySingleOrDefault<Row>(Select + " WHERE id = @id", new { id }, tx) is { } r ? Map(r) : null;

    /// <summary>Flags that are still true and nobody has dealt with, most serious first.</summary>
    public IReadOnlyList<FlagRow> Open()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<Row>(Select + " WHERE resolved_at IS NULL AND acknowledged_at IS NULL", transaction: tx).Select(Map)
            .OrderByDescending(f => SeverityExtensions.Rank(f.Severity)).ThenByDescending(f => f.CreatedAt).ToList();
    }

    public IReadOnlyList<FlagRow> Recent(int limit = 200)
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<Row>(Select + " ORDER BY id DESC LIMIT @limit", new { limit }, tx).Select(Map).ToList();
    }

    public void Acknowledge(long id, long? userId, string? note)
    {
        conn.Execute("UPDATE flags SET acknowledged_at = @now, acknowledged_by = @userId, ack_note = @note WHERE id = @id AND acknowledged_at IS NULL",
            new { id, now = clock.GetUtcNow(), userId, note }, tx);
    }
}
