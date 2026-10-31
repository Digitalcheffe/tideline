using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

/// <summary>A bill or subscription found in lines that are not tracked yet, ready to be added as a recurring item.</summary>
public sealed record BillCandidate(
    long Key, string Name, long CategoryId, string CategoryName, string Kind, string Frequency, int EveryN, long MinCents, long MaxCents, int DueDay,
    DateOnly FirstDueOn, int Occurrences, DateOnly LastPaid, long MonthlyCents, IReadOnlyList<long> LineIds, string? Match);

/// <summary>
/// Finds the bills and subscriptions hiding in untracked lines (docs/ARCHITECTURE.md, "Find my bills"): lines of type Bill, Required or Extra that
/// are not linked to a recurring item, grouped by who was paid (within a category, since one category such as Insurance holds several bills), then
/// judged by <see cref="RecurringInference"/>. It only reads.
/// </summary>
public static class BillFinder
{
    public const int Limit = 40;

    /// <summary>The same number for the same payee and category on every run, to name a candidate in a card.</summary>
    public static long Key(string name, long categoryId)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(name.ToUpperInvariant() + "|" + categoryId.ToString(CultureInfo.InvariantCulture))) hash = (hash ^ b) * 1099511628211UL;
        return (long)(hash & 0x7FFFFFFFFFFFFFFF);
    }

    private sealed record Row(long Id, DateOnly On, long Cents, string Name, string Description, long CategoryId, string CategoryName, string CategoryType, string Type);

    public static IReadOnlyList<BillCandidate> Find(IDbConnection conn, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var since = today.AddMonths(-RecurringInference.WindowMonths);
        var rows = conn.Query<(long Id, DateOnly On, long Cents, string Name, string Description, long CategoryId, string CategoryName, string CategoryType, string Type)>("""
            SELECT t.id, t.posted_on, -t.amount_cents, COALESCE(NULLIF(t.payee, ''), t.original_description), t.original_description,
                   c.id, c.name, c.default_type, t.type
            FROM transactions t JOIN categories c ON c.id = t.category_id
            WHERE t.recurring_id IS NULL AND t.move_id IS NULL AND t.left_out_at IS NULL AND t.amount_cents < 0
              AND t.type IN ('bill', 'extra', 'required') AND t.posted_on >= @since AND c.name <> @uncategorized
            """, new { since, uncategorized = LineClassifier.UncategorizedName })
            .Select(r => new Row(r.Id, r.On, r.Cents, r.Name, r.Description, r.CategoryId, r.CategoryName, r.CategoryType, r.Type)).ToList();

        var tracked = conn.Query<string>("SELECT name FROM recurring").Select(n => n.ToUpperInvariant()).ToHashSet();
        var found = new List<BillCandidate>();

        foreach (var g in rows.GroupBy(r => (Name: r.Name.Trim().ToUpperInvariant(), r.CategoryId)))
        {
            if (g.Key.Name.Length == 0 || tracked.Contains(g.Key.Name)) continue;
            var first = g.First();
            if (RecurringInference.Infer(g.Select(r => (r.On, r.Cents)).ToList(), today) is not { } p) continue;

            var bill = first.CategoryType == "bill" || g.Count(r => r.Type == "bill") * 2 > g.Count();
            found.Add(new BillCandidate(
                Key(first.Name, first.CategoryId), Title(first.Name), first.CategoryId, first.CategoryName, bill ? "bill" : "subscription",
                p.Frequency, p.EveryN, p.MinCents, p.MaxCents, p.DueDay, p.FirstDueOn, p.Occurrences, p.LastPaid, p.MonthlyCents,
                g.Select(r => r.Id).ToList(), MatchWords(first.Name, g.Select(r => r.Description).ToList())));
        }

        return found.OrderBy(c => c.Kind == "bill" ? 0 : 1).ThenByDescending(c => c.MonthlyCents).ThenBy(c => c.Name).Take(Limit).ToList();
    }

    /// <summary>A bank's wording, shouted and with store numbers, made readable: "CITY POWER" becomes "City Power".</summary>
    internal static string Title(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Any(char.IsLower)) return trimmed.Length > 100 ? trimmed[..100] : trimmed;
        var text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
        return text.Length > 100 ? text[..100] : text;
    }

    /// <summary>
    /// The words a rule can look for so that next month's line is linked by itself: the payee's name if every line's description has it, else the
    /// start the descriptions share. Null when nothing safe is common to them.
    /// </summary>
    internal static string? MatchWords(string name, IReadOnlyList<string> descriptions)
    {
        var upper = descriptions.Select(d => d.ToUpperInvariant()).ToList();
        var wanted = name.Trim().ToUpperInvariant();
        if (wanted.Length >= 4 && upper.All(d => d.Contains(wanted, StringComparison.Ordinal))) return wanted.Length > 100 ? wanted[..100] : wanted;

        var prefix = upper[0];
        foreach (var d in upper.Skip(1))
        {
            var n = 0;
            while (n < prefix.Length && n < d.Length && prefix[n] == d[n]) n++;
            prefix = prefix[..n];
        }
        prefix = prefix.TrimEnd(' ', '-', '*', '#', '.', ',');
        return prefix.Length >= 4 ? prefix : null;
    }
}

