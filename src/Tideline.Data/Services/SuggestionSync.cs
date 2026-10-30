using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Services;

/// <summary>
/// Brings the <c>recommendation_rules</c> table in line with the suggestions the app ships (<see cref="SuggestionCatalog"/>): one row each, with the
/// current words, and the person's on/off switch left exactly as it was. A suggestion the app no longer ships is removed with its dismissals.
/// Run at start-up, so an update to the wording reaches everyone without touching their switches.
/// </summary>
public static class SuggestionSync
{
    public static void Run(IDbConnection conn, TimeProvider clock)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        var now = clock.GetUtcNow();
        var rows = conn.Query<(long Id, string Kind)>("SELECT id, flag_kind FROM recommendation_rules ORDER BY id", transaction: tx).ToList();
        var firstOfKind = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.First().Id);

        var order = 0;
        foreach (var s in SuggestionCatalog.All)
        {
            order += 10;
            if (firstOfKind.TryGetValue(s.Key, out var id))
            {
                conn.Execute("""
                    UPDATE recommendation_rules SET name = @title, suggestion = @advice, min_severity = 'info', contains = NULL, sort_order = @order, updated_at = @now
                    WHERE id = @id AND (name <> @title OR suggestion <> @advice OR sort_order <> @order OR min_severity <> 'info' OR contains IS NOT NULL)
                    """, new { id, title = s.Title, advice = s.Advice, order, now }, tx);
            }
            else
            {
                conn.Execute("""
                    INSERT INTO recommendation_rules (name, flag_kind, min_severity, contains, suggestion, is_active, sort_order, created_at, updated_at)
                    VALUES (@title, @key, 'info', NULL, @advice, @on, @order, @now, @now)
                    """, new { title = s.Title, key = s.Key, advice = s.Advice, on = s.DefaultOn ? 1 : 0, order, now }, tx);
            }
        }

        // a rule an owner made before suggestions were shipped as a fixed list, a second one for the same kind, or one that was retired, goes
        foreach (var gone in rows.Where(r => SuggestionCatalog.Find(r.Kind) is null || firstOfKind[r.Kind] != r.Id))
        {
            conn.Execute("DELETE FROM recommendation_dismissals WHERE rule_id = @id", new { id = gone.Id }, tx);
            conn.Execute("DELETE FROM recommendation_rules WHERE id = @id", new { id = gone.Id }, tx);
        }
        tx.Commit();
    }
}
