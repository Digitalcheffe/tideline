using System.Globalization;

namespace Tideline.Core.Rules;

/// <summary>Payee when no rule names one (docs/05-import.md, "Payee"; reference: classify.payee_for).</summary>
public static class PayeeCleaner
{
    private static readonly string[] Prefixes =
        ["SQ *", "TST* ", "TST*", "SP ", "DD *DOORDASH ", "PP*", "FSP*", "PY *", "ACT*", "PHR*", "MED*", "CPP*", "ZEFFY* "];

    public static string For(string merchantName, string description)
    {
        if (merchantName.Length > 0 &&
            !merchantName.Equals("zelle", StringComparison.OrdinalIgnoreCase) &&
            !merchantName.Equals("bank of america", StringComparison.OrdinalIgnoreCase))
            return merchantName;

        var s = description;
        foreach (var p in Prefixes)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                s = s[p.Length..];
                break;
            }
        }

        var end = s.IndexOfAny(['#', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9']);
        var cut = (end < 0 ? s : s[..end]).Trim(' ', '-');
        var name = cut.Length > 0 ? cut : s;

        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        return titled.Length > 40 ? titled[..40] : titled;
    }
}
