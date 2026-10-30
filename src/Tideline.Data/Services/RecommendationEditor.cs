using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

/// <summary>
/// What a person can do with the suggestions the app ships: switch one on or off, and mark a card reviewed. Their words and rules are not
/// editable yet. Every change is audited.
/// </summary>
public sealed class RecommendationEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    public void SetOn(long id, bool on) => InTransaction((tx, audit) =>
    {
        var cur = Conn.QuerySingleOrDefault<(string Kind, bool Active)?>("SELECT flag_kind, is_active FROM recommendation_rules WHERE id = @id", new { id }, tx)
                  ?? throw Missing("suggestion");
        if (cur.Active == on) return;
        Conn.Execute("UPDATE recommendation_rules SET is_active = @on, updated_at = @now WHERE id = @id", new { id, on = on ? 1 : 0, now = Clock.GetUtcNow() }, tx);
        audit.Write("recommendation_rule.updated", "recommendation_rule", id, new { key = cur.Kind, isOn = cur.Active }, new { key = cur.Kind, isOn = on });
    });

    /// <summary>"Reviewed": hides one suggestion for one flag. It stays hidden if the flag comes back, and a new period brings a new flag.</summary>
    public void Dismiss(long ruleId, long flagId) => InTransaction((tx, audit) =>
    {
        if (!Exists("recommendation_rules", ruleId, tx)) throw Missing("suggestion");
        var flag = new FlagRepository(Conn, Clock, tx).Get(flagId) ?? throw Missing("flag");
        if (Conn.ExecuteScalar<long>("SELECT COUNT(*) FROM recommendation_dismissals WHERE rule_id = @ruleId AND flag_id = @flagId", new { ruleId, flagId }, tx) > 0)
            throw Bad("That suggestion has already been marked reviewed.");

        Conn.Execute("INSERT INTO recommendation_dismissals (rule_id, flag_id, dismissed_at, dismissed_by) VALUES (@ruleId, @flagId, @now, @by)",
            new { ruleId, flagId, now = Clock.GetUtcNow(), by = UserId }, tx);
        audit.Write("recommendation.dismissed", "recommendation_rule", ruleId, new { flagId, flag.Kind, dismissed = false }, new { flagId, flag.Kind, dismissed = true });
    });
}
