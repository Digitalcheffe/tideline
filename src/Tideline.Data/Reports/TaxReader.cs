using System.Data;
using Dapper;

namespace Tideline.Data.Reports;

/// <summary>One calendar year of tax lines: what was paid, what came back, and property tax on its own. The ids are for the drill-down links.</summary>
public sealed record TaxYear(int Year, long PaidCents, long PropertyCents, long RefundedCents);

public sealed record TaxYears(IReadOnlyList<TaxYear> Years, IReadOnlyList<long> PaidIds, IReadOnlyList<long> PropertyIds, IReadOnlyList<long> RefundIds);

/// <summary>
/// Taxes by year, from the Taxes, Property tax and Tax refund categories. The bank cannot show what is withheld from a paycheck, so this is payments
/// made, property tax and refunds received. It is read from the same lines the screens count.
/// </summary>
public static class TaxReader
{
    public static TaxYears Read(IDbConnection conn, int lastYear, int years = 6)
    {
        TypeHandlers.EnsureRegistered();
        IReadOnlyList<long> Ids(params string[] names) =>
            conn.Query<long>("SELECT id FROM categories WHERE LOWER(name) IN @names", new { names = names.Select(n => n.ToLowerInvariant()).ToArray() }).ToList();

        var paid = Ids("Taxes");
        var property = Ids("Property tax");
        var refund = Ids("Tax refund", "Tax refunds");

        long Out(IReadOnlyList<long> ids, int year)
        {
            if (ids.Count == 0) return 0;
            return conn.ExecuteScalar<long?>($"""
                WITH {EffectiveSql.Ctes}
                SELECT SUM(-e.amount_cents) FROM effective e WHERE e.category_id IN @ids AND e.amount_cents < 0 AND e.posted_on BETWEEN @from AND @to
                """, new { ids, from = new DateOnly(year, 1, 1), to = new DateOnly(year, 12, 31) }) ?? 0;
        }

        long In(IReadOnlyList<long> ids, int year)
        {
            if (ids.Count == 0) return 0;
            return conn.ExecuteScalar<long?>($"""
                WITH {EffectiveSql.Ctes}
                SELECT SUM(e.amount_cents) FROM effective e WHERE e.category_id IN @ids AND e.amount_cents > 0 AND e.posted_on BETWEEN @from AND @to
                """, new { ids, from = new DateOnly(year, 1, 1), to = new DateOnly(year, 12, 31) }) ?? 0;
        }

        var rows = new List<TaxYear>();
        for (var y = lastYear - years + 1; y <= lastYear; y++)
        {
            var year = new TaxYear(y, Out(paid, y), Out(property, y), In(refund.Concat(paid).Distinct().ToList(), y));
            if (rows.Count > 0 || year.PaidCents + year.PropertyCents + year.RefundedCents > 0) rows.Add(year); // start at the first year with anything
        }
        return new TaxYears(rows, paid, property, refund.Concat(paid).Distinct().ToList());
    }
}
