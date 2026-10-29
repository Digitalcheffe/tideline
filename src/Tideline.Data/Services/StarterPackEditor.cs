using System.Data;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>What the starter pack would still add to this database.</summary>
public sealed record StarterStatus(int CategoriesInPack, int CategoriesToAdd, int RulesInPack, int RulesToAdd);

public sealed record StarterResult(IReadOnlyList<string> CategoriesAdded, IReadOnlyList<string> RulesAdded, int CategoriesAlreadyThere, int RulesAlreadyThere);

/// <summary>
/// Adds the starter categories and rules that ship with the program (docs/ARCHITECTURE.md). Only what is missing, matched by name, so it is safe to
/// run again and never touches a category or rule a person made. The change and its audit entry are written in one transaction.
/// </summary>
public sealed class StarterPackEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    private (IReadOnlyList<string> Categories, IReadOnlyList<string> Rules) Missing(HouseholdSetup.Household pack, IDbTransaction? tx)
    {
        var haveCategories = Conn.Query<string>("SELECT name FROM categories", transaction: tx).ToHashSet();
        var haveRules = Conn.Query<string>("SELECT name FROM rules", transaction: tx).ToHashSet();
        return (pack.Categories.Select(c => c.Name).Where(n => !haveCategories.Contains(n)).ToList(),
                pack.Rules.Select(r => r.Name).Where(n => !haveRules.Contains(n)).ToList());
    }

    public StarterStatus Status()
    {
        TypeHandlers.EnsureRegistered();
        var pack = StarterPack.Load();
        var (categories, rules) = Missing(pack, null);
        return new StarterStatus(pack.Categories.Count, categories.Count, pack.Rules.Count, rules.Count);
    }

    public StarterResult Load() => InTransaction((tx, audit) =>
    {
        var pack = StarterPack.Load();
        var (categories, rules) = Missing(pack, tx);

        HouseholdSetup.Apply(pack, Conn, tx, Clock);

        if (categories.Count > 0 || rules.Count > 0)
            audit.Write("starter_pack.loaded", "rule", (string?)null, null, new { categoriesAdded = categories, rulesAdded = rules });

        return new StarterResult(categories, rules, pack.Categories.Count - categories.Count, pack.Rules.Count - rules.Count);
    });
}
