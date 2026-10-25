using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tideline.Core;
using Tideline.Core.Moves;
using Tideline.Core.Rules;
using Tideline.Data;
using Tideline.Data.Repositories;

namespace Tideline.Cli;

/// <summary>
/// Loads a household (accounts, categories, rules, move patterns) from a local JSON file so the ledger can be tried
/// before the Settings screens exist (Phase 3). The file is the household's own data: keep it outside the repository.
/// It only adds what is missing, matching by name, so it is safe to run again.
/// </summary>
public static class HouseholdSetup
{
    public sealed class Household
    {
        public List<AccountDef> Accounts { get; set; } = [];
        public List<CategoryDef> Categories { get; set; } = [];
        public List<RuleDef> Rules { get; set; } = [];
        public List<MovePatternDef> MovePatterns { get; set; } = [];
    }

    public sealed class AccountDef
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Institution { get; set; } = "Truist";
        public string? BankNumber { get; set; }
        public decimal? OpeningBalance { get; set; }
        public string? OpeningOn { get; set; }
        public bool InSpread { get; set; } = true;
        public string? CsvUntilOn { get; set; }
    }

    public sealed class CategoryDef
    {
        public string Name { get; set; } = "";
        public string DefaultType { get; set; } = "extra";
    }

    public sealed class RuleDef
    {
        public string Name { get; set; } = "";
        public int Priority { get; set; }
        public Dictionary<string, JsonElement> When { get; set; } = [];
        public RuleSet Set { get; set; } = new();
        public bool Active { get; set; } = true;
    }

    public sealed class RuleSet
    {
        public string? Category { get; set; }
        public string? Type { get; set; }
        public string? Payee { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    public sealed class MovePatternDef
    {
        public string From { get; set; } = "";
        public string FromContains { get; set; } = "";
        public string To { get; set; } = "";
        public string ToContains { get; set; } = "";
        public int MaxDaysApart { get; set; } = 5;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Household Parse(string json) =>
        JsonSerializer.Deserialize<Household>(json, Json) ?? throw new InvalidDataException("The household file is empty.");

    private static readonly Dictionary<string, (string Field, string Op)> WhenKeys = new()
    {
        ["contains_any"] = ("description", "contains_any"),
        ["description_is_any"] = ("description", "equals_any"),
        ["starts_with"] = ("description", "starts_with"),
        ["amount_min"] = ("amount", "gte"),
        ["amount_max"] = ("amount", "lte"),
        ["account"] = ("account", "is"),
        ["account_kind"] = ("account_kind", "in"),
        ["direction"] = ("direction", "is"),
    };

    /// <returns>One line per thing added or already there.</returns>
    public static IReadOnlyList<string> Apply(Household h, SqliteConnectionFactory factory, TimeProvider clock)
    {
        var log = new List<string>();
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();

        var accounts = new AccountRepository(conn, clock, tx);
        var categories = new CategoryRepository(conn, tx);
        var tags = new TagRepository(conn, tx);
        var rules = new RuleRepository(conn, clock, tx);
        var patterns = new MovePatternRepository(conn, tx);

        foreach (var a in h.Accounts)
        {
            if (accounts.All().Any(x => x.Name == a.Name)) { log.Add($"account {a.Name}: already there"); continue; }
            var kind = ParseKind(a.Kind, a.Name);
            accounts.Insert(new NewAccount(a.Name, kind, a.Institution, a.BankNumber,
                a.OpeningBalance is { } d ? (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero) : null,
                a.OpeningOn is { } on ? DateOnly.Parse(on) : null, a.InSpread,
                a.CsvUntilOn is { } cu ? DateOnly.Parse(cu) : null));
            log.Add($"account {a.Name}: added");
        }

        foreach (var c in h.Categories)
        {
            if (categories.IdByName(c.Name) is not null) { log.Add($"category {c.Name}: already there"); continue; }
            categories.Insert(c.Name, ParseType(c.DefaultType, $"category {c.Name}"));
            log.Add($"category {c.Name}: added");
        }

        var existingRules = rules.All().Select(r => r.Name).ToHashSet();
        foreach (var r in h.Rules)
        {
            if (existingRules.Contains(r.Name)) { log.Add($"rule {r.Name}: already there"); continue; }

            var conditions = new List<(string, string, string)>();
            foreach (var (key, value) in r.When)
            {
                if (!WhenKeys.TryGetValue(key, out var fo)) throw new InvalidDataException($"rule {r.Name}: unknown condition '{key}'.");
                var raw = value.GetRawText();
                RuleCondition.From(fo.Field, fo.Op, raw); // fail early on bad values
                conditions.Add((fo.Field, fo.Op, raw));
            }

            long? categoryId = null;
            if (r.Set.Category is { } cn)
                categoryId = categories.IdByName(cn) ?? throw new InvalidDataException($"rule {r.Name}: unknown category '{cn}'.");
            LineType? type = r.Set.Type is { } t ? ParseType(t, $"rule {r.Name}") : null;

            rules.Insert(new NewRule(r.Name, r.Priority, conditions, categoryId, type, r.Set.Payee,
                TagIds: r.Set.Tags.Select(tags.GetOrCreate).ToList(), IsActive: r.Active));
            log.Add($"rule {r.Name}: added");
        }

        var byName = accounts.All().ToDictionary(a => a.Name, a => a.Id);
        var existingPatterns = patterns.All();
        foreach (var p in h.MovePatterns)
        {
            long Id(string n) => byName.TryGetValue(n, out var id) ? id : throw new InvalidDataException($"move pattern: unknown account '{n}'.");
            var pattern = new MovePattern(0, Id(p.From), p.FromContains, Id(p.To), p.ToContains, p.MaxDaysApart);
            if (existingPatterns.Any(e => e.FromAccountId == pattern.FromAccountId && e.ToAccountId == pattern.ToAccountId
                    && e.FromContains == pattern.FromContains && e.ToContains == pattern.ToContains))
            { log.Add($"move pattern {p.From} to {p.To}: already there"); continue; }
            patterns.Insert(pattern);
            log.Add($"move pattern {p.From} to {p.To}: added");
        }

        tx.Commit();
        return log;
    }

    private static AccountKind ParseKind(string text, string account) =>
        Enum.GetValues<AccountKind>().FirstOrDefault(k => k.ToDb() == text.Trim().ToLowerInvariant().Replace(' ', '_'), (AccountKind)(-1)) is var k && (int)k >= 0
            ? k
            : throw new InvalidDataException($"account {account}: unknown kind '{text}' (checking, savings, credit_card, loan, asset, retirement).");

    private static LineType ParseType(string text, string where)
    {
        try { return TypeHandlers.FromDb<LineType>(text.Trim().ToLowerInvariant()); }
        catch (InvalidDataException) { throw new InvalidDataException($"{where}: unknown type '{text}' (income, bill, required, extra, one_time, savings)."); }
    }
}
