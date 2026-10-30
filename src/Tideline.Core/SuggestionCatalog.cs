namespace Tideline.Core;

/// <summary>
/// One suggestion the app ships. People can switch it on or off (and, for the ones that have their own message, whether it is sent); they cannot
/// change its words or its rules yet. The words live here, the checks live in code (an <c>IFlagFamily</c> in Tideline.Notify), and the switches
/// live in the database against <see cref="Key"/>. An update to the wording therefore keeps everyone's switches.
/// </summary>
/// <param name="Key">Also the kind of flag it is attached to. Fixed forever.</param>
/// <param name="Group">Where it is listed. "Alerts" means advice attached to one of the built-in alerts; the rest raise their own card.</param>
/// <param name="Advice">What to do or look at. The figures it was built from come with the card, as its details.</param>
/// <param name="Note">The small print on the card.</param>
/// <param name="Action">A fixed step a person can ask for from the card (see SuggestionActions), or null.</param>
/// <param name="Recurrence">How often it can come back, in words: "each time", "monthly", "quarterly" or "yearly".</param>
public sealed record SuggestionDef(
    string Key, string Group, string Title, string Advice, string? Note = null, bool DefaultOn = true, string? Action = null, string Recurrence = "each time")
{
    /// <summary>An alert's own advice, not a check of its own: its message and sending are the alert's.</summary>
    public bool IsAlertAdvice => Group == SuggestionCatalog.AlertsGroup;
}

public static class SuggestionCatalog
{
    public const string AlertsGroup = "Alerts";

    /// <summary>On the small print of every card that is a prompt to look at money.</summary>
    /// <summary>The fixed steps a card can offer. Only a person's click runs one, and each is audited.</summary>
    public const string MarkOneTime = "mark_one_time";
    public const string AddRecurring = "add_recurring";

    public const string NotAdvice = "A prompt to look, not financial or tax advice.";

    private static SuggestionDef Alert(string key, string title, string advice) => new(key, AlertsGroup, title, advice);

    private static SuggestionDef Own(string key, string group, string title, string advice, string recurrence, bool on = true, string? action = null, string? reference = null) =>
        new(key, group, title, advice, reference is null ? NotAdvice : $"{NotAdvice} {reference}", on, action, recurrence);

