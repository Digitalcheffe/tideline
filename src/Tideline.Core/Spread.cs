namespace Tideline.Core;

/// <summary>How a line shows up in reports: income as it is, every other type as positive spend (docs/04, "Sign convention").</summary>
public static class Reporting
{
    public static long ReportCents(LineType type, long amountCents) => type == LineType.Income ? amountCents : -amountCents;

    /// <summary>+1 or -1: multiply the signed sum of lines of this type to get the number a screen shows.</summary>
    public static int ReportSign(LineType type) => type == LineType.Income ? 1 : -1;
}

/// <summary>The Overview spread. In = Bills + Required + Extra + One-time + Savings + Left over, to the cent.</summary>
public sealed record Spread(long InCents, long BillsCents, long RequiredCents, long ExtraCents, long OneTimeCents, long SavingsCents)
{
    public long OutCents => BillsCents + RequiredCents + ExtraCents + OneTimeCents + SavingsCents;
    public long LeftOverCents => InCents - OutCents;

    public long For(LineType type) => type switch
    {
        LineType.Income => InCents,
        LineType.Bill => BillsCents,
        LineType.Required => RequiredCents,
        LineType.Extra => ExtraCents,
        LineType.OneTime => OneTimeCents,
        LineType.Savings => SavingsCents,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static Spread From(IEnumerable<(LineType Type, long ReportCents)> totals)
    {
        long inC = 0, bills = 0, required = 0, extra = 0, oneTime = 0, savings = 0;
        foreach (var (type, cents) in totals)
        {
            switch (type)
            {
                case LineType.Income: inC += cents; break;
                case LineType.Bill: bills += cents; break;
                case LineType.Required: required += cents; break;
                case LineType.Extra: extra += cents; break;
                case LineType.OneTime: oneTime += cents; break;
                case LineType.Savings: savings += cents; break;
            }
        }
        return new Spread(inC, bills, required, extra, oneTime, savings);
    }
}
