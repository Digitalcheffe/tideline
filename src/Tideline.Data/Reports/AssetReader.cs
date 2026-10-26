using System.Data;
using Dapper;

namespace Tideline.Data.Reports;

public sealed record AssetLoanView(long LoanId, string Name, long? OwedCents, string? AsOf);

public sealed record AssetYear(string Year, long PutInCents, long Lines);

public sealed record AssetCategory(long? CategoryId, string Name, long PutInCents, long Lines);

public sealed record ValuePoint(string AsOf, long Cents, string Source);

/// <summary>
/// One asset (a house, a car): what it is worth, what is owed on it, the equity, and what has been put into it (docs/ARCHITECTURE.md).
/// <c>PutInCents</c> is money out minus refunds on every line linked to the asset, over all time.
/// </summary>
public sealed record AssetView(
    long AssetId, string Name, bool IsClosed,
    long? ValueCents, string? ValueAsOf, bool ValueStale,
    long OwedCents, long? EquityCents, IReadOnlyList<AssetLoanView> Loans,
    long PutInCents, long Lines, string? FirstLineOn, string? LastLineOn,
    long? FirstValueCents, string? FirstValueAsOf, long? ValueChangeCents, long? NetCents,
    IReadOnlyList<AssetYear> ByYear, IReadOnlyList<AssetCategory> ByCategory, IReadOnlyList<ValuePoint> Values);

/// <summary>Asset ledgers: the lines linked to each asset account, set against what the asset is worth now.</summary>
public sealed class AssetReader(IDbConnection conn)
{
    public IReadOnlyList<AssetView> Read(DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var worth = new NetWorthReader(conn);
        var result = new List<AssetView>();
        foreach (var a in conn.Query<(long Id, string Name, bool Closed)>("SELECT id, name, is_closed FROM accounts WHERE kind = 'asset' ORDER BY is_closed, sort_order, id"))
            result.Add(Build(a.Id, a.Name, a.Closed, today, worth));
        return result;
    }

    public AssetView? Get(long id, DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var a = conn.QuerySingleOrDefault<(long Id, string Name, bool Closed)?>("SELECT id, name, is_closed FROM accounts WHERE id = @id AND kind = 'asset'", new { id });
        return a is { } v ? Build(v.Id, v.Name, v.Closed, today, new NetWorthReader(conn)) : null;
    }

    private AssetView Build(long id, string name, bool closed, DateOnly today, NetWorthReader worth)
    {
        var value = worth.Figure(id, today);

        // loans secured by this asset; what is owed is the latest figure for each
        var loans = new List<AssetLoanView>();
        foreach (var l in conn.Query<(long Id, string Name)>("""
            SELECT a.id, a.name FROM loan_terms t JOIN accounts a ON a.id = t.account_id WHERE t.secured_asset_id = @id ORDER BY a.sort_order, a.id
            """, new { id }))
        {
            var f = worth.Figure(l.Id, today);
            loans.Add(new AssetLoanView(l.Id, l.Name, f?.Cents, f?.AsOf));
        }
        var owed = loans.Sum(l => l.OwedCents ?? 0);
        long? equity = value is null ? null : value.Cents - owed;

        // lines linked to the asset: effective lines, so a split part counts for its own share
        var byYear = conn.Query<(string Year, long Cents, long Lines)>($"""
            WITH {EffectiveSql.Ctes}
            SELECT SUBSTR(e.posted_on, 1, 4), SUM(-e.amount_cents), COUNT(*) FROM effective e
            WHERE e.asset_account_id = @id GROUP BY 1 ORDER BY 1 DESC
            """, new { id }).Select(r => new AssetYear(r.Year, r.Cents, r.Lines)).ToList();
        var byCategory = conn.Query<(long? CategoryId, string? Name, long Cents, long Lines)>($"""
            WITH {EffectiveSql.Ctes}
            SELECT e.category_id, c.name, SUM(-e.amount_cents), COUNT(*) FROM effective e LEFT JOIN categories c ON c.id = e.category_id
            WHERE e.asset_account_id = @id GROUP BY e.category_id, c.name ORDER BY 3 DESC, 2
            """, new { id }).Select(r => new AssetCategory(r.CategoryId, r.Name ?? "No category", r.Cents, r.Lines)).ToList();
        var range = conn.QuerySingle<(string? First, string? Last)>($"""
            WITH {EffectiveSql.Ctes}
            SELECT MIN(e.posted_on), MAX(e.posted_on) FROM effective e WHERE e.asset_account_id = @id
            """, new { id });

        var values = conn.Query<(string AsOf, long Cents, string Source)>("""
            SELECT as_of_on, balance_cents, source FROM balance_snapshots WHERE account_id = @id ORDER BY as_of_on, id
            """, new { id }).Select(r => new ValuePoint(r.AsOf, r.Cents, r.Source)).ToList();
        var first = values.FirstOrDefault();

        var putIn = byYear.Sum(y => y.PutInCents);
        long? change = value is not null && first is not null ? value.Cents - first.Cents : null;
        // positive: it is worth more now than it was, after paying for what went into it
        long? net = change is { } c ? c - putIn : null;

        return new AssetView(id, name, closed, value?.Cents, value?.AsOf, value?.Stale ?? false, owed, equity, loans,
            putIn, byYear.Sum(y => y.Lines), range.First, range.Last,
            first?.Cents, first?.AsOf, change, net, byYear, byCategory, values);
    }
}
