using System.Security.Cryptography;
using System.Text;

namespace Tideline.Import.Truist;

public enum TruistFormat { Deposit, Card }

/// <summary>One parsed Truist line. Amounts are cents from the account holder's view: positive in, negative out.</summary>
public sealed record TruistRow(
    string BankNumber,
    DateOnly PostedOn,
    string Description,
    long AmountCents,
    string Merchant,
    string TruistCategory,
    long? BankBalanceCents,
    string CheckNumber,
    string SourceFile,
    int Occurrence,
    string RawJson,
    string? ExternalIdOverride = null)
{
    /// <summary>A Truist row has the id worked out from its content; a row from another door brings its own.</summary>
    public string ExternalId => ExternalIdOverride ?? ExternalIds.Truist(BankNumber, PostedOn, Description, AmountCents, Occurrence);
}

public static class ExternalIds
{
    /// <summary>
    /// <c>"truist:" + hex(SHA1("{bank}|{yyyy-MM-dd}|{description}|{amount 0.00}|{occurrence}"))[0..20]</c>
    /// (docs/ARCHITECTURE.md). Changing this orphans every stored row, so the vectors are tested.
    /// </summary>
    public static string Truist(string bankNumber, DateOnly postedOn, string description, long amountCents, int occurrence)
    {
        var amount = (amountCents / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"{bankNumber}|{postedOn:yyyy-MM-dd}|{description}|{amount}|{occurrence}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return "truist:" + Convert.ToHexStringLower(hash)[..20];
    }

    public static string SimpleFin(string connectionId, string accountId, string transactionId) =>
        $"sfin:{connectionId}:{accountId}:{transactionId}";

    public static string Manual() => "manual:" + Guid.NewGuid().ToString("N");
}
