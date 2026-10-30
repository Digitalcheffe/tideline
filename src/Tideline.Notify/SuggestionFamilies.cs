using System.Data;
using System.Globalization;
using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Reports;

namespace Tideline.Notify;

/// <summary>
/// What every suggestion check shares: whether it is switched on, and a few questions about the ledger. A check that is off finds nothing, so any
/// card it made clears by itself. Checks only read; they never change a line.
/// </summary>
internal static class Ask
{
    public static bool IsOn(IDbConnection conn, string key) =>
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM recommendation_rules WHERE flag_kind = @key AND is_active = 1", new { key }) > 0;

    public static string Quarter(DateOnly d) => $"{d.Year}-Q{(d.Month - 1) / 3 + 1}";

    public static string YearText(int year) => year.ToString(CultureInfo.InvariantCulture);

    public static string Percent(long part, long whole) => $"{Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";

    /// <summary>Months of data the ledger has: the day of its oldest line, or null when it is empty.</summary>
    public static DateOnly? FirstLineOn(IDbConnection conn) =>
        conn.ExecuteScalar<string?>("SELECT MIN(posted_on) FROM transactions WHERE left_out_at IS NULL") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>At least a full month of lines, so a brand-new household is not nagged.</summary>
    public static bool HasAMonthOfData(IDbConnection conn, DateOnly today) => FirstLineOn(conn) is { } first && first.AddDays(30) <= today;

    public static IReadOnlyList<long> CategoryIds(IDbConnection conn, params string[] names) =>
        conn.Query<long>("SELECT id FROM categories WHERE LOWER(name) IN @names", new { names = names.Select(n => n.ToLowerInvariant()).ToArray() }).ToList();

    /// <summary>Money in and out of some categories over some days, from the lines the screens count (splits and left-out lines respected).</summary>
    public static (long InCents, long OutCents) CategoryFlow(IDbConnection conn, IReadOnlyCollection<long> categoryIds, DateOnly from, DateOnly to)
    {
        if (categoryIds.Count == 0) return (0, 0);
        var row = conn.QuerySingle<(long? In, long? Out)>($"""
            WITH {Tideline.Data.Reports.SuggestionSql.Ctes}
            SELECT SUM(CASE WHEN e.amount_cents > 0 THEN e.amount_cents ELSE 0 END), SUM(CASE WHEN e.amount_cents < 0 THEN -e.amount_cents ELSE 0 END)
            FROM effective e WHERE e.category_id IN @ids AND e.posted_on BETWEEN @from AND @to
            """, new { ids = categoryIds, from, to });
        return (row.In ?? 0, row.Out ?? 0);
    }

    /// <summary>What went into savings accounts in a month: transfers in and lines typed Savings. Interest the bank paid is not a deposit.</summary>
    public static long SavingsDeposits(IDbConnection conn, string month) =>
        conn.ExecuteScalar<long?>("""
            SELECT SUM(t.amount_cents) FROM transactions t JOIN accounts a ON a.id = t.account_id
            WHERE a.kind = 'savings' AND a.is_closed = 0 AND t.amount_cents > 0 AND t.left_out_at IS NULL
              AND (t.move_id IS NOT NULL OR t.type = 'savings') AND SUBSTR(t.posted_on, 1, 7) = @month
            """, new { month }) ?? 0;

    public static bool HasSavingsAccount(IDbConnection conn) =>
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE kind = 'savings' AND is_closed = 0") > 0;
}

/// <summary>The three tax suggestions: a big refund, a big payment at filing, and nothing seen at all. Prompts to look, never tax advice.</summary>
public sealed class TaxFlags : IFlagFamily
{
    public const long RefundFloorCents = 100_000;
    public const double RefundShareOfIncome = 0.02;
    public const long PaymentFloorCents = 100_000;

    public IReadOnlyList<string> Kinds => [FlagKinds.TaxOverpaid, FlagKinds.TaxUnderpaid, FlagKinds.TaxUnknown];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var taxYear = c.Today.Year - 1;
        var year = Ask.YearText(taxYear);
        var from = new DateOnly(taxYear + 1, 1, 1);
        var refundIds = Ask.CategoryIds(c.Conn, "Tax refund", "Tax refunds");
        var paidIds = Ask.CategoryIds(c.Conn, "Taxes");
        if (refundIds.Count == 0 && paidIds.Count == 0) yield break;

