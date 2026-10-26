using System.Globalization;
using System.Text;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;
using Tideline.Data.Reports;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Notify;

internal static class Text
{
    public static string Dollars(long cents) => new Money(cents).ToString();

    public static string Month(string yyyyMm) => EvalContext.FirstOf(yyyyMm).ToString("MMM yyyy", CultureInfo.InvariantCulture);

    public static string Day(DateOnly d) => d.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>A number that is the same on every run for the same text, to stand in for a payee in a flag's subject.</summary>
    public static long Stable(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToUpperInvariant())) hash = (hash ^ b) * 1099511628211UL;
        return (long)(hash & 0x7FFFFFFFFFFFFFFF);
    }
}

/// <summary>A bill or recurring item not paid, or paid for an unusual amount (docs/ARCHITECTURE.md).</summary>
public sealed class RecurringFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.RecurringMissing, FlagKinds.RecurringOutOfRange];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var months = c.RecentMonths;
        var from = EvalContext.FirstOf(DebtCountdown.AddMonths(months[0], -3));
        var paid = new ReportRepository(c.Conn).RecurringPaid(from, c.Today).ToLookup(p => p.RecurringId);

        foreach (var r in new RecurringRepository(c.Conn).All())
        {
            var byMonth = paid[r.Item.Id].ToDictionary(p => p.Month, p => p.Cents);
            foreach (var month in months)
            {
                var year = int.Parse(month[..4]);
                var m = int.Parse(month[5..7]);
                var history = byMonth.Where(kv => string.CompareOrdinal(kv.Key, month) < 0).OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                var cell = RecurringStatus.Evaluate(r.Item, year, m, byMonth.GetValueOrDefault(month), c.Today, history);
                var due = RecurringStatus.DueDate(r.Item, year, m);

                if (cell.Status == CellStatus.Missing)
                    yield return new FlagCandidate(FlagKinds.RecurringMissing, "recurring", r.Item.Id, month, r.CategoryType == LineType.Bill ? Severity.Alert : Severity.Warn,
                        FlagText.Join($"{r.Item.Name} not paid for {Text.Month(month)}",
                            $"Expected {Text.Dollars(r.Item.ExpectedMinCents)} to {Text.Dollars(r.Item.ExpectedMaxCents)} by {Text.Day(due)}. Nothing linked yet."));
                else if (cell.Status == CellStatus.PaidUnusual)
                    yield return new FlagCandidate(FlagKinds.RecurringOutOfRange, "recurring", r.Item.Id, month, Severity.Warn,
                        FlagText.Join($"{r.Item.Name} was an unusual amount for {Text.Month(month)}",
                            $"Paid {Text.Dollars(cell.PaidCents)}; expected {Text.Dollars(r.Item.ExpectedMinCents)} to {Text.Dollars(r.Item.ExpectedMaxCents)}."));
            }
        }
    }
}

/// <summary>Cards that should be paid in full, and interest or fees showing up on a card (docs/ARCHITECTURE.md).</summary>
public sealed class CardFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.CardNotPaidInFull, FlagKinds.CardInterest];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var reports = new ReportRepository(c.Conn);
        var ledger = new LedgerRepository(c.Conn, c.Clock);
        var terms = new CardTermsRepository(c.Conn).All();
        var cards = c.Conn.Query<(long Id, string Name)>("SELECT id, name FROM accounts WHERE kind = 'credit_card' AND is_closed = 0").ToList();

        foreach (var (id, name) in cards)
        {
            if (terms.TryGetValue(id, out var t) && t.IsPayInFull)
                foreach (var month in c.RecentMonths)
                {
                    var year = int.Parse(month[..4]);
                    var m = int.Parse(month[5..7]);
                    var last = EvalContext.LastOf(month);
                    if (last >= c.Today) continue;
                    var deadline = CardCheck.PaymentDeadline(year, m, t.DueDay ?? 31);
                    if (deadline > c.Today) continue;

                    var balance = ledger.BalanceAsOf(id, last);
                    var paid = reports.CardPayments(id, last.AddDays(1), deadline);
                    if (!CardCheck.PaidInFull(balance, paid))
                        yield return new FlagCandidate(FlagKinds.CardNotPaidInFull, "account", id, month, Severity.Alert,
                            FlagText.Join($"{name} was not paid in full for {Text.Month(month)}",
                                $"It owed {Text.Dollars(-balance)} at month end and {Text.Dollars(paid)} was paid by {Text.Day(deadline)}."));
                }
        }

        if (c.CardInterestCategoryId is not { } interest) yield break;
        var since = EvalContext.FirstOf(c.RecentMonths[0]);
        var lines = c.Conn.Query<(long Id, string Account, DateOnly On, long Cents)>("""
            SELECT t.id, a.name, t.posted_on, t.amount_cents FROM transactions t JOIN accounts a ON a.id = t.account_id
            WHERE a.kind = 'credit_card' AND t.category_id = @interest AND t.amount_cents < 0 AND t.move_id IS NULL AND t.left_out_at IS NULL AND t.posted_on >= @since
            """, new { interest, since });
        foreach (var l in lines)
            yield return new FlagCandidate(FlagKinds.CardInterest, "transaction", l.Id, l.On.ToString("yyyy-MM"), Severity.Alert,
                FlagText.Join($"Interest or fees on {l.Account}", $"{Text.Dollars(-l.Cents)} on {Text.Day(l.On)}. A sign of carrying a balance."));
    }
}

