using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Reports;

public sealed record RecommendationRuleView(long Id, string Name, string FlagKind, string MinSeverity, string? Contains, string Suggestion, bool IsActive, int SortOrder);

/// <summary>A suggestion that applies right now: a rule that matched a flag that is still true.</summary>
public sealed record RecommendationCard(
    long RuleId, string RuleName, string Suggestion, long FlagId, string FlagKind, string Severity, string FlagTitle, string FlagDetail, string Period, bool Acknowledged);

/// <summary>Suggestions are rules on flags: kind, how serious, and words in the flag's text (docs/ARCHITECTURE.md).</summary>
public sealed class RecommendationReader(IDbConnection conn)
{
    public IReadOnlyList<RecommendationRuleView> Rules()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<(long Id, string Name, string Kind, string Min, string? Contains, string Suggestion, bool Active, int Order)>(
                "SELECT id, name, flag_kind, min_severity, contains, suggestion, is_active, sort_order FROM recommendation_rules ORDER BY sort_order, id")
            .Select(r => new RecommendationRuleView(r.Id, r.Name, r.Kind, r.Min, r.Contains, r.Suggestion, r.Active, r.Order)).ToList();
    }

    /// <summary>Flags that are still true (acknowledged or not), each with every active, undismissed rule that fits it. Most serious first.</summary>
    public IReadOnlyList<RecommendationCard> Cards()
    {
        TypeHandlers.EnsureRegistered();
        var rules = Rules().Where(r => r.IsActive).ToList();
        var dismissed = conn.Query<(long RuleId, long FlagId)>("SELECT rule_id, flag_id FROM recommendation_dismissals").ToHashSet();
        var flags = conn.Query<(long Id, string Kind, string Period, string Severity, string Message, bool Acknowledged)>(
            "SELECT id, kind, period, severity, message, acknowledged_at IS NOT NULL FROM flags WHERE resolved_at IS NULL").ToList();

        var cards = new List<RecommendationCard>();
        foreach (var f in flags.OrderByDescending(f => SeverityExtensions.Rank(f.Severity)).ThenByDescending(f => f.Id))
        {
            var (title, detail) = FlagText.Split(f.Message);
            foreach (var r in rules.Where(r => r.FlagKind == f.Kind))
            {
                if (SeverityExtensions.Rank(f.Severity) < SeverityExtensions.Rank(r.MinSeverity)) continue;
                if (r.Contains is { } words && !f.Message.Contains(words, StringComparison.OrdinalIgnoreCase)) continue;
                if (dismissed.Contains((r.Id, f.Id))) continue;
                cards.Add(new RecommendationCard(r.Id, r.Name, r.Suggestion, f.Id, f.Kind, f.Severity, title, detail, f.Period, f.Acknowledged));
            }
        }
        return cards;
    }
}
