namespace Tideline.Core;

public enum Severity { Info, Warn, Alert }

public enum NotifyMode { Immediate, Summary, Off }

/// <summary>The complete list of flags (docs/ARCHITECTURE.md) and how each is sent unless a person chooses otherwise.</summary>
public static class FlagKinds
{
    public const string RecurringMissing = "recurring_missing";
    public const string RecurringOutOfRange = "recurring_out_of_range";
    public const string CardNotPaidInFull = "card_not_paid_in_full";
    public const string CardInterest = "card_interest";
    public const string BalanceMismatch = "balance_mismatch";
    public const string ImportFailed = "import_failed";
    public const string MoveUnpaired = "move_unpaired";
    public const string NewDebt = "new_debt";
    public const string NewRecurring = "new_recurring";
    public const string BudgetPop = "budget_pop";
    public const string IncomeShort = "income_short";
    public const string PlanWavered = "plan_wavered";
    public const string PlanBehind = "plan_behind";
    public const string OneTimeReview = "one_time_review";
    public const string ReviewNeeded = "review_needed";
    public const string RetirementGapGrew = "retirement_gap_grew";
    public const string CheckUnknown = "check_unknown";

    // cards the suggestions raise themselves (see SuggestionCatalog)
    public const string TaxOverpaid = "tax_overpaid";
    public const string TaxUnderpaid = "tax_underpaid";
    public const string TaxUnknown = "tax_unknown";
    public const string SavingsNone = "savings_none";
    public const string SavingsLow = "savings_low";
    public const string NeedsHighMonth = "needs_high_month";
    public const string NeedsHighYear = "needs_high_year";
    public const string WantsHighMonth = "wants_high_month";
    public const string WantsHighYear = "wants_high_year";
    public const string SavingsShareLowMonth = "savings_share_low_month";
    public const string SavingsShareLowYear = "savings_share_low_year";
    public const string NoSavingsAccount = "no_savings_account";
    public const string EmergencyCushion = "emergency_cushion";
    public const string SubscriptionsReview = "subscriptions_review";
    public const string SubscriptionsMany = "subscriptions_many";

    /// <summary>The alerts that were built in first.</summary>
    public static readonly IReadOnlyList<string> Alerts =
    [
        RecurringMissing, RecurringOutOfRange, CardNotPaidInFull, CardInterest, BalanceMismatch, ImportFailed, MoveUnpaired, NewDebt, NewRecurring,
        BudgetPop, IncomeShort, PlanWavered, PlanBehind, OneTimeReview, ReviewNeeded, RetirementGapGrew, CheckUnknown,
    ];

    /// <summary>Every kind of flag: the alerts, and the cards the suggestions in <see cref="SuggestionCatalog"/> raise themselves.</summary>
    public static readonly IReadOnlyList<string> All = [.. Alerts, .. SuggestionCatalog.OwnKinds];

    private static readonly HashSet<string> ImmediateByDefault =
    [
        RecurringMissing, CardNotPaidInFull, CardInterest, BalanceMismatch, ImportFailed, NewDebt, PlanBehind,
    ];

    /// <summary>A suggestion that raises its own card stays in the app until a person turns its message on.</summary>
    public static NotifyMode DefaultMode(string kind) =>
        ImmediateByDefault.Contains(kind) ? NotifyMode.Immediate
        : SuggestionCatalog.Find(kind) is { IsAlertAdvice: false } ? NotifyMode.Off
        : NotifyMode.Summary;

    /// <summary>What a person reads for the kind, in plain words, for the notification settings.</summary>
    public static string Describe(string kind) => kind switch
    {
        RecurringMissing => "A bill or recurring item has not been paid",
        RecurringOutOfRange => "A recurring item was paid, but for an unusual amount",
        CardNotPaidInFull => "A card that should be paid in full was not",
        CardInterest => "Interest or fees were charged on a card",
        BalanceMismatch => "The ledger does not agree with the bank's balance",
        ImportFailed => "A file or sync could not be imported",
        MoveUnpaired => "A transfer has no matching other side",
        NewDebt => "A payment that looks like a new loan",
        NewRecurring => "A charge that looks like a new recurring item",
        BudgetPop => "Spending is over a budget guideline",
        IncomeShort => "Income is behind what was expected",
        PlanWavered => "A plan has slipped a little",
        PlanBehind => "A plan is behind",
        OneTimeReview => "A large one-time item to look at",
        ReviewNeeded => "Lines tagged for review",
        RetirementGapGrew => "The retirement gap got bigger",
        CheckUnknown => "A check that needs a name: what was it for?",
        _ => SuggestionCatalog.Find(kind)?.Title ?? kind,
    };
}

/// <summary>A flag's one text field holds a short title and a detail, joined by a line break.</summary>
public static class FlagText
{
    public static string Join(string title, string detail) => $"{title}\n{detail}";

    public static (string Title, string Detail) Split(string message)
    {
        var i = message.IndexOf('\n');
        return i < 0 ? (message, "") : (message[..i], message[(i + 1)..]);
    }
}

/// <summary>Quiet hours (docs/ARCHITECTURE.md): urgent messages wait until they are over.</summary>
public static class QuietHours
{
    /// <summary>True when <paramref name="local"/> is inside the window. A window that crosses midnight, like 21:00 to 07:00, is the usual case.</summary>
    public static bool IsQuiet(TimeOnly local, TimeOnly start, TimeOnly end)
    {
        if (start == end) return false;
        return start < end ? local >= start && local < end : local >= start || local < end;
    }
}
