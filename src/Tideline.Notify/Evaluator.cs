using System.Data;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;

namespace Tideline.Notify;

/// <summary>What the evaluators look at: the database, today in the household's time zone, and the few settings that steer them.</summary>
public sealed class EvalContext(IDbConnection conn, DateOnly today, TimeProvider clock)
{
    public const string CardInterestCategoryKey = "card_interest_category_id";
    public const string DebtWordsKey = "debt_words";

    public static readonly IReadOnlyList<string> DefaultDebtWords =
        ["LOAN", "FINANCING", "FINANCE CO", "AFFIRM", "KLARNA", "AFTERPAY", "SYNCHRONY", "CREDIT ONE", "CAPITAL ONE", "LENDING"];

    public IDbConnection Conn => conn;
    public DateOnly Today => today;
    public TimeProvider Clock => clock;
    public string ThisMonth => today.ToString("yyyy-MM");

    public long? CardInterestCategoryId => long.TryParse(new SettingsRepository(conn, clock).Get(CardInterestCategoryKey), out var id) ? id : null;

    public IReadOnlyList<string> DebtWords
    {
        get
        {
            var raw = new SettingsRepository(conn, clock).Get(DebtWordsKey);
            var words = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToUpperInvariant()).ToList();
            return words.Count > 0 ? words : DefaultDebtWords;
        }
    }

    /// <summary>The three months that count: this one and the two before it, as 'YYYY-MM'.</summary>
    public IReadOnlyList<string> RecentMonths => [DebtCountdown.AddMonths(ThisMonth, -2), DebtCountdown.AddMonths(ThisMonth, -1), ThisMonth];

    public static DateOnly FirstOf(string month) => new(int.Parse(month[..4]), int.Parse(month[5..7]), 1);
    public static DateOnly LastOf(string month) => FirstOf(month).AddMonths(1).AddDays(-1);
}

/// <summary>One rule family from docs/ARCHITECTURE.md. Each says what is true now; the evaluator does the rest.</summary>
public interface IFlagFamily
{
    IReadOnlyList<string> Kinds { get; }
    IEnumerable<FlagCandidate> Evaluate(EvalContext context);
}

public sealed record EvaluationResult(int Found, int Created, int Resolved);

/// <summary>
/// Runs every family, adds the flags it finds (idempotent), and resolves the ones whose condition has cleared
/// (docs/ARCHITECTURE.md). What it creates is handed to the notifier by the host.
/// </summary>
public sealed class Evaluator(IEnumerable<IFlagFamily> families)
{
    public static IReadOnlyList<IFlagFamily> StandardFamilies { get; } =
    [
        new RecurringFlags(), new CardFlags(), new BalanceFlags(), new ImportFlags(), new MoveFlags(), new DebtFlags(), new NewRecurringFlags(),
        new BudgetFlags(), new PlanFlags(), new OneTimeFlags(), new ReviewFlags(), new CheckFlags(), new RetirementFlags(),
        new TaxFlags(), new SavingsFlags(), new CashflowFlags(), new SubscriptionFlags(), new LargePaymentFlags(), new BillCoverageFlags(),
    ];

    public Evaluator() : this(StandardFamilies) { }

    public EvaluationResult Run(IDbConnection conn, DateOnly today, TimeProvider clock)
    {
        TypeHandlers.EnsureRegistered();
        var context = new EvalContext(conn, today, clock);
        int found = 0, created = 0, resolved = 0;

        foreach (var family in families)
        {
            var candidates = family.Evaluate(context).ToList(); // read first; the writes then go in one transaction
            using var tx = conn.BeginTransaction();
            var flags = new FlagRepository(conn, clock, tx);
            var keys = new HashSet<string>();
            foreach (var c in candidates)
            {
                if (!keys.Add(FlagRepository.KeyOf(c.Kind, c.SubjectType, c.SubjectId, c.Period))) continue;
                found++;
                if (flags.Upsert(c).Created) created++;
            }
            resolved += flags.ResolveNotIn(family.Kinds.ToList(), keys);
            tx.Commit();
        }
        return new EvaluationResult(found, created, resolved);
    }
}
