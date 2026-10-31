using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

/// <summary>One line offered for a new recurring item, and whether it starts ticked.</summary>
public sealed record QuickLine(long Id, DateOnly PostedOn, string Description, string? Payee, long Cents, bool Selected);

/// <summary>What the quick form starts with: the words, the usual amounts, the day and the lines, all worked out from the lines it was opened with.</summary>
public sealed record QuickSuggestion(
    string Name, long CategoryId, string CategoryName, string Frequency, int EveryN, long UsualCents, long MinCents, long MaxCents, int DueDay,
    DateOnly FirstDueOn, string? Match, IReadOnlyList<QuickLine> Lines);

/// <summary>What a person confirmed on the quick form.</summary>
public sealed record QuickInput(
    string? Name, long CategoryId, long? MinCents, long? MaxCents, string? Frequency, int? EveryN, int? DueDay, string? FirstDueOn, IReadOnlyList<long>? LineIds, bool? AddRule);

public sealed record QuickCreated(long RecurringId, string Name, int LinesLinked, int LinesSkipped, long? RuleId);

/// <summary>
/// The quick way to make a recurring item from lines you can see: open it from a Bills "Other" row, from selected lines, or from Settings, and it
/// comes filled in; confirm and the item is made, the ticked lines are linked, and (if asked) a rule links next month's. It goes through the same
/// editors as doing it by hand, so every step is checked and audited. Reading changes nothing.
/// </summary>
public static class QuickRecurring
{
    public const int PoolLimit = 200;

    private sealed record Row(long Id, DateOnly On, string Description, string? Payee, long Cents, long CategoryId, string Type);

    private static string Who(Row r) => (string.IsNullOrWhiteSpace(r.Payee) ? r.Description : r.Payee).Trim().ToUpperInvariant();

    public static QuickSuggestion Suggest(IDbConnection conn, DateOnly today, long? categoryId, IReadOnlyList<long>? lineIds, string? name)
    {
        TypeHandlers.EnsureRegistered();
        var seeds = (lineIds ?? []).Distinct().Take(500).ToList();
        var seedRows = seeds.Count == 0 ? [] : Rows(conn, "t.id IN @seeds", new { seeds });
        if (seeds.Count > 0 && seedRows.Count == 0) throw new EditException(EditError.NotFound, "Those lines are not there any more.");

        var category = categoryId
            ?? seedRows.GroupBy(r => r.CategoryId).OrderByDescending(g => g.Count()).Select(g => (long?)g.Key).FirstOrDefault()
            ?? throw new EditException(EditError.Invalid, "Pick a category.");
        var categoryName = conn.ExecuteScalar<string?>("SELECT name FROM categories WHERE id = @category", new { category }) ?? throw new EditException(EditError.NotFound, "That category does not exist.");

        // the pool: this category's lines that are not tracked yet and look like money out
        var since = today.AddMonths(-RecurringInference.WindowMonths);
        var pool = Rows(conn, "t.category_id = @category AND t.recurring_id IS NULL AND t.move_id IS NULL AND t.left_out_at IS NULL AND t.amount_cents < 0 AND t.posted_on >= @since",
            new { category, since }).OrderByDescending(r => r.On).ThenByDescending(r => r.Id).Take(PoolLimit).ToList();
        foreach (var s in seedRows.Where(s => s.Cents > 0 && !pool.Any(p => p.Id == s.Id))) pool.Add(s);

        // what starts ticked: the lines it was opened with and the other lines of the same payee; with none given, the bill lines of the category
        var wanted = new HashSet<long>(seedRows.Select(r => r.Id));
        var payees = seedRows.Select(Who).ToHashSet();
        foreach (var p in pool)
            if (payees.Contains(Who(p))) wanted.Add(p.Id);
        if (seedRows.Count == 0)
            foreach (var p in pool.Where(p => p.Type == "bill")) wanted.Add(p.Id);

        var chosen = pool.Where(p => wanted.Contains(p.Id)).ToList();
        string suggestedName;
        if (!string.IsNullOrWhiteSpace(name)) suggestedName = name.Trim();
        else if (seedRows.Count > 0)
        {
            var top = seedRows.GroupBy(Who).OrderByDescending(g => g.Count()).First().First();
            suggestedName = BillFinder.Title(string.IsNullOrWhiteSpace(top.Payee) ? top.Description : top.Payee);
        }
        else suggestedName = categoryName;
        if (suggestedName.Length > 100) suggestedName = suggestedName[..100];

        var (frequency, everyN) = Rhythm(chosen);
        var amounts = chosen.Select(c => c.Cents).OrderBy(c => c).ToList();
        var usual = amounts.Count == 0 ? 0 : amounts[amounts.Count / 2];
        var low = amounts.Count == 0 ? 0 : (long)Math.Floor(amounts[0] * (1 - RecurringInference.Margin));
        var high = amounts.Count == 0 ? 0 : (long)Math.Ceiling(amounts[^1] * (1 + RecurringInference.Margin));
        var dueDay = chosen.Count == 0 ? Math.Min(today.Day, 28) : chosen.Select(c => c.On.Day).OrderBy(d => d).ElementAt(chosen.Count / 2);
        var earliest = chosen.Count == 0 ? today : chosen.Min(c => c.On);
        var firstDue = new DateOnly(earliest.Year, earliest.Month, Math.Min(dueDay, DateTime.DaysInMonth(earliest.Year, earliest.Month)));

        return new QuickSuggestion(suggestedName, category, categoryName, frequency, everyN, usual, low, high, dueDay, firstDue,
            chosen.Count == 0 ? null : BillFinder.MatchWords(suggestedName, chosen.Select(c => c.Description).ToList()),
            pool.Select(p => new QuickLine(p.Id, p.On, p.Description, p.Payee, p.Cents, wanted.Contains(p.Id))).ToList());
    }

