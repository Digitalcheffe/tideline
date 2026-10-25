using System.Text.Json;

namespace Tideline.Core.Rules;

/// <summary>What a rule looks at. Built from a bank line plus its account.</summary>
public sealed record RuleLine(
    string Description,
    long AmountCents,
    string AccountBankNumber,
    AccountKind AccountKind,
    bool IsManualOverride = false,
    bool IsMove = false);

/// <summary>One condition of a rule. All conditions of a rule must hold (AND).</summary>
public abstract record RuleCondition
{
    public abstract bool Holds(RuleLine line);

    /// <summary>
    /// Build a condition from its stored form (<c>rule_conditions.field</c>, <c>op</c>, JSON <c>value</c>);
    /// see docs/06-rules-engine.md, "Conditions".
    /// </summary>
    public static RuleCondition From(string field, string op, string valueJson)
    {
        using var doc = JsonDocument.Parse(valueJson);
        var v = doc.RootElement;

        string[] Strings() => v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [v.GetString() ?? ""];

        return (field, op) switch
        {
            ("description", "contains_any") => new DescriptionContainsAny(Strings()),
            ("description", "equals_any") => new DescriptionEqualsAny(Strings()),
            ("description", "starts_with") => new DescriptionStartsWith(Strings().Single()),
            ("amount", "gte") => new AmountAtLeast(DollarsToCents(v)),
            ("amount", "lte") => new AmountAtMost(DollarsToCents(v)),
            ("account", "is") => new AccountIs(Strings().Single()),
            ("account_kind", "in") => new AccountKindIn(Strings().Select(ParseKind).ToArray()),
            ("direction", "is") => new DirectionIs(Strings().Single() switch
            {
                "in" => Direction.In,
                "out" => Direction.Out,
                var other => throw new ArgumentException($"Unknown direction '{other}'."),
            }),
            _ => throw new ArgumentException($"Unknown rule condition '{field}' '{op}'."),
        };
    }

    private static long DollarsToCents(JsonElement v) =>
        (long)Math.Round(v.GetDecimal() * 100m, MidpointRounding.AwayFromZero);

    private static AccountKind ParseKind(string text) => text switch
    {
        "checking" => AccountKind.Checking,
        "savings" => AccountKind.Savings,
        "credit_card" => AccountKind.CreditCard,
        "loan" => AccountKind.Loan,
        "asset" => AccountKind.Asset,
        "retirement" => AccountKind.Retirement,
        _ => throw new ArgumentException($"Unknown account kind '{text}'."),
    };
}

public enum Direction { In, Out }

public sealed record DescriptionContainsAny(IReadOnlyList<string> Values) : RuleCondition
{
    public override bool Holds(RuleLine line) =>
        Values.Any(v => line.Description.Contains(v, StringComparison.OrdinalIgnoreCase));
}

public sealed record DescriptionEqualsAny(IReadOnlyList<string> Values) : RuleCondition
{
    public override bool Holds(RuleLine line) => Values.Contains(line.Description.Trim());
}

public sealed record DescriptionStartsWith(string Prefix) : RuleCondition
{
    public override bool Holds(RuleLine line) => line.Description.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>abs(amount) >= value. Value is in cents here (the stored form is dollars).</summary>
public sealed record AmountAtLeast(long Cents) : RuleCondition
{
    public override bool Holds(RuleLine line) => Math.Abs(line.AmountCents) >= Cents;
}

public sealed record AmountAtMost(long Cents) : RuleCondition
{
    public override bool Holds(RuleLine line) => Math.Abs(line.AmountCents) <= Cents;
}

public sealed record AccountIs(string BankNumber) : RuleCondition
{
    public override bool Holds(RuleLine line) => line.AccountBankNumber == BankNumber;
}

public sealed record AccountKindIn(IReadOnlyList<AccountKind> Kinds) : RuleCondition
{
    public override bool Holds(RuleLine line) => Kinds.Contains(line.AccountKind);
}

public sealed record DirectionIs(Direction Direction) : RuleCondition
{
    public override bool Holds(RuleLine line) =>
        Direction == Direction.In ? line.AmountCents > 0 : line.AmountCents < 0;
}
