using System.Data;
using Tideline.Core;
using Tideline.Core.Rules;
using Tideline.Data.Repositories;
using Rule = Tideline.Core.Rules.Rule;

namespace Tideline.Data.Services;

public sealed record Classification(
    long? CategoryId,
    LineType Type,
    string? Payee,
    long? RecurringId,
    long? PlanId,
    long? RuleId,
    IReadOnlyList<long> TagIds,
    bool MatchedARule);

/// <summary>
/// Gives a bank line its meaning: the first matching rule wins; anything unmatched goes to Uncategorized with the <c>review</c> tag
/// (docs/ARCHITECTURE.md). Unmatched money coming into a checking or savings account is typed Income, so a first paycheck is not
/// counted as spending before anyone has written a rule; anything else unmatched is Extra. Used by the import and by "reset to rules".
/// </summary>
public sealed class LineClassifier
{
    public const string UncategorizedName = "Uncategorized";
    public const string ReviewTagName = "review";

    private readonly IReadOnlyList<Rule> _rules;
    private readonly IReadOnlyDictionary<long, LineType> _categoryTypes;

    public long UncategorizedId { get; }
    public long ReviewTagId { get; }

    public LineClassifier(IReadOnlyList<Rule> rules, IReadOnlyDictionary<long, LineType> categoryTypes, long uncategorizedId, long reviewTagId)
    {
        _rules = rules;
        _categoryTypes = categoryTypes;
        UncategorizedId = uncategorizedId;
        ReviewTagId = reviewTagId;
    }

    /// <summary>Reads the rules and categories, creating the defaults (Uncategorized, review) if they are missing.</summary>
    public static LineClassifier Load(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
    {
        var categories = new CategoryRepository(conn, tx);
        var uncategorized = categories.IdByName(UncategorizedName) ?? categories.Insert(UncategorizedName, LineType.Extra);
        var review = new TagRepository(conn, tx).GetOrCreate(ReviewTagName);
        return new LineClassifier(new RuleRepository(conn, clock, tx).All(), categories.DefaultTypes(), uncategorized, review);
    }

    /// <summary>A deposit into a checking or savings account. A credit on a card is a refund, not income, so it stays out.</summary>
    private static bool IsMoneyIn(RuleLine line) => line.AmountCents > 0 && line.AccountKind is AccountKind.Checking or AccountKind.Savings;

    public Classification Classify(RuleLine line, string merchantName)
    {
        var decision = RulesEngine.Decide(line, _rules, c => _categoryTypes.TryGetValue(c, out var t) ? t : LineType.Extra);
        var tags = new List<long>(decision?.TagIds ?? []);
        var categoryId = decision?.CategoryId;
        var type = decision?.Type;

        if (decision is null || type is null)
        {
            categoryId ??= UncategorizedId;
            type ??= decision is null && IsMoneyIn(line) ? LineType.Income : _categoryTypes.GetValueOrDefault(categoryId.Value, LineType.Extra);
            if (decision is null) tags.Add(ReviewTagId);
        }

        return new Classification(categoryId, type.Value, decision?.Payee ?? PayeeCleaner.For(merchantName, line.Description),
            decision?.RecurringId, decision?.PlanId, decision?.Rule.Id, tags, decision is not null);
    }
}
