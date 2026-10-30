using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Reports;

/// <summary>A suggestion the app ships, with the switch a person has set. The words come from <see cref="SuggestionCatalog"/>.</summary>
public sealed record RecommendationRuleView(
    long Id, string Key, string Group, string Title, string Advice, string? Note, string? Action, string Recurrence, bool IsActive, bool HasOwnMessage, int SortOrder);

/// <summary>A suggestion that applies right now: one that is on, for a flag that is still true.</summary>
public sealed record RecommendationCard(
    long RuleId, string RuleName, string Suggestion, string? Note, string? Action, long FlagId, string FlagKind, string Severity, string FlagTitle, string FlagDetail,
    string Period, string SubjectType, long SubjectId, bool Acknowledged);

/// <summary>Suggestions are shipped by the app; people switch them on and off and mark a card reviewed (docs/ARCHITECTURE.md).</summary>
public sealed class RecommendationReader(IDbConnection conn)
{
    public IReadOnlyList<RecommendationRuleView> Rules()
    {
        TypeHandlers.EnsureRegistered();
        var rows = conn.Query<(long Id, string Kind, bool Active, int Order)>("SELECT id, flag_kind, is_active, sort_order FROM recommendation_rules ORDER BY sort_order, id");
        var views = new List<RecommendationRuleView>();
        foreach (var r in rows)
        {
            if (SuggestionCatalog.Find(r.Kind) is not { } s) continue;
            var note = s.Note ?? (s.IsAlertAdvice ? null : SuggestionCatalog.NotAdvice);
            views.Add(new RecommendationRuleView(r.Id, s.Key, s.Group, s.Title, s.Advice, note, s.Action, s.Recurrence, r.Active, !s.IsAlertAdvice, r.Order));
        }
        return views;
    }

    /// <summary>Flags that are still true (acknowledged or not), each with every suggestion that is on and not yet reviewed for it. Most serious first.</summary>
    public IReadOnlyList<RecommendationCard> Cards()
    {
        TypeHandlers.EnsureRegistered();
        var rules = Rules().Where(r => r.IsActive).ToList();
        var dismissed = conn.Query<(long RuleId, long FlagId)>("SELECT rule_id, flag_id FROM recommendation_dismissals").ToHashSet();
        var flags = conn.Query<(long Id, string Kind, string SubjectType, long SubjectId, string Period, string Severity, string Message, bool Acknowledged)>(
            "SELECT id, kind, subject_type, subject_id, period, severity, message, acknowledged_at IS NOT NULL FROM flags WHERE resolved_at IS NULL").ToList();

        var cards = new List<RecommendationCard>();
        foreach (var f in flags.OrderByDescending(f => SeverityExtensions.Rank(f.Severity)).ThenByDescending(f => f.Id))
        {
            var (title, detail) = FlagText.Split(f.Message);
            foreach (var r in rules.Where(r => r.Key == f.Kind))
            {
                if (dismissed.Contains((r.Id, f.Id))) continue;
                cards.Add(new RecommendationCard(r.Id, r.Title, r.Advice, r.Note, r.Action, f.Id, f.Kind, f.Severity, title, detail, f.Period, f.SubjectType, f.SubjectId, f.Acknowledged));
            }
        }
        return cards;
    }
}
