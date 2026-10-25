using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tideline.Cli;

public sealed record DemoOptions(int Lines, int Seed, DateOnly From, DateOnly To)
{
    public static DemoOptions Default => new(5000, 42, new DateOnly(2025, 1, 1), new DateOnly(2026, 9, 30));
}

public sealed record DemoResult(string Folder, string HouseholdFile, int Lines, int Files, IReadOnlyDictionary<string, int> LinesPerAccount);

/// <summary>
/// Made-up Truist exports for a household with six accounts, so the app can be tried with a realistic amount of data.
/// Everything comes from a seed: the same options always give the same files. Nothing here is real, and the files are
/// meant for a scratch folder, not the repository.
/// </summary>
public static class DemoData
{
    private sealed class Rng
    {
        private ulong _s;

        public Rng(int seed)
        {
            _s = (ulong)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL;
            Next();
        }

        private ulong Next()
        {
            _s ^= _s >> 12;
            _s ^= _s << 25;
            _s ^= _s >> 27;
            return _s * 0x2545F4914F6CDD1DUL;
        }

        public int Int(int min, int maxExclusive) => min + (int)(Next() % (ulong)(maxExclusive - min));
        public double Double() => (Next() >> 11) * (1.0 / (1UL << 53));
        public long Cents(double min, double max) => (long)Math.Round((min + Double() * (max - min)) * 100);
        public T Pick<T>(IReadOnlyList<T> items) => items[Int(0, items.Count)];

        public T Weighted<T>(IReadOnlyList<(int Weight, T Item)> items)
        {
            var roll = Int(0, items.Sum(i => i.Weight));
            foreach (var (w, item) in items)
            {
                if (roll < w) return item;
                roll -= w;
            }
            return items[^1].Item;
        }
    }

    private sealed record Account(string Key, string Name, string Kind, string Bank, long OpeningCents)
    {
        public bool IsCard => Kind == "credit_card";
    }

    /// <param name="Cents">From the account holder's view: positive in, negative out.</param>
    private sealed record Row(string Account, DateOnly Date, string Description, long Cents, string Check = "");

    private sealed record Shop(int Weight, Func<Rng, string> Describe, double Min, double Max);

    private static readonly Account[] Accounts =
    [
        new("everyday", "Everyday Checking", "checking", "1111", 8_000_00),
        new("bills", "Bills Checking", "checking", "4444", 9_000_00),
        new("savings", "Money Market", "savings", "3333", 18_000_00),
        new("emergency", "Emergency Fund", "savings", "5555", 9_000_00),
        new("visa", "Everyday Visa", "credit_card", "2222", 0),
        new("travel", "Travel Card", "credit_card", "6666", 0),
    ];

    // The shop table is in "typical" dollars. Monthly card spending stays inside the household's income whatever the line
    // count: more lines a month means smaller purchases. 0.55 is right for about 210 purchases a month.
    private const double SpendScale = 0.55;
    private const double PurchasesPerMonthAtScale = 210;

    private static Account A(string key) => Accounts.First(a => a.Key == key);

    private static string N(Rng r, int lo = 100, int hi = 999) => r.Int(lo, hi).ToString(CultureInfo.InvariantCulture);

    private static readonly string[] Eateries = ["CORNER BAKERY", "LUCKY NOODLE", "PIZZA SHACK", "TACO TIME", "GREEN LEAF CAFE", "SMOKEHOUSE BBQ", "HARBOR SUSHI", "MAPLE DINER"];
    private static readonly string[] Cities = ["DENVER", "AUSTIN", "SEATTLE", "ORLANDO", "BOSTON", "PORTLAND"];

