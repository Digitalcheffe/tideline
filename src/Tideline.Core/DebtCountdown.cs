namespace Tideline.Core;

/// <param name="PaysOff">False when the payment does not even cover the interest, so the balance never goes down.</param>
/// <param name="MonthsLeft">Fractional months until the balance reaches zero; null when it never does.</param>
/// <param name="PayoffMonth">'YYYY-MM' of the last payment, or null when the loan never pays off.</param>
public sealed record Payoff(bool PaysOff, double? MonthsLeft, long InterestLeftCents, string? PayoffMonth);

/// <param name="MonthsSaved">Null when the extra turns a loan that never pays off into one that does.</param>
public sealed record ExtraPaymentEffect(Payoff Without, Payoff With, double? MonthsSaved, long InterestSavedCents);

/// <summary>Loans (docs/04-domain-rules.md, "Debt countdown"). The payment is principal and interest only: escrow is not principal.</summary>
public static class DebtCountdown
{
    /// <summary>Monthly rate from basis points a year.</summary>
    public static double MonthlyRate(int rateBps) => rateBps / 10000.0 / 12.0;

    public static Payoff Compute(long balanceCents, int rateBps, long paymentCents, string thisMonth)
    {
        if (balanceCents <= 0) return new Payoff(true, 0, 0, thisMonth);
        if (paymentCents <= 0) return new Payoff(false, null, 0, null);

        var r = MonthlyRate(rateBps);
        double months;
        if (r == 0)
        {
            months = (double)balanceCents / paymentCents;
        }
        else
        {
            var interestOnly = balanceCents * r;
            if (paymentCents <= interestOnly) return new Payoff(false, null, 0, null);
            months = -Math.Log(1 - interestOnly / paymentCents) / Math.Log(1 + r);
        }

        var interest = (long)Math.Round(months * paymentCents - balanceCents, MidpointRounding.AwayFromZero);
        return new Payoff(true, months, interest, AddMonths(thisMonth, (int)Math.Ceiling(months - 1e-9)));
    }

    /// <summary>What paying <paramref name="extraCents"/> more each month does: months and interest saved.</summary>
    public static ExtraPaymentEffect WithExtra(long balanceCents, int rateBps, long paymentCents, long extraCents, string thisMonth)
    {
        var without = Compute(balanceCents, rateBps, paymentCents, thisMonth);
        var with = Compute(balanceCents, rateBps, paymentCents + extraCents, thisMonth);
        double? saved = without.PaysOff && with.PaysOff ? without.MonthsLeft - with.MonthsLeft : null;
        return new ExtraPaymentEffect(without, with, saved, without.PaysOff && with.PaysOff ? without.InterestLeftCents - with.InterestLeftCents : 0);
    }

    /// <summary>The balance after one more payment: B * (1 + r) - P, never below zero.</summary>
    public static long NextBalance(long balanceCents, int rateBps, long paymentCents) =>
        Math.Max(0, (long)Math.Round(balanceCents * (1 + MonthlyRate(rateBps)) - paymentCents, MidpointRounding.AwayFromZero));

    /// <summary>'YYYY-MM' plus whole months.</summary>
    public static string AddMonths(string month, int months)
    {
        var index = int.Parse(month[..4]) * 12 + int.Parse(month[5..7]) - 1 + months;
        return $"{Math.DivRem(index, 12, out var rest):0000}-{rest + 1:00}";
    }
}
