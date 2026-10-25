namespace Tideline.Core.Rules;

public sealed record Rule(
    long Id,
    string Name,
    int Priority,
    bool IsActive,
    IReadOnlyList<RuleCondition> Conditions,
    long? SetCategoryId = null,
    LineType? SetType = null,
    string? SetPayee = null,
    long? SetRecurringId = null,
    long? SetPlanId = null,
    IReadOnlyList<long>? TagIds = null)
{
    public bool Matches(RuleLine line) => Conditions.All(c => c.Holds(line));
}

/// <summary>What the winning rule says to do. <see cref="Type"/> is already resolved (rule type, else the category's default).</summary>
public sealed record RuleDecision(
    Rule Rule,
    long? CategoryId,
    LineType? Type,
    string? Payee,
    long? RecurringId,
    long? PlanId,
    IReadOnlyList<long> TagIds);

public static class RulesEngine
{
    /// <summary>
    /// First active rule, in ascending priority, whose conditions all hold wins (docs/06-rules-engine.md).
    /// Never touches manual overrides or moves. Returns null when nothing matches.
    /// </summary>
    /// <param name="categoryDefaultType">Looks up a category's default type, used when the rule sets none.</param>
    public static RuleDecision? Decide(RuleLine line, IEnumerable<Rule> rules, Func<long, LineType> categoryDefaultType)
    {
        if (line.IsManualOverride || line.IsMove) return null;

        var rule = rules
            .Where(r => r.IsActive)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .FirstOrDefault(r => r.Matches(line));
        if (rule is null) return null;

        LineType? type = rule.SetType ?? (rule.SetCategoryId is { } c ? categoryDefaultType(c) : null);
        return new RuleDecision(rule, rule.SetCategoryId, type, rule.SetPayee, rule.SetRecurringId, rule.SetPlanId,
            rule.TagIds ?? []);
    }
}
