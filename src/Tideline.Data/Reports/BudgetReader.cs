using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Reports;

public sealed record BudgetCategoryRow(
    long CategoryId, string Name, string Type, long? MonthlyCents, long? AnnualCents, long ThisMonthCents, long YtdCents, bool Popped);

public sealed record BudgetIncome(long? ExpectedAnnualCents, long ActualYtdCents, long? ExpectedYtdCents, bool Short);

public sealed record BudgetView(
    int Year, string Month, int ElapsedMonths, int PopPercent, long PopMinCents, BudgetIncome Income, IReadOnlyList<BudgetCategoryRow> Categories, bool Saved);

public sealed record BudgetProposal(long CategoryId, string Name, long MonthlyCents, long TwelveMonthCents, long? CurrentMonthlyCents, long? CurrentAnnualCents);

public sealed record BudgetProposals(string From, string To, IReadOnlyList<BudgetProposal> Items);

/// <summary>The yearly budget against what was spent (docs/04-domain-rules.md, "Budget").</summary>
public sealed class BudgetReader(IDbConnection conn)
{
    public BudgetView Read(int year, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var head = conn.QuerySingleOrDefault<(long Id, long? Income, int Pop, long Min)?>("SELECT id, expected_income_cents, pop_percent, pop_min_cents FROM budgets WHERE year = @year", new { year });
        var lines = head is { } h
            ? conn.Query<(long Category, long? Monthly, long? Annual)>("SELECT category_id, monthly_cents, annual_cents FROM budget_lines WHERE budget_id = @id", new { id = h.Id }).ToDictionary(l => l.Category)
            : [];

        // the month being judged: this month in the current year, December in a past year, and the first month in a future one
        var month = year == today.Year ? today.Month : year < today.Year ? 12 : 1;
        var elapsed = year == today.Year ? today.Month : year < today.Year ? 12 : 0;
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = year == today.Year && month == today.Month ? today : monthStart.AddMonths(1).AddDays(-1);
        var ytdEnd = year == today.Year ? today : new DateOnly(year, 12, 31);

        var reports = new ReportRepository(conn);
        var spend = reports.SpendByCategoryMonth(new DateOnly(year, 1, 1), ytdEnd);
        var monthKey = $"{year:0000}-{month:00}";
        var pop = head?.Pop ?? 10;
        var min = head?.Min ?? 2500;

        var categories = conn.Query<(long Id, string Name, string Type, bool Archived)>("SELECT id, name, default_type, is_archived FROM categories ORDER BY sort_order, name").ToList();
        var rows = new List<BudgetCategoryRow>();
        foreach (var c in categories)
        {
            lines.TryGetValue(c.Id, out var line);
            var ytd = spend.Where(s => s.CategoryId == c.Id).Sum(s => s.Cents);
            var thisMonth = spend.Where(s => s.CategoryId == c.Id && s.Month == monthKey).Sum(s => s.Cents);
            if (c.Type == "income" || (c.Archived && line == default && ytd == 0)) continue;
            if (line == default && ytd == 0) { rows.Add(new BudgetCategoryRow(c.Id, c.Name, c.Type, null, null, 0, 0, false)); continue; }

            var popped = line.Monthly is { } m ? BudgetRules.MonthlyPopped(thisMonth, m, pop, min)
                : line.Annual is { } a && BudgetRules.AnnualPopped(ytd, a);
            rows.Add(new BudgetCategoryRow(c.Id, c.Name, c.Type, line.Monthly, line.Annual, thisMonth, ytd, popped));
        }

        var actualIncome = reports.SpreadFor(new DateOnly(year, 1, 1), ytdEnd).InCents;
        var expectedYtd = head?.Income is { } e && elapsed > 0 ? (long?)Math.Round(e * (decimal)elapsed / 12, MidpointRounding.AwayFromZero) : null;
        var income = new BudgetIncome(head?.Income, actualIncome, expectedYtd, head?.Income is { } ex && BudgetRules.IncomeShort(actualIncome, ex, elapsed));

        return new BudgetView(year, monthKey, elapsed, pop, min, income, rows, head is not null);
    }

    /// <summary>Guidelines from the last twelve whole months, each category's average rounded up to ten dollars. Nothing is saved.</summary>
    public BudgetProposals ProposeFromAverages(int year, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var from = firstOfThisMonth.AddMonths(-12);
        var to = firstOfThisMonth.AddDays(-1);

        var totals = new ReportRepository(conn).SpendByCategoryMonth(from, to).GroupBy(s => s.CategoryId).ToDictionary(g => g.Key, g => g.Sum(s => s.Cents));
        var current = conn.Query<(long Category, long? Monthly, long? Annual)>("""
            SELECT l.category_id, l.monthly_cents, l.annual_cents FROM budget_lines l JOIN budgets b ON b.id = l.budget_id WHERE b.year = @year
            """, new { year }).ToDictionary(l => l.Category);

        var items = conn.Query<(long Id, string Name)>("SELECT id, name FROM categories WHERE is_archived = 0 AND default_type <> 'income' ORDER BY name")
            .Where(c => totals.GetValueOrDefault(c.Id) > 0)
            .Select(c =>
            {
                current.TryGetValue(c.Id, out var line);
                var total = totals[c.Id];
                return new BudgetProposal(c.Id, c.Name, BudgetRules.ProposedMonthly(total), total, line.Monthly, line.Annual);
            })
            .OrderByDescending(p => p.TwelveMonthCents).ToList();
        return new BudgetProposals(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), items);
    }
}