    /// <summary>The usual number of months between payments; anything that is not a clean rhythm is called monthly.</summary>
    private static (string Frequency, int EveryN) Rhythm(IReadOnlyList<Row> lines)
    {
        var months = lines.Select(l => l.On.Year * 12 + l.On.Month).Distinct().OrderBy(m => m).ToList();
        if (months.Count < 2) return ("monthly", 1);
        var gaps = months.Zip(months.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
        var gap = gaps[gaps.Count / 2];
        return gap == 12 ? ("yearly", 1) : gap is 2 or 3 or 6 ? ("monthly", gap) : ("monthly", 1);
    }

    private static List<Row> Rows(IDbConnection conn, string where, object args) =>
        conn.Query<(long Id, DateOnly On, string Description, string? Payee, long Cents, long CategoryId, string Type)>($"""
            SELECT t.id, t.posted_on, t.original_description, t.payee, -t.amount_cents, COALESCE(t.category_id, 0), COALESCE(t.type, '')
            FROM transactions t WHERE {where}
            """, args).Select(r => new Row(r.Id, r.On, r.Description, r.Payee, r.Cents, r.CategoryId, r.Type)).ToList();

    public static QuickCreated Create(IDbConnection conn, TimeProvider clock, Actor? actor, QuickInput input, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        if (input.MinCents is null || input.MaxCents is null) throw new EditException(EditError.Invalid, "Give the lowest and highest you expect.");
        var wanted = (input.LineIds ?? []).Distinct().Take(500).ToList();

        // only lines that are still free to link: money out, not a transfer, not left out, not already in another item
        var free = wanted.Count == 0 ? [] : Rows(conn, "t.id IN @wanted AND t.recurring_id IS NULL AND t.move_id IS NULL AND t.left_out_at IS NULL AND t.amount_cents < 0", new { wanted });
        var name = input.Name?.Trim() ?? "";

        var first = input.FirstDueOn;
        if (string.IsNullOrWhiteSpace(first))
        {
            var day = Math.Clamp(input.DueDay ?? 1, 1, 31);
            var start = free.Count == 0 ? today : free.Min(f => f.On);
            first = new DateOnly(start.Year, start.Month, Math.Min(day, DateTime.DaysInMonth(start.Year, start.Month))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var setup = new SetupEditor(conn, clock, actor);
        var id = setup.CreateRecurring(new RecurringInput(name, input.CategoryId, input.MinCents, input.MaxCents, input.Frequency, input.EveryN, input.DueDay ?? 1, first));
        var linked = free.Count == 0 ? 0 : BillAdder.Link(conn, clock, actor, id, free.Select(f => f.Id).ToList());

        long? ruleId = null;
        if (input.AddRule == true && free.Count > 0 && BillFinder.MatchWords(name, free.Select(f => f.Description).ToList()) is { } words)
        {
            ruleId = new RuleEditor(conn, clock, actor).Create(new RuleInput(BillAdder.Unique(conn, "rules", "Bill: " + name, 150), null, true, input.CategoryId, null, name, id, null,
                [new RuleConditionInput("description", "contains_any", JsonSerializer.SerializeToElement(new[] { words }))]));
        }
        return new QuickCreated(id, name, linked, wanted.Count - linked, ruleId);
    }
}
