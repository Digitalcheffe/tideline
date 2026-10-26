using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

public sealed record RecommendationRuleInput(string? Name, string? FlagKind = null, string? MinSeverity = null, string? Contains = null, string? Suggestion = null, bool? IsActive = null);

/// <summary>
/// Suggestion rules: when a flag of this kind is open (and is at least this serious, and its text has these words), show this suggestion
/// (docs/ARCHITECTURE.md). Every change is audited. Dismissing a suggestion for one flag is audited too.
/// </summary>
public sealed class RecommendationEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    private static readonly string[] Severities = ["info", "warn", "alert"];

    private object? Rule(long id, IDbTransaction tx) => Conn.QuerySingleOrDefault<(string Name, string Kind, string Min, string? Contains, string Suggestion, bool Active)?>(
        "SELECT name, flag_kind, min_severity, contains, suggestion, is_active FROM recommendation_rules WHERE id = @id", new { id }, tx) is { } r
        ? new { name = r.Name, flagKind = r.Kind, minSeverity = r.Min, contains = r.Contains, suggestion = r.Suggestion, isActive = r.Active }
        : null;

    private static string Kind(string? kind)
    {
        var k = kind?.Trim().ToLowerInvariant() ?? "";
        return FlagKinds.All.Contains(k) ? k : throw Bad("Pick one of the kinds of flag from the list.");
    }

    private static string Severity(string? severity)
    {
        var s = (severity ?? "info").Trim().ToLowerInvariant();
        return Severities.Contains(s) ? s : throw Bad("The least serious flag to react to is info, warn or alert.");
    }

    private static string Suggestion(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) throw Bad("Write the suggestion to show.");
        if (t.Length > 600) throw Bad("The suggestion is too long (600 characters at most).");
        return t;
    }

    private static string? Contains(string? text)
    {
        var t = Blank(text);
        return t is { Length: > 100 } ? throw Bad("The words to look for are too long (100 characters at most).") : t;
    }

    public long Create(RecommendationRuleInput r) => InTransaction((tx, audit) =>
    {
        var name = Name(r.Name, 100, "suggestion");
        var now = Clock.GetUtcNow();
        var order = Conn.ExecuteScalar<int>("SELECT COALESCE(MAX(sort_order), 0) + 10 FROM recommendation_rules", transaction: tx);
        Conn.Execute("""
            INSERT INTO recommendation_rules (name, flag_kind, min_severity, contains, suggestion, is_active, sort_order, created_at, updated_at)
            VALUES (@name, @kind, @severity, @contains, @suggestion, @active, @order, @now, @now)
            """, new { name, kind = Kind(r.FlagKind), severity = Severity(r.MinSeverity), contains = Contains(r.Contains), suggestion = Suggestion(r.Suggestion), active = (r.IsActive ?? true) ? 1 : 0, order, now }, tx);
        var id = Conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("recommendation_rule.created", "recommendation_rule", id, null, Rule(id, tx));
        return id;
    });

    public void Update(long id, RecommendationRuleInput r) => InTransaction((tx, audit) =>
    {
        var cur = Conn.QuerySingleOrDefault<(string Name, string Kind, string Min, string? Contains, string Suggestion, bool Active)?>(
            "SELECT name, flag_kind, min_severity, contains, suggestion, is_active FROM recommendation_rules WHERE id = @id", new { id }, tx) ?? throw Missing("suggestion");
        var before = Rule(id, tx);

        Conn.Execute("""
            UPDATE recommendation_rules SET name = @name, flag_kind = @kind, min_severity = @severity, contains = @contains, suggestion = @suggestion,
                is_active = @active, updated_at = @now
            WHERE id = @id
            """, new
        {
            id,
            name = r.Name is null ? cur.Name : Name(r.Name, 100, "suggestion"),
            kind = r.FlagKind is null ? cur.Kind : Kind(r.FlagKind),
            severity = r.MinSeverity is null ? cur.Min : Severity(r.MinSeverity),
            contains = r.Contains is null ? cur.Contains : Contains(r.Contains),
            suggestion = r.Suggestion is null ? cur.Suggestion : Suggestion(r.Suggestion),
            active = (r.IsActive ?? cur.Active) ? 1 : 0,
            now = Clock.GetUtcNow(),
        }, tx);
        audit.Write("recommendation_rule.updated", "recommendation_rule", id, before, Rule(id, tx));
    });

    public void Delete(long id) => InTransaction((tx, audit) =>
    {
        var before = Rule(id, tx) ?? throw Missing("suggestion");
        Conn.Execute("DELETE FROM recommendation_dismissals WHERE rule_id = @id", new { id }, tx);
        Conn.Execute("DELETE FROM recommendation_rules WHERE id = @id", new { id }, tx);
        audit.Write("recommendation_rule.deleted", "recommendation_rule", id, before, null);
    });

    /// <summary>"Not useful for this one": hides one suggestion for one flag. It stays hidden if the flag comes back.</summary>
    public void Dismiss(long ruleId, long flagId) => InTransaction((tx, audit) =>
    {
        if (!Exists("recommendation_rules", ruleId, tx)) throw Missing("suggestion");
        var flag = new FlagRepository(Conn, Clock, tx).Get(flagId) ?? throw Missing("flag");
        if (Conn.ExecuteScalar<long>("SELECT COUNT(*) FROM recommendation_dismissals WHERE rule_id = @ruleId AND flag_id = @flagId", new { ruleId, flagId }, tx) > 0)
            throw Bad("That suggestion has already been dismissed.");

        Conn.Execute("INSERT INTO recommendation_dismissals (rule_id, flag_id, dismissed_at, dismissed_by) VALUES (@ruleId, @flagId, @now, @by)",
            new { ruleId, flagId, now = Clock.GetUtcNow(), by = UserId }, tx);
        audit.Write("recommendation.dismissed", "recommendation_rule", ruleId, new { flagId, flag.Kind, dismissed = false }, new { flagId, flag.Kind, dismissed = true });
    });
}