/// <summary>What happened to one candidate that was added.</summary>
public sealed record BillAdded(long Key, long RecurringId, string Name, int LinesLinked, long? RuleId);

/// <summary>
/// Adds the candidates a person picked: a recurring item for each, its past lines linked to it, and a rule so next month's line is linked as it
/// arrives. It goes through the same editors as doing it by hand, so every step is checked and audited. Nothing is added without being asked.
/// </summary>
public static class BillAdder
{
    public static IReadOnlyList<BillAdded> Add(IDbConnection conn, TimeProvider clock, Actor? actor, IReadOnlyList<(long Key, string? Name)> picks, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        if (picks.Count == 0) throw new EditException(EditError.Invalid, "Pick at least one.");
        var candidates = BillFinder.Find(conn, today).ToDictionary(c => c.Key);
        var setup = new SetupEditor(conn, clock, actor);
        var rules = new RuleEditor(conn, clock, actor);
        var added = new List<BillAdded>();

        foreach (var (key, chosenName) in picks.DistinctBy(p => p.Key))
        {
            if (!candidates.TryGetValue(key, out var c)) throw new EditException(EditError.NotFound, "One of those is not found any more. Look again.");
            var name = Unique(conn, "recurring", string.IsNullOrWhiteSpace(chosenName) ? c.Name : chosenName.Trim(), 100);

            var id = setup.CreateRecurring(new RecurringInput(name, c.CategoryId, c.MinCents, c.MaxCents, c.Frequency, c.EveryN, c.DueDay,
                c.FirstDueOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

            var linked = Link(conn, clock, actor, id, c.LineIds);

            long? ruleId = null;
            if (c.Match is { } words)
            {
                ruleId = rules.Create(new RuleInput(Unique(conn, "rules", "Bill: " + name, 150), null, true, c.CategoryId, null, name, id, null,
                    [new RuleConditionInput("description", "contains_any", JsonSerializer.SerializeToElement(new[] { words }))]));
            }
            added.Add(new BillAdded(key, id, name, linked, ruleId));
        }
        return added;
    }

    internal static int Link(IDbConnection conn, TimeProvider clock, Actor? actor, long recurringId, IReadOnlyList<long> lineIds)
    {
        using var tx = conn.BeginTransaction();
        var linked = 0;
        foreach (var chunk in lineIds.Chunk(400))
            linked += conn.Execute("UPDATE transactions SET recurring_id = @recurringId, updated_at = @now WHERE id IN @chunk AND recurring_id IS NULL AND left_out_at IS NULL",
                new { recurringId, now = clock.GetUtcNow(), chunk }, tx);
        new AuditWriter(conn, clock, tx, actor).Write("recurring.lines_linked", "recurring", recurringId, null, new { linked });
        tx.Commit();
        return linked;
    }

    internal static string Unique(IDbConnection conn, string table, string name, int max)
    {
        var baseName = name.Length > max - 5 ? name[..(max - 5)] : name;
        var candidate = baseName;
        for (var n = 2; conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table} WHERE name = @candidate", new { candidate }) > 0; n++) candidate = $"{baseName} ({n})";
        return candidate;
    }
}