        // the lines land in the year after the tax year: the refund early in it, the payment by April
        var to = new DateOnly(taxYear + 1, 12, 31);
        var refund = Ask.CategoryFlow(c.Conn, refundIds.Concat(paidIds).Distinct().ToList(), from, to).InCents;
        var seasonEnd = new DateOnly(taxYear + 1, 4, 30);
        var paid = Ask.CategoryFlow(c.Conn, paidIds, from, seasonEnd).OutCents;
        var penalty = c.Conn.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM transactions WHERE amount_cents < 0 AND left_out_at IS NULL AND posted_on BETWEEN @from AND @seasonEnd
              AND (UPPER(original_description) LIKE '%PENALTY%' OR UPPER(original_description) LIKE '%UNDERPAYMENT%' OR UPPER(original_description) LIKE '%IRS INTEREST%')
            """, new { from, seasonEnd }) > 0;
        var subject = (paidIds.Concat(refundIds)).FirstOrDefault();

        if (Ask.IsOn(c.Conn, FlagKinds.TaxOverpaid) && refund > 0)
        {
            var income = new ReportRepository(c.Conn).SpreadFor(new DateOnly(taxYear, 1, 1), new DateOnly(taxYear, 12, 31)).InCents;
            var line = income > 0 ? Math.Min(RefundFloorCents, (long)Math.Round(income * RefundShareOfIncome, MidpointRounding.AwayFromZero)) : RefundFloorCents;
            if (refund >= line)
                yield return new FlagCandidate(FlagKinds.TaxOverpaid, "category", refundIds.FirstOrDefault(), year, Severity.Info,
                    FlagText.Join($"Your tax refund for {year} was {Text.Dollars(refund)}",
                        $"That is about {Text.Dollars(refund / 12)} a month you could have had in your paycheck. A big refund can mean more was withheld than needed."));
        }

        if (Ask.IsOn(c.Conn, FlagKinds.TaxUnderpaid) && (paid >= PaymentFloorCents || penalty))
            yield return new FlagCandidate(FlagKinds.TaxUnderpaid, "category", paidIds.FirstOrDefault(), year, Severity.Info,
                FlagText.Join(penalty && paid < PaymentFloorCents ? $"A tax penalty or interest showed up for {year}" : $"You paid {Text.Dollars(paid)} in taxes at filing for {year}",
                    "That can mean too little was withheld or paid in during the year."));

        // nothing at all for the last tax year, once the filing season is over
        if (Ask.IsOn(c.Conn, FlagKinds.TaxUnknown) && c.Today > seasonEnd && refund == 0 && paid == 0 && Ask.HasAMonthOfData(c.Conn, new DateOnly(taxYear + 1, 1, 1)))
            yield return new FlagCandidate(FlagKinds.TaxUnknown, "category", subject, year, Severity.Info,
                FlagText.Join($"No tax refund or payment seen for {year}", "Nothing was found in the Tax refund or Taxes categories."));
    }
}

/// <summary>Savings: nothing went in last month, or well under the usual. Two separate suggestions.</summary>
public sealed class SavingsFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.SavingsNone, FlagKinds.SavingsLow];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        if (!Ask.HasSavingsAccount(c.Conn)) yield break;
        var month = DebtCountdown.AddMonths(c.ThisMonth, -1);
        var usual = Enumerable.Range(2, 6).Select(i => Ask.SavingsDeposits(c.Conn, DebtCountdown.AddMonths(c.ThisMonth, -i))).ToList();
        var months = usual.Count(d => d > 0);
        var average = months == 0 ? 0 : usual.Sum() / usual.Count;
        if (months < 3 || average < 5_000) yield break; // not enough history to call anything "usual"

        var deposited = Ask.SavingsDeposits(c.Conn, month);
        if (deposited == 0 && Ask.IsOn(c.Conn, FlagKinds.SavingsNone))
            yield return new FlagCandidate(FlagKinds.SavingsNone, "account", 0, month, Severity.Info,
                FlagText.Join($"Nothing went into savings in {Text.Month(month)}", $"Your usual is about {Text.Dollars(average)} a month."));
        else if (deposited > 0 && deposited * 2 < average && Ask.IsOn(c.Conn, FlagKinds.SavingsLow))
            yield return new FlagCandidate(FlagKinds.SavingsLow, "account", 0, month, Severity.Info,
                FlagText.Join($"{Text.Dollars(deposited)} went into savings in {Text.Month(month)}", $"That is less than half your usual of about {Text.Dollars(average)} a month."));
    }
}

/// <summary>
/// 50/30/20 on take-home pay: needs are Bills + Required, wants are Extra + One-time. Each measure has a monthly and a yearly suggestion, switched
/// separately. They stay quiet when no income was seen.
/// </summary>
public sealed class CashflowFlags : IFlagFamily
{
    public const double NeedsAbove = 0.60;
    public const double WantsAbove = 0.30;
    public const double SavingsBelow = 0.20;

    public IReadOnlyList<string> Kinds =>
    [
        FlagKinds.NeedsHighMonth, FlagKinds.NeedsHighYear, FlagKinds.WantsHighMonth, FlagKinds.WantsHighYear,
        FlagKinds.SavingsShareLowMonth, FlagKinds.SavingsShareLowYear, FlagKinds.NoSavingsAccount, FlagKinds.EmergencyCushion,
    ];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var reports = new ReportRepository(c.Conn);
        var lastMonth = DebtCountdown.AddMonths(c.ThisMonth, -1);
        var lastYear = c.Today.Year - 1;

        foreach (var f in Shares(c, reports, EvalContext.FirstOf(lastMonth), EvalContext.LastOf(lastMonth), lastMonth, Text.Month(lastMonth), monthly: true)) yield return f;
        if (Ask.FirstLineOn(c.Conn) is { } first && first <= new DateOnly(lastYear, 7, 1))
            foreach (var f in Shares(c, reports, new DateOnly(lastYear, 1, 1), new DateOnly(lastYear, 12, 31), Ask.YearText(lastYear), Ask.YearText(lastYear), monthly: false)) yield return f;

        if (Ask.HasAMonthOfData(c.Conn, c.Today) && !Ask.HasSavingsAccount(c.Conn) && Ask.IsOn(c.Conn, FlagKinds.NoSavingsAccount))
            yield return new FlagCandidate(FlagKinds.NoSavingsAccount, "account", 0, Ask.YearText(c.Today.Year), Severity.Info,
                FlagText.Join("There is no savings account in Tideline", "If you have one, add it under Settings, Money accounts."));

        foreach (var f in Cushion(c, reports)) yield return f;
    }

    private static IEnumerable<FlagCandidate> Shares(EvalContext c, ReportRepository reports, DateOnly from, DateOnly to, string period, string label, bool monthly)
    {
        var s = reports.SpreadFor(from, to);
        if (s.InCents <= 0) yield break;
        var needs = s.BillsCents + s.RequiredCents;
        var wants = s.ExtraCents + s.OneTimeCents;
        var span = monthly ? "month" : "year";

        var needsKey = monthly ? FlagKinds.NeedsHighMonth : FlagKinds.NeedsHighYear;
        if (needs > s.InCents * NeedsAbove && Ask.IsOn(c.Conn, needsKey))
            yield return new FlagCandidate(needsKey, "account", 0, period, Severity.Info,
                FlagText.Join($"Bills and required costs were {Ask.Percent(needs, s.InCents)} of income in {label}",
                    $"{Text.Dollars(needs)} of the {Text.Dollars(s.InCents)} that came in. The 50/30/20 guide puts needs at 50%, and a common fixed-cost range is 50 to 60%."));

        var wantsKey = monthly ? FlagKinds.WantsHighMonth : FlagKinds.WantsHighYear;
        if (wants > s.InCents * WantsAbove && Ask.IsOn(c.Conn, wantsKey))
            yield return new FlagCandidate(wantsKey, "account", 0, period, Severity.Info,
                FlagText.Join($"Extra and one-time spending was {Ask.Percent(wants, s.InCents)} of income in {label}",
                    $"{Text.Dollars(wants)} of the {Text.Dollars(s.InCents)} that came in. The 50/30/20 guide puts wants at 30%."));

        var savingsKey = monthly ? FlagKinds.SavingsShareLowMonth : FlagKinds.SavingsShareLowYear;
        if (s.SavingsCents < s.InCents * SavingsBelow && Ask.IsOn(c.Conn, savingsKey))
            yield return new FlagCandidate(savingsKey, "account", 0, period, Severity.Info,
                FlagText.Join($"Savings were {Ask.Percent(Math.Max(0, s.SavingsCents), s.InCents)} of income in {label}",
                    $"{Text.Dollars(s.SavingsCents)} of the {Text.Dollars(s.InCents)} that came in. The 50/30/20 guide puts savings and debt payments at 20% of the {span}."));
    }

    /// <summary>Months of bills and required costs the savings accounts would cover. Only when a savings account is listed and its balance is known.</summary>
    private static IEnumerable<FlagCandidate> Cushion(EvalContext c, ReportRepository reports)
    {
        if (!Ask.IsOn(c.Conn, FlagKinds.EmergencyCushion)) yield break;
        var accounts = c.Conn.Query<(long Id, long? Opening)>("SELECT id, opening_balance_cents FROM accounts WHERE kind = 'savings' AND is_closed = 0").ToList();
        if (accounts.Count == 0 || accounts.All(a => a.Opening is null)) yield break;

        var ledger = new LedgerRepository(c.Conn, c.Clock);
        var balance = accounts.Sum(a => ledger.BalanceAsOf(a.Id, c.Today));
        var from = EvalContext.FirstOf(DebtCountdown.AddMonths(c.ThisMonth, -3));
        var to = EvalContext.LastOf(DebtCountdown.AddMonths(c.ThisMonth, -1));
        var s = reports.SpreadFor(from, to);
        var monthlyNeeds = (s.BillsCents + s.RequiredCents) / 3;
        if (monthlyNeeds <= 0) yield break;

        var months = balance / (double)monthlyNeeds;
        if (months >= 3) yield break;
        yield return new FlagCandidate(FlagKinds.EmergencyCushion, "account", 0, Ask.Quarter(c.Today), Severity.Info,
            FlagText.Join($"Your savings cover about {Math.Max(0, months).ToString("0.0", CultureInfo.InvariantCulture)} months of bills and required costs",
                $"{Text.Dollars(balance)} against about {Text.Dollars(monthlyNeeds)} a month. Many guides suggest 3 to 6 months of essential expenses."));
    }
}

/// <summary>Subscriptions: a review every quarter, and a nudge when there are more than ten. Two separate suggestions.</summary>
public sealed class SubscriptionFlags : IFlagFamily
{
    public const int Many = 10;

    public IReadOnlyList<string> Kinds => [FlagKinds.SubscriptionsReview, FlagKinds.SubscriptionsMany];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var subs = new RecurringRepository(c.Conn).All()
            .Where(r => r.Item.IsActive && r.CategoryType is not LineType.Bill and not LineType.Income)
            .Select(r =>
            {
                var cost = (r.Item.ExpectedMinCents + r.Item.ExpectedMaxCents) / 2;
                return r.Item.Frequency == Frequency.Yearly ? cost / 12 : cost / Math.Max(1, r.Item.EveryN);
            }).ToList();
        if (subs.Count == 0) yield break;

        var monthly = subs.Sum();
        var quarter = Ask.Quarter(c.Today);
        var cost = $"about {Text.Dollars(monthly)} a month ({Text.Dollars(monthly * 12)} a year)";

        if (Ask.IsOn(c.Conn, FlagKinds.SubscriptionsReview))
            yield return new FlagCandidate(FlagKinds.SubscriptionsReview, "account", 0, quarter, Severity.Info,
                FlagText.Join($"You have {subs.Count} subscription{(subs.Count == 1 ? "" : "s")}", $"That is {cost}."));
        if (subs.Count > Many && Ask.IsOn(c.Conn, FlagKinds.SubscriptionsMany))
            yield return new FlagCandidate(FlagKinds.SubscriptionsMany, "account", 0, quarter, Severity.Info,
                FlagText.Join($"You are paying for {subs.Count} subscriptions", $"That is {cost}."));
    }
}
