namespace Tideline.Core;

/// <summary>Subscriptions: spotting a recent price change (docs/ARCHITECTURE.md).</summary>
public static class PriceHistory
{
    /// <summary>
    /// If the latest charge differs from one of the few charges before it, returns the most recent different amount.
    /// Older changes are not news, so they are not flagged.
    /// </summary>
    /// <param name="charges">Charges oldest first, as positive spend.</param>
    /// <param name="lookBack">How many charges before the latest to compare with.</param>
    public static long? RecentChange(IReadOnlyList<long> charges, int lookBack = 3)
    {
        if (charges.Count < 2) return null;
        var last = charges[^1];
        for (var i = charges.Count - 2; i >= Math.Max(0, charges.Count - 1 - lookBack); i--)
            if (charges[i] != last) return charges[i];
        return null;
    }
}