/// <summary>The bank's last balance against what the ledger adds up to (docs/ARCHITECTURE.md).</summary>
public sealed class BalanceFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.BalanceMismatch];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var ledger = new LedgerRepository(c.Conn, c.Clock);
        var latest = c.Conn.Query<(long Id, string Name, DateOnly On, long Bank)>("""
            SELECT a.id, a.name, s.as_of_on, s.balance_cents FROM accounts a JOIN balance_snapshots s ON s.account_id = a.id AND s.source = 'bank'
            WHERE s.as_of_on = (SELECT MAX(as_of_on) FROM balance_snapshots x WHERE x.account_id = a.id AND x.source = 'bank')
            """).ToList();

        foreach (var (id, name, on, bank) in latest)
        {
            var mine = ledger.BalanceAsOf(id, on);
            if (mine != bank)
                yield return new FlagCandidate(FlagKinds.BalanceMismatch, "account", id, on.ToString("yyyy-MM-dd"), Severity.Alert,
                    FlagText.Join($"{name} does not match the bank on {Text.Day(on)}",
                        $"The bank says {Text.Dollars(bank)} and the ledger says {Text.Dollars(mine)}, a difference of {Text.Dollars(Math.Abs(mine - bank))}. A missing file or line is the usual cause."));
        }
    }
}

/// <summary>A file or sync that failed and has not been followed by one that worked.</summary>
public sealed class ImportFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.ImportFailed];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var since = c.Today.AddDays(-30);
        var failed = c.Conn.Query<(long Id, string? File, string? Error, DateOnly On)>("""
            SELECT b.id, b.file_name, b.error, SUBSTR(b.started_at, 1, 10) FROM import_batches b
            WHERE b.status = 'failed' AND b.dismissed_at IS NULL AND SUBSTR(b.started_at, 1, 10) >= @since
              AND NOT EXISTS (SELECT 1 FROM import_batches ok WHERE ok.status = 'done' AND ok.id > b.id AND COALESCE(ok.file_name, '') = COALESCE(b.file_name, ''))
            """, new { since });
        foreach (var f in failed)
            yield return new FlagCandidate(FlagKinds.ImportFailed, "import", f.Id, f.On.ToString("yyyy-MM-dd"), Severity.Alert,
                FlagText.Join($"{f.File ?? "An import"} could not be imported", f.Error ?? "No reason was recorded."));
    }
}

/// <summary>One half of a transfer with nothing on the other side after a week.</summary>
public sealed class MoveFlags : IFlagFamily
{
    public const int WaitDays = 7;

    public IReadOnlyList<string> Kinds => [FlagKinds.MoveUnpaired];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var cutoff = c.Today.AddDays(-WaitDays);
        var lonely = c.Conn.Query<(long Id, string Account, DateOnly On, long Cents)>("""
            SELECT t.id, a.name, t.posted_on, t.amount_cents FROM transactions t JOIN accounts a ON a.id = t.account_id
            WHERE t.move_id IS NOT NULL AND t.posted_on <= @cutoff AND t.posted_on >= @since
              AND (SELECT COUNT(*) FROM transactions x WHERE x.move_id = t.move_id) = 1
            """, new { cutoff, since = cutoff.AddDays(-60) });
        foreach (var l in lonely)
            yield return new FlagCandidate(FlagKinds.MoveUnpaired, "transaction", l.Id, l.On.ToString("yyyy-MM"), Severity.Warn,
                FlagText.Join($"A transfer on {l.Account} has no other side", $"{Text.Dollars(Math.Abs(l.Cents))} on {Text.Day(l.On)}. The matching line has not shown up in the other account."));
    }
}

