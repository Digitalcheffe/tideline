namespace Tideline.Core;

/// <summary>Exactly one type per effective line. This is what makes the spread add up.</summary>
public enum LineType
{
    Income,
    Bill,
    Required,
    Extra,
    OneTime,
    Savings,
}

public static class LineTypeExtensions
{
    /// <summary>Database form: snake_case (<c>OneTime</c> becomes <c>one_time</c>).</summary>
    public static string ToDb(this LineType type) => type switch
    {
        LineType.Income => "income",
        LineType.Bill => "bill",
        LineType.Required => "required",
        LineType.Extra => "extra",
        LineType.OneTime => "one_time",
        LineType.Savings => "savings",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static LineType FromDb(string value) => value switch
    {
        "income" => LineType.Income,
        "bill" => LineType.Bill,
        "required" => LineType.Required,
        "extra" => LineType.Extra,
        "one_time" => LineType.OneTime,
        "savings" => LineType.Savings,
        _ => throw new ArgumentException($"Unknown line type '{value}'.", nameof(value)),
    };
}