    private static readonly IReadOnlyList<Shop> Shops =
    [
        new(20, r => $"KROGER #{N(r)}", 18, 165),
        new(8, r => $"TRADER JOES #{N(r)}", 22, 98),
        new(4, r => $"COSTCO WHSE #{N(r)}", 85, 340),
        new(6, r => $"WHOLEFDS MKT {N(r)}", 15, 120),
        new(8, r => $"SHELL OIL {N(r, 1000, 9999)}", 28, 68),
        new(5, r => $"CHEVRON {N(r, 1000, 9999)}", 30, 72),
        new(3, r => $"EXXONMOBIL {N(r, 1000, 9999)}", 30, 70),
        new(14, r => $"SQ *{r.Pick(Eateries)}", 9, 74),
        new(6, r => $"TST* {r.Pick(Eateries)} {N(r, 10, 99)}", 12, 88),
        new(4, r => $"CHIPOTLE {N(r, 1000, 9999)}", 9, 28),
        new(16, r => $"STARBUCKS STORE {N(r, 10000, 99999)}", 4.5, 13),
        new(6, r => $"DD *DOORDASH {r.Pick(Eateries)}", 18, 62),
        new(3, _ => "UBER EATS HELP.UBER.COM", 15, 55),
        new(14, r => $"AMAZON.COM*{r.Int(10000, 99999)}AB", 8, 220),
        new(8, r => $"TARGET {N(r, 1000, 9999)}", 12, 150),
        new(4, r => $"HOME DEPOT #{N(r, 1000, 9999)}", 14, 210),
        new(5, _ => "WALMART SUPERCENTER", 11, 140),
        new(4, r => $"CVS PHARMACY #{N(r, 1000, 9999)}", 6, 58),
        new(2, r => $"WALGREENS #{N(r, 1000, 9999)}", 5, 40),
        new(1, _ => "DR KIM DENTAL", 45, 220),
        new(1, _ => "PTA SCHOOL FUND", 10, 60),
        new(1, _ => "SCHOLASTIC BOOK CLUB", 8, 40),
        new(2, _ => "KIDS CLOTHES ETC", 20, 90),
        new(2, r => $"AMC THEATRES {N(r, 1000, 9999)}", 14, 60),
        new(1, _ => "STEAM GAMES", 6, 60),
        new(3, r => $"ODDS AND ENDS {N(r, 1000, 9999)}", 5, 120), // matches no rule on purpose: lands in the review pile
    ];

    private static readonly IReadOnlyList<Shop> TravelShops =
    [
        new(3, r => $"DELTA AIR 0062{r.Int(100000, 999999)}", 180, 720),
        new(3, r => $"MARRIOTT {r.Pick(Cities)}", 120, 640),
        new(2, r => $"AIRBNB * {r.Int(10000, 99999)}", 150, 900),
        new(2, _ => "HERTZ RENTAL", 60, 340),
        new(3, r => $"SQ *{r.Pick(Eateries)}", 12, 95),
    ];

    public static DemoResult Generate(string folder, DemoOptions o)
    {
        var rng = new Rng(o.Seed);
        var months = new List<DateOnly>();
        for (var m = new DateOnly(o.From.Year, o.From.Month, 1); m <= o.To; m = m.AddMonths(1)) months.Add(m);
        if (months.Count == 0) throw new ArgumentException("The period is empty.");

        var rows = new List<Row>();
        var checkNo = 1040;

        DateOnly D(DateOnly month, int day) => new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
        bool In(DateOnly d) => d >= o.From && d <= o.To;
        void Add(string account, DateOnly d, string desc, long cents, string check = "")
        {
            if (In(d)) rows.Add(new Row(account, d, desc, cents, check));
        }

        void Move(string from, string to, DateOnly month, int day, string outDesc, string inDesc, long cents)
        {
            Add(from, D(month, day), outDesc, -cents);
            Add(to, D(month, day + 1), inDesc, cents);
        }

        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            var raise = i >= 10 ? 5_800_00L : 5_600_00L;

            // income
            Add("everyday", D(m, 1), "PAYROLL ACME CORP DIRECT DEP", raise);
            Add("everyday", D(m, 15), "PAYROLL ACME CORP DIRECT DEP", raise);
            Add("everyday", D(m, 27), "PAYROLL BRIGHT SCHOOLS DIRECT DEP", 3_800_00);
            if (m.Month == 3) Add("everyday", D(m, 12), "IRS TREAS 310 TAX REF", rng.Cents(1_500, 2_200));

            // moves between our own accounts
            Move("everyday", "bills", m, 1, "TRANSFER TO BILLS CHECKING", "TRANSFER FROM EVERYDAY CHECKING", 3_500_00);
            Move("everyday", "savings", m, 5, "TRANSFER TO MONEY MARKET", "TRANSFER FROM EVERYDAY CHECKING", 1_200_00);
            Move("savings", "emergency", m, 7, "TRANSFER TO EMERGENCY FUND", "TRANSFER FROM MONEY MARKET", 150_00);

            // bills
            var season = Math.Cos((m.Month - 1) / 12.0 * 2 * Math.PI); // 1 in January, -1 in July
            Add("bills", D(m, 3), "MORTGAGE ACH LAKESIDE BANK", -1_850_00);
            Add("bills", D(m, 8), "CITY POWER AUTOPAY", -(long)Math.Round((125 + 40 * Math.Abs(season) + rng.Double() * 12) * 100));
            Add("bills", D(m, 12), "WATER DEPT UTILITY PMT", -rng.Cents(48, 72));
            Add("bills", D(m, 14), "FIBERNET INTERNET", -79_99);
            Add("bills", D(m, 16), "MOBILEONE WIRELESS", -124_37);
            Add("bills", D(m, 20), "SAFEHARBOR AUTO INS", -142_18);
            if (m.Month is 1 or 7) Add("bills", D(m, 10), "SAFEHARBOR HOME INS", -612_00);
            if (m.Month is 4 or 10) Add("bills", D(m, 15), "COUNTY TAX COLLECTOR PROP TAX", -2_350_00);
            for (var c = rng.Int(2, 4); c > 0; c--)
                Add("bills", D(m, rng.Int(2, 28)), $"Check #{++checkNo}", -rng.Cents(120, 240), (checkNo).ToString(CultureInfo.InvariantCulture));
            if (m.Year == o.From.Year + 1 && m.Month == 5) Add("bills", D(m, 18), $"Check #{++checkNo}", -4_200_00, checkNo.ToString(CultureInfo.InvariantCulture));

            // interest on the savings accounts, last day of the month
            Add("savings", D(m, 31), "INTEREST PAID", rng.Cents(4, 7));
            Add("emergency", D(m, 31), "INTEREST PAID", rng.Cents(2, 4));

            // subscriptions on the Visa
            Add("visa", D(m, 2), "NETFLIX.COM", -(i >= 10 ? 17_99 : 15_49));
            Add("visa", D(m, 4), "SPOTIFY USA", -10_99);
            Add("visa", D(m, 9), "PLANET FITNESS", -24_99);
            Add("visa", D(m, 11), "APPLE.COM/BILL ICLOUD", -2_99);
            Add("visa", D(m, 21), "HULU 877-8244858", -7_99);
        }