/// <summary>A payment that looks like a loan Tideline does not know about (docs/ARCHITECTURE.md).</summary>
public sealed class DebtFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.NewDebt];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var words = c.DebtWords;
        var known = c.Conn.Query<long>("SELECT payment_category_id FROM loan_terms WHERE payment_category_id IS NOT NULL").ToHashSet();
        var since = c.Today.AddDays(-45);
        var lines = c.Conn.Query<(long Id, string Description, DateOnly On, long Cents, long? Category)>("""
            SELECT id, original_description, posted_on, amount_cents, category_id FROM transactions
            WHERE amount_cents < 0 AND move_id IS NULL AND left_out_at IS NULL AND posted_on >= @since
            """, new { since });
        foreach (var l in lines)
        {
            if (l.Category is { } cat && known.Contains(cat)) continue;
            var hit = words.FirstOrDefault(w => l.Description.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;
            yield return new FlagCandidate(FlagKinds.NewDebt, "transaction", l.Id, l.On.ToString("yyyy-MM"), Severity.Alert,
                FlagText.Join("A payment that looks like a new loan", $"{Text.Dollars(-l.Cents)} on {Text.Day(l.On)}: {l.Description}. It contains \"{hit}\" and is not a payment on a loan you have entered."));
        }
    }
}

/// <summary>A payee charging about the same amount in two of the last three months that is not a recurring item yet.</summary>
public sealed class NewRecurringFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.NewRecurring];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        // the last three whole months, and the six before them
        var recentFirst = DebtCountdown.AddMonths(c.ThisMonth, -3);
        var recent = new[] { recentFirst, DebtCountdown.AddMonths(c.ThisMonth, -2), DebtCountdown.AddMonths(c.ThisMonth, -1) };
        var from = EvalContext.FirstOf(DebtCountdown.AddMonths(recentFirst, -6));
        var to = EvalContext.LastOf(recent[2]);

        var lines = c.Conn.Query<(string Payee, string Month, long Cents)>("""
            SELECT COALESCE(NULLIF(payee, ''), original_description), SUBSTR(posted_on, 1, 7), -amount_cents FROM transactions
            WHERE amount_cents < 0 AND move_id IS NULL AND left_out_at IS NULL AND recurring_id IS NULL AND posted_on BETWEEN @from AND @to AND type <> 'income'
            """, new { from, to }).ToList();

        foreach (var g in lines.GroupBy(l => l.Payee.ToUpperInvariant()))
        {
            var inRecent = g.Where(l => recent.Contains(l.Month)).ToList();
            var months = inRecent.Select(l => l.Month).Distinct().ToList();
            if (months.Count < 2) continue;
            if (g.Any(l => string.CompareOrdinal(l.Month, recentFirst) < 0)) continue; // charged before: not new

            var amounts = months.Select(m => inRecent.Where(l => l.Month == m).Sum(l => l.Cents)).ToList();
            if (amounts.Max() > amounts.Min() * 1.10m) continue;

            var payee = g.First().Payee;
            yield return new FlagCandidate(FlagKinds.NewRecurring, "payee", Text.Stable(payee) % 1_000_000_000, c.ThisMonth, Severity.Info,
                FlagText.Join($"{payee} looks like a new recurring charge", $"About {Text.Dollars(amounts.Max())} in {months.Count} of the last 3 months. Make it a recurring item, or acknowledge it."));
        }
    }
}

/// <summary>Spending over a guideline, and income behind the year's pace (docs/ARCHITECTURE.md).</summary>
public sealed class BudgetFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.BudgetPop, FlagKinds.IncomeShort];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var view = new BudgetReader(c.Conn).Read(c.Today.Year, c.Today);
        if (!view.Saved) yield break;

        foreach (var row in view.Categories.Where(r => r.Popped))
        {
            var monthly = row.MonthlyCents is not null;
            yield return new FlagCandidate(FlagKinds.BudgetPop, "category", row.CategoryId, monthly ? view.Month : view.Year.ToString(CultureInfo.InvariantCulture), Severity.Warn,
                FlagText.Join($"{row.Name} is over its guideline",
                    monthly ? $"{Text.Dollars(row.ThisMonthCents)} this month against {Text.Dollars(row.MonthlyCents!.Value)}."
                            : $"{Text.Dollars(row.YtdCents)} so far this year against {Text.Dollars(row.AnnualCents ?? 0)}."));
        }

        if (view.Income.Short)
            yield return new FlagCandidate(FlagKinds.IncomeShort, "budget", view.Year, view.Month, Severity.Info,
                FlagText.Join("Income is behind the year's pace",
                    $"{Text.Dollars(view.Income.ActualYtdCents)} so far; the budget expects {Text.Dollars(view.Income.ExpectedYtdCents ?? 0)} by now."));
    }
}

