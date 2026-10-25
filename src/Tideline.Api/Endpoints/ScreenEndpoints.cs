using System.Globalization;
using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Reports;

namespace Tideline.Api.Endpoints;

/// <summary>
/// Read models for the screens (docs/07-api.md, "Screens"). Every figure carries the drill-down query that opens
/// the lines behind it, and a test asserts the two agree.
/// </summary>
public static class ScreenEndpoints
{
    private const long OneTimeAttentionCents = 50000;

    public static void MapScreenEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/overview", Overview);
        api.MapGet("/accounts/by-month", AccountsByMonth);
        api.MapGet("/cards", Cards);
        api.MapGet("/bills", Bills);
        api.MapGet("/required", (HttpRequest r, SqliteConnectionFactory f, Clock c) => TypeScreen(LineType.Required, r, f, c));
        api.MapGet("/extra", (HttpRequest r, SqliteConnectionFactory f, Clock c) => TypeScreen(LineType.Extra, r, f, c));
        api.MapGet("/income", Income);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static bool TryRange(HttpRequest req, Clock clock, out DateOnly from, out DateOnly to, out IResult? problem)
    {
        problem = null;
        var today = clock.Today;
        from = new DateOnly(today.Year, today.Month, 1);
        to = Months.Last(from);

        var f = req.Query["from"].ToString();
        var t = req.Query["to"].ToString();
        if (f.Length > 0 && !DateOnly.TryParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from))
            problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["'from' must be YYYY-MM-DD."] });
        else if (t.Length > 0 && !DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to))
            problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["'to' must be YYYY-MM-DD."] });
        else if (f.Length > 0 && t.Length == 0) to = Months.Last(from);
        else if (from > to)
            problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["'to' is before 'from'."] });
        return problem is null;
    }

    private static bool TryYear(HttpRequest req, Clock clock, out int year, out IResult? problem)
    {
        problem = null;
        year = clock.Today.Year;
        var text = req.Query["year"].ToString();
        if (text.Length > 0 && (!int.TryParse(text, out year) || year < 1990 || year > 2200))
            problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["year"] = ["'year' must be a four digit year."] });
        return problem is null;
    }

    private static string MonthLabel(int year, int month) => $"{year:D4}-{month:D2}";

    // ---- overview ------------------------------------------------------------------------------------------

    private static IResult Overview(HttpRequest req, SqliteConnectionFactory factory, Clock clock)
    {
        if (!TryRange(req, clock, out var from, out var to, out var problem)) return problem!;

        using var conn = factory.Open();
        var byMonth = new ReportRepository(conn).SpreadByMonth(from, to);
        var spread = Spread.From(byMonth.Select(t => (t.Type, t.ReportCents)));

        Figure F(LineType t) => new(spread.For(t), Drill.Range(from, to, ("type", t)));

        var months = byMonth.GroupBy(t => t.Month).OrderBy(g => g.Key).Select(g =>
        {
            var s = Spread.From(g.Select(t => (t.Type, t.ReportCents)));
            return new { month = g.Key, @in = s.InCents, bills = s.BillsCents, required = s.RequiredCents, extra = s.ExtraCents, oneTime = s.OneTimeCents, savings = s.SavingsCents, leftOver = s.LeftOverCents };
        }).ToList();

        return Results.Ok(new
        {
            from,
            to,
            spread = new
            {
                @in = F(LineType.Income), bills = F(LineType.Bill), required = F(LineType.Required), extra = F(LineType.Extra),
                oneTime = F(LineType.OneTime), savings = F(LineType.Savings),
                @out = new Figure(spread.OutCents, null), leftOver = new Figure(spread.LeftOverCents, null),
            },
            months,
            plans = Array.Empty<object>(),
            flags = new { alert = 0, warn = 0, info = 0 },
        });
    }

    // ---- accounts by month ---------------------------------------------------------------------------------

    private static IResult AccountsByMonth(HttpRequest req, SqliteConnectionFactory factory, Clock clock, TimeProvider time)
    {
        if (!TryYear(req, clock, out var year, out var problem)) return problem!;
        var today = clock.Today;
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);

        using var conn = factory.Open();
        var reports = new ReportRepository(conn);
        var flows = reports.AccountFlows(start, end).ToLookup(f => f.AccountId);

        var accounts = new AccountRepository(conn, time).All()
            .Where(a => a.Kind is AccountKind.Checking or AccountKind.Savings or AccountKind.CreditCard).ToList();

        var rows = accounts.Select(a =>
        {
            var running = (a.OpeningBalanceCents ?? 0) + reports.NetBefore(a.Id, start);
            var cells = Enumerable.Range(1, 12).Select(m =>
            {
                var first = new DateOnly(year, m, 1);
                var key = MonthLabel(year, m);
                var flow = flows[a.Id].FirstOrDefault(f => f.Month == key);
                running += flow?.NetCents ?? 0;
                return new
                {
                    month = key,
                    inCents = flow?.InCents ?? 0,
                    outCents = flow?.OutCents ?? 0,
                    netCents = flow?.NetCents ?? 0,
                    endBalanceCents = first <= today ? (long?)running : null,
                    drill = Drill.Range(first, Months.Last(first), ("account", a.Id), ("move", "include")),
                };
            }).ToList();

            return new
            {
                accountId = a.Id,
                name = a.Name,
                kind = a.Kind.ToDb(),
                openingKnown = a.OpeningBalanceCents is not null,
                cells,
            };
        }).ToList();

        return Results.Ok(new { year, accounts = rows });
    }

    // ---- cards ---------------------------------------------------------------------------------------------

    private static IResult Cards(HttpRequest req, SqliteConnectionFactory factory, Clock clock, TimeProvider time)
    {
        var today = clock.Today;
        var selected = new DateOnly(today.Year, today.Month, 1);
        var monthText = req.Query["month"].ToString();
        if (monthText.Length > 0 && !Months.TryParse(monthText, out selected))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["month"] = ["'month' must be YYYY-MM."] });

        var year = selected.Year;
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);

        using var conn = factory.Open();
        var reports = new ReportRepository(conn);
        var ledger = new LedgerRepository(conn, time);
        var terms = new CardTermsRepository(conn).All();
        var flows = reports.AccountFlows(yearStart, yearEnd).ToLookup(f => f.AccountId);

        var cards = new AccountRepository(conn, time).All().Where(a => a.Kind == AccountKind.CreditCard).Select(a =>
        {
            terms.TryGetValue(a.Id, out var t);
            var running = (a.OpeningBalanceCents ?? 0) + reports.NetBefore(a.Id, yearStart);

            var months = new List<object>();
            bool? lastChecked = null;
            for (var m = 1; m <= 12; m++)
            {
                var first = new DateOnly(year, m, 1);
                var last = Months.Last(first);
                var flow = flows[a.Id].FirstOrDefault(f => f.Month == MonthLabel(year, m));
                running += flow?.NetCents ?? 0;

                bool? paidInFull = null;
                if (t is { IsPayInFull: true } && last < today)
                {
                    var deadline = CardCheck.PaymentDeadline(year, m, t.DueDay ?? 31);
                    if (deadline <= today)
                    {
                        paidInFull = CardCheck.PaidInFull(running, reports.CardPayments(a.Id, last.AddDays(1), deadline));
                        lastChecked = paidInFull;
                    }
                }

                months.Add(new
                {
                    month = MonthLabel(year, m),
                    purchasesCents = flow?.OutCents ?? 0,
                    paymentsCents = reports.CardPayments(a.Id, first, last),
                    endBalanceCents = first <= today ? (long?)running : null,
                    paidInFull,
                    drill = Drill.Range(first, last, ("account", a.Id), ("move", "include")),
                });
            }

            DateOnly? nextDue = null;
            if (t?.DueDay is { } due)
            {
                var candidate = new DateOnly(today.Year, today.Month, Math.Min(due, DateTime.DaysInMonth(today.Year, today.Month)));
                if (candidate < today) { var n = today.AddMonths(1); candidate = new DateOnly(n.Year, n.Month, Math.Min(due, DateTime.DaysInMonth(n.Year, n.Month))); }
                nextDue = candidate;
            }

            var selLast = Months.Last(selected);
            var categories = reports.AccountCategoryTotals(a.Id, selected, selLast).Select(c => new
            {
                c.CategoryId, c.Name, c.Cents, c.Lines,
                drill = Drill.Range(selected, selLast, ("account", a.Id), ("category", c.CategoryId)),
            }).ToList();

            return new
            {
                accountId = a.Id,
                name = a.Name,
                balanceCents = ledger.BalanceAsOf(a.Id, today),
                balanceDrill = Drill.Range(new DateOnly(2000, 1, 1), today, ("account", a.Id), ("move", "include")),
                isPayInFull = t?.IsPayInFull ?? true,
                dueDay = t?.DueDay,
                statementDay = t?.StatementDay,
                nextDue,
                lastCheckedPaidInFull = lastChecked,
                months,
                categories,
                paymentsDrill = Drill.Range(yearStart, yearEnd, ("account", a.Id), ("move", "only"), ("direction", "in")),
            };
        }).ToList();

        return Results.Ok(new { month = Months.Key(selected), year, cards });
    }

    // ---- bills ---------------------------------------------------------------------------------------------

    private static IResult Bills(HttpRequest req, SqliteConnectionFactory factory, Clock clock)
    {
        if (!TryYear(req, clock, out var year, out var problem)) return problem!;
        var today = clock.Today;
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);

        using var conn = factory.Open();
        var reports = new ReportRepository(conn);

        var items = new RecurringRepository(conn).All().Where(r => r.CategoryType == LineType.Bill).ToList();
        var paidAll = reports.RecurringPaid(new DateOnly(year - 1, 1, 1), end).ToLookup(p => p.RecurringId);

        var rows = new List<BillRow>();
        foreach (var r in items)
        {
            var byMonth = paidAll[r.Item.Id].ToDictionary(p => p.Month, p => p.Cents);
            var cells = new List<BillCell>();
            for (var m = 1; m <= 12; m++)
            {
                var key = MonthLabel(year, m);
                var history = byMonth.Where(kv => string.CompareOrdinal(kv.Key, key) < 0).OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                var cell = RecurringStatus.Evaluate(r.Item, year, m, byMonth.GetValueOrDefault(key), today, history);
                var first = new DateOnly(year, m, 1);
                cells.Add(new BillCell(key, cell.Status.ToDb(), cell.PaidCents, cell.ProjectedCents,
                    cell.PaidCents > 0 ? Drill.Range(first, Months.Last(first), ("recurring", r.Item.Id)) : null));
            }
            rows.Add(new BillRow(r.Item.Id, r.Item.Name, r.CategoryName, false, cells, cells.Sum(c => c.PaidCents + c.ProjectedCents), cells.Sum(c => c.ProjectedCents)));
        }

        foreach (var g in reports.UnlinkedBills(start, end).GroupBy(u => u.CategoryId))
        {
            var byMonth = g.ToDictionary(u => u.Month, u => u.Cents);
            var cells = Enumerable.Range(1, 12).Select(m =>
            {
                var key = MonthLabel(year, m);
                var paid = byMonth.GetValueOrDefault(key);
                var first = new DateOnly(year, m, 1);
                return new BillCell(key, paid != 0 ? "paid" : "not_expected", paid, 0,
                    paid != 0 ? Drill.Range(first, Months.Last(first), ("type", "bill"), ("category", g.Key), ("recurring", "none")) : null);
            }).ToList();
            rows.Add(new BillRow(null, "Other: " + g.First().CategoryName, g.First().CategoryName, true, cells, cells.Sum(c => c.PaidCents), 0));
        }

        var columnTotals = Enumerable.Range(0, 12).Select(i => new
        {
            month = MonthLabel(year, i + 1),
            paidCents = rows.Sum(r => r.Cells[i].PaidCents),
            projectedCents = rows.Sum(r => r.Cells[i].ProjectedCents),
        }).ToList();

        var attention = new LinesRepository(conn).Query(new LineFilter(start, end, [LineType.OneTime], MinCents: OneTimeAttentionCents, Sort: LineSort.DateDesc), 1, 50);

        return Results.Ok(new
        {
            year,
            rows,
            columnTotals,
            totalPaidCents = rows.Sum(r => r.Cells.Sum(c => c.PaidCents)),
            totalProjectedCents = rows.Sum(r => r.TotalProjectedCents),
            grandTotalCents = rows.Sum(r => r.TotalCents),
            oneTimeAttention = new
            {
                thresholdCents = OneTimeAttentionCents,
                drill = Drill.Range(start, end, ("type", "one_time"), ("minCents", OneTimeAttentionCents)),
                items = attention.Items.Select(LineDto.From),
            },
        });
    }

    private sealed record BillCell(string Month, string Status, long PaidCents, long ProjectedCents, string? Drill);

    private sealed record BillRow(long? RecurringId, string Name, string CategoryName, bool IsOther, List<BillCell> Cells, long TotalCents, long TotalProjectedCents);

    // ---- required / extra ----------------------------------------------------------------------------------

    private static IResult TypeScreen(LineType type, HttpRequest req, SqliteConnectionFactory factory, Clock clock)
    {
        if (!TryRange(req, clock, out var from, out var to, out var problem)) return problem!;

        using var conn = factory.Open();
        var reports = new ReportRepository(conn);
        var total = reports.SpreadFor(from, to).For(type);
        var byMonth = reports.CategoryByMonth(type, from, to).ToLookup(c => c.CategoryId);

        var categories = reports.CategoryTotals(type, from, to).Select(c => new
        {
            id = c.CategoryId,
            name = c.Name,
            cents = c.Cents,
            lines = c.Lines,
            drill = Drill.Range(from, to, ("type", type), ("category", c.CategoryId)),
            months = byMonth[c.CategoryId].OrderBy(m => m.Month).Select(m => new { month = m.Month, cents = m.Cents }).ToList(),
        }).ToList();

        var payees = reports.TopPayees(type, from, to).Select(p => new
        {
            p.Payee, p.Cents, p.Lines,
            drill = Drill.Range(from, to, ("type", type), ("q", p.Payee)),
        }).ToList();

        object? subscriptions = null;
        if (type == LineType.Extra) subscriptions = Subscriptions(conn, clock.Today);

        return Results.Ok(new
        {
            type = type.ToDb(),
            from,
            to,
            total = new Figure(total, Drill.Range(from, to, ("type", type))),
            categories,
            payees,
            subscriptions,
        });
    }

    private static List<object> Subscriptions(System.Data.IDbConnection conn, DateOnly today)
    {
        var reports = new ReportRepository(conn);
        var paid = reports.RecurringPaid(today.AddMonths(-14), today).ToLookup(p => p.RecurringId);

        return new RecurringRepository(conn).All()
            .Where(r => r.CategoryType is not LineType.Bill and not LineType.Income && r.Item.IsActive)
            .Select(r =>
            {
                var history = paid[r.Item.Id].OrderBy(p => p.Month).Select(p => p.Cents).Where(c => c > 0).ToList();
                var last = history.Count > 0 ? history[^1] : (long?)null;
                var previous = history.Count > 1 ? history[^2] : (long?)null;

                var cost = last ?? (r.Item.ExpectedMinCents + r.Item.ExpectedMaxCents) / 2;
                var perMonth = r.Item.Frequency == Frequency.Yearly ? cost / 12 : cost / Math.Max(1, r.Item.EveryN);

                DateOnly? next = null;
                for (var i = 0; i < 24 && next is null; i++)
                {
                    var d = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
                    if (!RecurringStatus.IsExpected(r.Item, d.Year, d.Month)) continue;
                    var due = RecurringStatus.DueDate(r.Item, d.Year, d.Month);
                    if (due >= today) next = due;
                }

                return (object)new
                {
                    id = r.Item.Id,
                    name = r.Item.Name,
                    category = r.CategoryName,
                    lastChargeCents = last,
                    previousChargeCents = previous,
                    priceChanged = last is not null && previous is not null && last != previous,
                    monthlyCostCents = perMonth,
                    yearlyCostCents = perMonth * 12,
                    nextCharge = next,
                    drill = Drill.Range(today.AddMonths(-14), today, ("recurring", r.Item.Id)),
                };
            }).ToList();
    }

    // ---- income --------------------------------------------------------------------------------------------

    private static IResult Income(HttpRequest req, SqliteConnectionFactory factory, Clock clock)
    {
        if (!TryYear(req, clock, out var year, out var problem)) return problem!;
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);

        using var conn = factory.Open();
        var reports = new ReportRepository(conn);
        var byMonth = reports.SpreadByMonth(start, end).Where(t => t.Type == LineType.Income).ToDictionary(t => t.Month, t => t.ReportCents);
        var expectedYear = conn.ExecuteScalar<long?>("SELECT expected_income_cents FROM budgets WHERE year = @year", new { year });
        var total = byMonth.Values.Sum();

        var months = Enumerable.Range(1, 12).Select(m =>
        {
            var key = MonthLabel(year, m);
            var first = new DateOnly(year, m, 1);
            return new
            {
                month = key,
                actualCents = byMonth.GetValueOrDefault(key),
                expectedCents = expectedYear is { } e ? e / 12 : (long?)null,
                drill = Drill.Range(first, Months.Last(first), ("type", "income")),
            };
        }).ToList();

        var categories = reports.CategoryTotals(LineType.Income, start, end).Select(c => new
        {
            id = c.CategoryId, name = c.Name, cents = c.Cents, lines = c.Lines,
            drill = Drill.Range(start, end, ("type", "income"), ("category", c.CategoryId)),
        }).ToList();

        var payees = reports.TopPayees(LineType.Income, start, end).Select(p => new
        {
            p.Payee, p.Cents, p.Lines,
            drill = Drill.Range(start, end, ("type", "income"), ("q", p.Payee)),
        }).ToList();

        var review = new LinesRepository(conn).Query(new LineFilter(start, end, ReviewOnly: true, Direction: LineDirection.In), 1, 50);

        return Results.Ok(new
        {
            year,
            total = new Figure(total, Drill.Range(start, end, ("type", "income"))),
            expectedCents = expectedYear,
            months,
            categories,
            payees,
            reviewDeposits = new
            {
                count = review.Summary.Count,
                sumCents = review.Summary.SumCents,
                drill = Drill.Range(start, end, ("review", true), ("direction", "in")),
                items = review.Items.Select(LineDto.From),
            },
        });
    }
}

internal static class StatusNames
{
    public static string ToDb(this CellStatus s) => TypeHandlers.ToSnakeCase(s.ToString());
}