        // card payments: last month's purchases, paid in full, on the 17th to 19th of the next month
        var cardPayments = new[] { ("visa", "CARD PAYMENT VISA", "PAYMENT THANK YOU", 18), ("travel", "CARD PAYMENT TRAVEL", "PAYMENT THANK YOU", 19) };
        bool PaymentFits(DateOnly month, int outDay) => In(D(month.AddMonths(1), outDay)) && In(D(month.AddMonths(1), outDay - 1));
        var cardPaymentRows = cardPayments.Sum(p => months.Count(m => PaymentFits(m, p.Item4)) * 2);

        var fixedCount = rows.Count + cardPaymentRows;
        var remaining = o.Lines - fixedCount;
        if (remaining < 0)
            throw new ArgumentException($"{o.Lines} lines is too few for this period: the regular lines alone are {fixedCount}. Ask for at least {fixedCount}.");

        // everyday card purchases and the travel card, spread across the months
        var spendScale = Math.Clamp(SpendScale * PurchasesPerMonthAtScale / Math.Max(1.0, (double)remaining / months.Count), 0.15, 3.0);
        var perMonth = months.Select((_, i) => remaining / months.Count + (i < remaining % months.Count ? 1 : 0)).ToList();
        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            var left = perMonth[i];
            var tripMonth = i % 5 == 2;
            while (left > 0)
            {
                var travel = rng.Double() < (tripMonth ? 0.22 : 0.05);
                var cardKey = travel ? "travel" : rng.Double() < 0.12 ? "everyday" : "visa"; // some purchases are plain debit card spending
                var shop = rng.Weighted((travel ? TravelShops : Shops).Select(s => (s.Weight, s)).ToList());
                var day = D(m, rng.Int(1, 29));
                if (!In(day)) day = o.To < D(m, 28) ? o.To : (o.From > D(m, 1) ? o.From : D(m, 1));

                var desc = shop.Describe(rng);
                var cents = rng.Cents(shop.Min * spendScale, shop.Max * spendScale);
                var refund = cardKey == "visa" && rng.Double() < 0.015 && desc.StartsWith("AMAZON", StringComparison.Ordinal);
                if (refund) desc = "AMAZON.COM RETURN";

                var signed = refund ? cents : -cents;
                Add(cardKey, day, desc, signed);
                left--;

                // coffee runs sometimes repeat on the same day, exactly: the importer keeps genuine repeats
                if (cardKey != "everyday" && left > 0 && desc.StartsWith("STARBUCKS", StringComparison.Ordinal) && rng.Double() < 0.2)
                {
                    Add(cardKey, day, desc, signed);
                    left--;
                }
            }
        }

        foreach (var (cardKey, payDesc, receiveDesc, outDay) in cardPayments)
            foreach (var m in months)
            {
                var next = m.AddMonths(1);
                var paidOn = D(next, outDay);
                if (!PaymentFits(m, outDay)) continue;

                // everything charged that month (refunds included), not counting the payment that arrived in it
                var spent = -rows.Where(r => r.Account == cardKey && r.Date.Year == m.Year && r.Date.Month == m.Month && r.Description != receiveDesc).Sum(r => r.Cents);
                if (spent <= 0) spent = 1_00; // keep the line count exact even in a month with no net spend
                Add("everyday", paidOn, payDesc, -spent);
                Add(cardKey, D(next, outDay - 1), receiveDesc, spent);
            }

        if (rows.Count != o.Lines)
            throw new InvalidOperationException($"Generated {rows.Count} lines, expected {o.Lines}.");

        Directory.CreateDirectory(folder);
        var files = WriteFiles(folder, rows, months);
        var householdFile = Path.Combine(folder, "household.json");
        File.WriteAllText(householdFile, Household(o));

        return new DemoResult(folder, householdFile, rows.Count, files,
            Accounts.ToDictionary(a => a.Name, a => rows.Count(r => r.Account == a.Key)));
    }

    // ---- files --------------------------------------------------------------------------------------------

    private const string DepositHeader = "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Full description,Merchant name,Category name,Sub-category name,Amount,Daily Posted Balance";
    private const string CardHeader = "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Description,Amount";

    private static string Money(long cents)
    {
        var abs = Math.Abs(cents);
        var text = $"${abs / 100:N0}.{abs % 100:00}";
        return cents < 0 ? $"({text})" : text;
    }

    private static string Q(string s) => s.Contains(',') ? $"\"{s}\"" : s;

    private static int WriteFiles(string folder, List<Row> rows, List<DateOnly> months)
    {
        var written = 0;
        foreach (var account in Accounts)
        {
            var mine = rows.Where(r => r.Account == account.Key).OrderBy(r => r.Date).ToList(); // stable: ties keep generation order

            // end of day balances across the whole span
            var endOfDay = new Dictionary<DateOnly, long>();
            var running = account.OpeningCents;
            foreach (var g in mine.GroupBy(r => r.Date).OrderBy(g => g.Key))
            {
                running += g.Sum(r => r.Cents);
                endOfDay[g.Key] = running;
                if (!account.IsCard && running < 0)
                    throw new InvalidOperationException($"{account.Name} would go below zero on {g.Key:yyyy-MM-dd}; the demo amounts need adjusting.");
            }

            foreach (var m in months)
            {
                var monthRows = mine.Where(r => r.Date.Year == m.Year && r.Date.Month == m.Month).ToList();
                if (monthRows.Count == 0) continue;

                var last = new DateOnly(m.Year, m.Month, DateTime.DaysInMonth(m.Year, m.Month));
                var name = $"acct_{account.Bank}_{m.Month:00}_01_{m.Year}_to_{last.Month:00}_{last.Day:00}_{last.Year}.csv";
                var sb = new StringBuilder(account.IsCard ? CardHeader : DepositHeader).Append("\r\n");

                foreach (var r in monthRows)
                {
                    var posted = r.Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    var transacted = r.Date.AddDays(-1).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    if (account.IsCard)
                    {
                        var kind = r.Cents > 0 && r.Description.StartsWith("PAYMENT", StringComparison.Ordinal) ? "Payment" : "Purchase";
                        // card exports show purchases positive and payments (and refunds) in parentheses
                        sb.Append($"{posted},{transacted},{kind},,{Q(r.Description)},{Q(Money(-r.Cents))}\r\n");
                    }
                    else
                    {
                        var kind = r.Check.Length > 0 ? "Check" : r.Description.StartsWith("TRANSFER", StringComparison.Ordinal) ? "Transfer" : r.Cents > 0 ? "Deposit" : "Withdrawal";
                        sb.Append($"{posted},{transacted},{kind},{r.Check},{Q(r.Description)},,,,{Q(Money(r.Cents))},{Q(Money(endOfDay[r.Date]))}\r\n");
                    }
                }

                File.WriteAllText(Path.Combine(folder, name), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                written++;
            }
        }
        return written;
    }

    // ---- the household that goes with the files --------------------------------------------------------------

    private static string Household(DemoOptions o)
    {
        var first = o.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        object When(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
        object Rule(string name, int priority, object when, string category, string? payee = null, string? recurring = null) =>
            new { name, priority, when, set = new { category, payee, recurring } };
        object Rec(string name, string category, decimal min, decimal max, int dueDay, int everyN = 1, string? firstDueOn = null) =>
            new { name, category, min, max, frequency = "monthly", everyN, dueDay, firstDueOn = firstDueOn ?? first };

        var household = new
        {
            accounts = new object[]
            {
                new { name = "Everyday Checking", kind = "checking", bankNumber = "1111" },
                new { name = "Bills Checking", kind = "checking", bankNumber = "4444" },
                new { name = "Money Market", kind = "savings", bankNumber = "3333" },
                new { name = "Emergency Fund", kind = "savings", bankNumber = "5555" },
                new { name = "Everyday Visa", kind = "credit_card", bankNumber = "2222", card = new { payInFull = true, dueDay = 25 } },
                new { name = "Travel Card", kind = "credit_card", bankNumber = "6666", card = new { payInFull = true, dueDay = 25 } },
            },
            categories = new object[]
            {
                new { name = "Salary", defaultType = "income" }, new { name = "Tax refund", defaultType = "income" }, new { name = "Interest", defaultType = "income" },
                new { name = "Mortgage", defaultType = "bill" }, new { name = "Utilities", defaultType = "bill" }, new { name = "Internet and phone", defaultType = "bill" },
                new { name = "Insurance", defaultType = "bill" }, new { name = "Property tax", defaultType = "bill" },
                new { name = "Groceries", defaultType = "required" }, new { name = "Gas and auto", defaultType = "required" }, new { name = "Medical", defaultType = "required" },
                new { name = "Kids", defaultType = "required" }, new { name = "Childcare", defaultType = "required" },
                new { name = "Dining", defaultType = "extra" }, new { name = "Coffee", defaultType = "extra" }, new { name = "Delivery", defaultType = "extra" },
                new { name = "Shopping", defaultType = "extra" }, new { name = "Entertainment", defaultType = "extra" },
                new { name = "Streaming", defaultType = "extra" }, new { name = "Gym", defaultType = "extra" },
                new { name = "Travel", defaultType = "one_time" }, new { name = "Home repairs", defaultType = "one_time" },
            },
            recurring = new object[]
            {
                Rec("Mortgage", "Mortgage", 1850, 1850, 3), Rec("Electric", "Utilities", 100, 190, 8), Rec("Water", "Utilities", 45, 75, 12),
                Rec("Internet", "Internet and phone", 79.99m, 79.99m, 14), Rec("Phone", "Internet and phone", 124.37m, 124.37m, 16),
                Rec("Auto insurance", "Insurance", 142.18m, 142.18m, 20), Rec("Home insurance", "Insurance", 612, 612, 10, everyN: 6),
                Rec("Netflix", "Streaming", 15.49m, 17.99m, 2), Rec("Spotify", "Streaming", 10.99m, 10.99m, 4), Rec("Hulu", "Streaming", 7.99m, 7.99m, 21),
                Rec("iCloud", "Streaming", 2.99m, 2.99m, 11), Rec("Planet Fitness", "Gym", 24.99m, 24.99m, 9),
            },
            rules = new object[]
            {
                Rule("Income: Acme payroll", 10, When(("direction", "in"), ("account_kind", new[] { "checking", "savings" }), ("contains_any", new[] { "PAYROLL ACME" })), "Salary", "Acme Corp"),
                Rule("Income: school payroll", 11, When(("direction", "in"), ("account_kind", new[] { "checking", "savings" }), ("contains_any", new[] { "PAYROLL BRIGHT" })), "Salary", "Bright Schools"),
                Rule("Income: tax refund", 12, When(("direction", "in"), ("contains_any", new[] { "IRS TREAS" })), "Tax refund", "IRS"),
                Rule("Income: interest", 14, When(("direction", "in"), ("contains_any", new[] { "INTEREST PAID" })), "Interest", "Truist"),
                Rule("Delivery above dining", 20, When(("contains_any", new[] { "DOORDASH", "UBER EATS" })), "Delivery"),
                Rule("Bill: mortgage", 22, When(("contains_any", new[] { "MORTGAGE ACH" })), "Mortgage", "Lakeside Bank", "Mortgage"),
                Rule("Bill: electric", 24, When(("contains_any", new[] { "CITY POWER" })), "Utilities", "City Power", "Electric"),
                Rule("Bill: water", 26, When(("contains_any", new[] { "WATER DEPT" })), "Utilities", "Water Department", "Water"),
                Rule("Bill: internet", 28, When(("contains_any", new[] { "FIBERNET" })), "Internet and phone", "FiberNet", "Internet"),
                Rule("Bill: phone", 30, When(("contains_any", new[] { "MOBILEONE" })), "Internet and phone", "MobileOne", "Phone"),
                Rule("Bill: auto insurance", 32, When(("contains_any", new[] { "SAFEHARBOR AUTO" })), "Insurance", "SafeHarbor", "Auto insurance"),
                Rule("Bill: home insurance", 34, When(("contains_any", new[] { "SAFEHARBOR HOME" })), "Insurance", "SafeHarbor", "Home insurance"),
                Rule("Bill: property tax", 36, When(("contains_any", new[] { "COUNTY TAX" })), "Property tax", "County Tax Collector"),
                Rule("Sub: Netflix", 40, When(("contains_any", new[] { "NETFLIX" })), "Streaming", "Netflix", "Netflix"),
                Rule("Sub: Spotify", 41, When(("contains_any", new[] { "SPOTIFY" })), "Streaming", "Spotify", "Spotify"),
                Rule("Sub: Hulu", 42, When(("contains_any", new[] { "HULU" })), "Streaming", "Hulu", "Hulu"),
                Rule("Sub: iCloud", 43, When(("contains_any", new[] { "ICLOUD" })), "Streaming", "Apple", "iCloud"),
                Rule("Sub: gym", 44, When(("contains_any", new[] { "PLANET FITNESS" })), "Gym", "Planet Fitness", "Planet Fitness"),
                Rule("Big check: roof", 50, When(("starts_with", "Check #"), ("amount_min", 4000)), "Home repairs", "Roofer"),
                Rule("Check: childcare", 52, When(("starts_with", "Check #"), ("amount_min", 100), ("amount_max", 300)), "Childcare", "Babysitter"),
                Rule("Gas", 60, When(("contains_any", new[] { "SHELL OIL", "CHEVRON", "EXXONMOBIL" })), "Gas and auto"),
                Rule("Groceries", 62, When(("contains_any", new[] { "KROGER", "TRADER JOES", "COSTCO", "WHOLEFDS" })), "Groceries"),
                Rule("Medical", 64, When(("contains_any", new[] { "CVS PHARMACY", "WALGREENS", "DR KIM" })), "Medical"),
                Rule("Kids", 66, When(("contains_any", new[] { "PTA SCHOOL", "SCHOLASTIC", "KIDS CLOTHES" })), "Kids"),
                Rule("Coffee", 70, When(("contains_any", new[] { "STARBUCKS" })), "Coffee", "Starbucks"),
                Rule("Dining", 72, When(("contains_any", new[] { "SQ *", "TST*", "CHIPOTLE" })), "Dining"),
                Rule("Shopping", 74, When(("contains_any", new[] { "AMAZON", "TARGET", "HOME DEPOT", "WALMART" })), "Shopping"),
                Rule("Entertainment", 76, When(("contains_any", new[] { "AMC THEATRES", "STEAM GAMES" })), "Entertainment"),
                Rule("Travel", 80, When(("contains_any", new[] { "DELTA AIR", "MARRIOTT", "AIRBNB", "HERTZ" })), "Travel"),
            },
            movePatterns = new object[]
            {
                new { from = "Everyday Checking", fromContains = "TRANSFER TO BILLS CHECKING", to = "Bills Checking", toContains = "TRANSFER FROM EVERYDAY CHECKING" },
                new { from = "Everyday Checking", fromContains = "TRANSFER TO MONEY MARKET", to = "Money Market", toContains = "TRANSFER FROM EVERYDAY CHECKING" },
                new { from = "Money Market", fromContains = "TRANSFER TO EMERGENCY FUND", to = "Emergency Fund", toContains = "TRANSFER FROM MONEY MARKET" },
                new { from = "Everyday Checking", fromContains = "CARD PAYMENT VISA", to = "Everyday Visa", toContains = "PAYMENT THANK YOU" },
                new { from = "Everyday Checking", fromContains = "CARD PAYMENT TRAVEL", to = "Travel Card", toContains = "PAYMENT THANK YOU" },
            },
        };

        return JsonSerializer.Serialize(household, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