    public static IReadOnlyList<SuggestionDef> All { get; } =
    [
        // advice attached to the alerts that were already there
        Alert(FlagKinds.RecurringMissing, "A bill has not been paid", "Check whether the bill was paid some other way, or is late. If it is late, pay it now; the flag clears by itself once the payment shows up."),
        Alert(FlagKinds.RecurringOutOfRange, "A bill came in at an odd amount", "Open the bill and look at this month's amount. If the price went up for good, change the expected range under Settings, Recurring so it stops flagging."),
        Alert(FlagKinds.CardNotPaidInFull, "A card was not paid in full", "Pay the whole statement balance before the due date. Paying in full each month is what keeps a card free of interest."),
        Alert(FlagKinds.CardInterest, "Interest was charged on a card", "Paying the full balance next month stops more interest. If this keeps happening, look at what is being put on the card."),
        Alert(FlagKinds.BalanceMismatch, "The ledger and the bank disagree", "Compare the bank's balance with the ledger on the account. A file that was missed or imported twice is the usual cause; import the file for the days involved again."),
        Alert(FlagKinds.ImportFailed, "An import did not work", "Read the reason on the Imports page. The usual causes are a file for the wrong account, or a file that starts before the cutover date."),
        Alert(FlagKinds.MoveUnpaired, "A transfer has no other side", "Find the other half in the other account's export, or pair the two lines by hand in the Ledger. If it was not a transfer, open the line and release it."),
        Alert(FlagKinds.NewDebt, "Looks like a new loan", "Add it under Settings, Money accounts as a loan, with its rate and payment, so it counts in net worth and the debt countdown."),
        Alert(FlagKinds.NewRecurring, "Looks like a new recurring charge", "If it will keep coming, add it as a recurring item. It then gets a row in the Bills grid and a missed payment is noticed."),
        Alert(FlagKinds.BudgetPop, "Spending is over a budget guideline", "Open the lines behind it. If it was a one-off, tag it so it is easy to find; if it is the new normal, change the budget."),
        Alert(FlagKinds.IncomeShort, "Income is short", "Check whether a paycheck has not arrived yet, or was given the wrong category so it is not counted as income."),
        Alert(FlagKinds.PlanWavered, "A plan has slipped", "Open the plan on the Planning screen. It shows how much more each month would put it back on track."),
        Alert(FlagKinds.PlanBehind, "A plan is behind", "Open the plan on the Planning screen to see how much more each month it needs, or move the target date if the goal has changed."),
        Alert(FlagKinds.OneTimeReview, "A large one-time item", "Check it really is a one-time cost. If it will come around again, give it a recurring item or a plan so it is not a surprise."),
        Alert(FlagKinds.ReviewNeeded, "Lines are waiting for review", "Open the lines tagged review and give each one the right category. Select several and fix them together."),
        Alert(FlagKinds.RetirementGapGrew, "The retirement gap grew", "Look at the balances and the assumptions on the Retirement screen for anything out of date. It is a projection, not advice."),
        Alert(FlagKinds.CheckUnknown, "A check needs a name", "Open the check and say what it was for, and make a rule if it will repeat. The Needs a category screen lets you answer them one after another."),

        // taxes: what the bank shows is the result of the year, never the withholding itself
        Own(FlagKinds.TaxOverpaid, "Taxes", "Your refund may be larger than needed", "Your pay stub, or the IRS Tax Withholding Estimator, can show whether your withholding still fits.", "yearly", reference: "Source: the IRS Tax Withholding Estimator (irs.gov/individuals/tax-withholding-estimator), which suggests checking withholding every January and after big life changes."),
        Own(FlagKinds.TaxUnderpaid, "Taxes", "A payment at filing may mean too little was withheld", "Check your withholding, or whether estimated payments make sense, to avoid a penalty. Your pay stub or the IRS Tax Withholding Estimator can help.", "yearly", reference: "Source: the IRS page on the underpayment of estimated tax penalty: no penalty if you owe less than $1,000 at filing, or if you paid in at least 90% of this year's tax or 100% of last year's (110% at higher incomes)."),
        Own(FlagKinds.TaxUnknown, "Taxes", "No tax refund or payment seen", "If your refund or payment went through an account you have not added, add it under Settings, Money accounts. If you have not filed yet, check the deadline.", "yearly"),

        // savings
        Own(FlagKinds.SavingsNone, "Savings", "Nothing went into savings last month", "Check whether it went into an account you have not added, or move something into savings.", "monthly"),
        Own(FlagKinds.SavingsLow, "Savings", "Savings were well below usual last month", "Look at what changed that month. A smaller transfer is fine if there was a reason; set it back when you can.", "monthly"),
        Own(FlagKinds.NoSavingsAccount, "Savings", "No savings account", "A separate place for savings makes it easier to see what is put aside. If you have one, add it under Settings, Money accounts. If not, it may be worth opening one.", "yearly"),
        Own(FlagKinds.EmergencyCushion, "Savings", "Savings cover less than three months", "A smaller starting cushion, such as $500 to $1,000, is a common first step. People with irregular or self-employed income are often advised to hold more.", "quarterly", reference: "Many guides suggest 3 to 6 months of essential expenses. The CFPB gives no set number: it says the right amount depends on your situation, and that even a small amount helps."),

        // cashflow, as a share of take-home pay (50/30/20), each measure monthly and yearly
        Own(FlagKinds.NeedsHighMonth, "Cashflow", "Bills and required costs are a large share of income (monthly)", "Review your monthly bills for anything that can come down. Insurance, phone, internet and subscriptions are the usual places.", "monthly", reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),
        Own(FlagKinds.NeedsHighYear, "Cashflow", "Bills and required costs are a large share of income (yearly)", "Review your bills for the year for anything that can come down. Insurance, phone, internet and subscriptions are the usual places.", "yearly", reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),
        Own(FlagKinds.WantsHighMonth, "Cashflow", "Extra and one-time spending is a large share of income (monthly)", "Look through the Extra and One-time lines for the month. A few can usually be trimmed without much notice.", "monthly", false, reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),
        Own(FlagKinds.WantsHighYear, "Cashflow", "Extra and one-time spending is a large share of income (yearly)", "Look through the Extra and One-time lines for the year. A few can usually be trimmed without much notice.", "yearly", false, reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),
        Own(FlagKinds.SavingsShareLowMonth, "Cashflow", "Savings are a small share of income (monthly)", "Look at what was left over, and whether some of it can move into savings before it is spent.", "monthly", false, reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),
        Own(FlagKinds.SavingsShareLowYear, "Cashflow", "Savings are a small share of income (yearly)", "Look at what was left over, and whether some of it can move into savings before it is spent.", "yearly", false, reference: "The 50/30/20 guide puts needs at 50%, wants at 30% and savings and debt payments at 20% of after-tax income (popularised by Elizabeth Warren and Amelia Warren Tyagi in All Your Worth). Another common guide puts fixed costs at 50 to 60% of take-home pay. Rules of thumb, not rules."),

        // bills: make sure the Bills sheet is set up so a missed one can be noticed
        Own(FlagKinds.NoBills, "Bills", "Nothing is marked as a bill", "Bills like a mortgage, power or phone are tracked on the Bills screen. If you have any, set their category to a bill type, or add the starter set under Settings, Rules.", "yearly"),
        Own(FlagKinds.NoRecurring, "Bills", "No bills are being tracked", "Without them a missed bill cannot be noticed. Use Find my bills under Settings, Recurring to set them up in one go.", "yearly"),
        Own(FlagKinds.RecurringCandidate, "Bills", "This looks like a bill or subscription", "Tracking it gives it a row on the Bills screen or the subscriptions list, and a missed payment is noticed. Your past lines are linked to it, and next month's link themselves.", "yearly", true, AddRecurring),

        // spending
        Own(FlagKinds.LargePayment, "Spending", "A large payment that looks one-time", "If it will not come around again, mark it One-time so it does not skew the month. If it will, give it a recurring item or a plan.", "each time", true, MarkOneTime),

        // subscriptions
        Own(FlagKinds.SubscriptionsReview, "Subscriptions", "Quarterly review of subscriptions", "Is each one still worth it? Cancel or pause any you no longer use. They are listed on the Extra screen.", "quarterly"),
        Own(FlagKinds.SubscriptionsMany, "Subscriptions", "Many subscriptions", "A quick look at which ones you still use can free some of that.", "quarterly"),
    ];

    private static readonly Dictionary<string, SuggestionDef> ByKey = All.ToDictionary(s => s.Key);

    public static SuggestionDef? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The kinds of flag that only suggestions raise (the alerts are listed in <see cref="FlagKinds"/>).</summary>
    public static IEnumerable<string> OwnKinds => All.Where(s => !s.IsAlertAdvice).Select(s => s.Key);
}
