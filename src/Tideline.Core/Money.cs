namespace Tideline.Core;

/// <summary>Integer cents. Sign is the account holder's view: positive in, negative out.</summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    public static Money Zero => new(0);

    public Money Abs() => new(Math.Abs(Cents));

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));
    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));
    public static Money operator -(Money a) => new(checked(-a.Cents));

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);
    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;
    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;

    /// <summary>US dollars with cents and thousands separators: $1,234.56 or -$45.12.</summary>
    public override string ToString()
    {
        var abs = Cents < 0 ? (ulong)(-(Cents + 1)) + 1UL : (ulong)Cents;
        var dollars = (abs / 100).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var cents = (abs % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return $"{(Cents < 0 ? "-" : "")}${dollars}.{cents}";
    }
}

public static class MoneyExtensions
{
    public static Money Sum(this IEnumerable<Money> source)
    {
        var total = Money.Zero;
        foreach (var m in source) total += m;
        return total;
    }
}