/// <summary>Trackable plans that have slipped (docs/ARCHITECTURE.md).</summary>
public sealed class PlanFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.PlanWavered, FlagKinds.PlanBehind];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        foreach (var p in new PlanReader(c.Conn).Read(c.Today).Where(p => p.IsTrackable && p.Lifecycle == "active"))
        {
            if (p.Status is not ("wavered" or "behind")) continue;
            var behind = p.Status == "behind";
            yield return new FlagCandidate(behind ? FlagKinds.PlanBehind : FlagKinds.PlanWavered, "plan", p.Id, c.ThisMonth, behind ? Severity.Alert : Severity.Warn,
                FlagText.Join($"{p.Name} is {(behind ? "behind" : "a little behind")}",
                    $"It should be at {Text.Dollars(p.ExpectedCents)} by now and is at {Text.Dollars(p.ActualCents)}. {Text.Dollars(p.NeededPerMonthCents)} a month finishes on time."));
        }
    }
}

/// <summary>Large one-time items to look at; one linked to a plan was planned, so it is acknowledged on sight.</summary>
public sealed class OneTimeFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.OneTimeReview];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var threshold = SetupEditor.OneTimeAttentionCents(c.Conn);
        var since = c.Today.AddDays(-90);
        var lines = c.Conn.Query<(long Id, string Description, DateOnly On, long Cents, string? PlanName)>("""
            SELECT t.id, COALESCE(NULLIF(t.payee, ''), t.original_description), t.posted_on, -t.amount_cents, p.name
            FROM transactions t LEFT JOIN plans p ON p.id = t.plan_id
            WHERE t.type = 'one_time' AND t.move_id IS NULL AND t.left_out_at IS NULL AND t.amount_cents < 0 AND -t.amount_cents >= @threshold AND t.posted_on >= @since
            """, new { threshold, since });
        foreach (var l in lines)
            yield return new FlagCandidate(FlagKinds.OneTimeReview, "transaction", l.Id, l.On.ToString("yyyy-MM"), Severity.Info,
                FlagText.Join($"A large one-time item: {l.Description}", $"{Text.Dollars(l.Cents)} on {Text.Day(l.On)}."), l.PlanName is null ? null : $"planned: {l.PlanName}");
    }
}

/// <summary>Lines tagged for review, as one count for the month (they are told in the monthly summary, not one by one).</summary>
public sealed class ReviewFlags : IFlagFamily
{
    public IReadOnlyList<string> Kinds => [FlagKinds.ReviewNeeded];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var count = c.Conn.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id WHERE g.name = 'review'
            """);
        if (count > 0)
            yield return new FlagCandidate(FlagKinds.ReviewNeeded, "review", 0, c.ThisMonth, Severity.Info,
                FlagText.Join($"{count} line{(count == 1 ? "" : "s")} tagged review", "Lines the rules could not place, waiting for a look in the Ledger."));
    }
}

/// <summary>The retirement shortfall getting bigger than it was last month (docs/ARCHITECTURE.md).</summary>
public sealed class RetirementFlags : IFlagFamily
{
    public const long MinGrowthCents = 50_000;

    public IReadOnlyList<string> Kinds => [FlagKinds.RetirementGapGrew];

    public IEnumerable<FlagCandidate> Evaluate(EvalContext c)
    {
        var result = new RetirementReader(c.Conn).Read(c.Today).Result;
        if (result is null) yield break;

        var settings = new SettingsRepository(c.Conn, c.Clock);
        var key = $"retirement_gap:{c.ThisMonth}";
        var previousKey = $"retirement_gap:{DebtCountdown.AddMonths(c.ThisMonth, -1)}";
        var now = Math.Max(0, result.DeficitTodayCents);

        if (settings.Get(key) is null) settings.Set(key, now.ToString(CultureInfo.InvariantCulture));
        var thisMonth = long.Parse(settings.Get(key)!, CultureInfo.InvariantCulture);

        if (long.TryParse(settings.Get(previousKey), out var before) && thisMonth - before >= MinGrowthCents && thisMonth > before * 101 / 100)
            yield return new FlagCandidate(FlagKinds.RetirementGapGrew, "household", 1, c.ThisMonth, Severity.Info,
                FlagText.Join("The retirement shortfall grew",
                    $"It is {Text.Dollars(thisMonth)} in today's dollars, up from {Text.Dollars(before)} last month."));
    }
}
