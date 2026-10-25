using System.Data;
using System.Text;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Reports;

public enum MoveMode { Exclude, Include, Only }

public enum LineSort { DateDesc, DateAsc, AmountDesc, AmountAsc }

public enum LineDirection { Any, In, Out }

/// <summary>The drill-down contract (docs/07-api.md): one filter shape opens the lines behind any number.</summary>
public sealed record LineFilter(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<LineType>? Types = null,
    IReadOnlyList<long>? CategoryIds = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<long>? AccountIds = null,
    long? RecurringId = null,
    bool RecurringNone = false,
    long? PlanId = null,
    MoveMode Move = MoveMode.Exclude,
    bool ReviewOnly = false,
    string? Text = null,
    long? MinCents = null,
    long? MaxCents = null,
    LineSort Sort = LineSort.DateDesc,
    LineDirection Direction = LineDirection.Any,
    bool RecurringOutsideBills = false,
    long? AssetId = null);

public sealed record LineItem(
    long TransactionId,
    long? SplitLineId,
    DateOnly PostedOn,
    long AccountId,
    string AccountName,
    long AmountCents,
    string Description,
    string? Payee,
    long? CategoryId,
    string? CategoryName,
    string? Type,
    IReadOnlyList<string> Tags,
    bool IsSplitPart,
    long? RecurringId,
    long? PlanId,
    long? MoveId);

public sealed record LineSummary(long Count, long SumCents);

public sealed record LinePage(IReadOnlyList<LineItem> Items, int Page, int PageSize, long Total, LineSummary Summary);

public sealed class LinesRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    public const int MaxPageSize = 500;

    private sealed class Row
    {
        public long TransactionId { get; set; }
        public long? SplitLineId { get; set; }
        public DateOnly PostedOn { get; set; }
        public long AccountId { get; set; }
        public string AccountName { get; set; } = "";
        public long AmountCents { get; set; }
        public string Description { get; set; } = "";
        public string? Payee { get; set; }
        public long? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? Type { get; set; }
        public bool IsSplitPart { get; set; }
        public long? RecurringId { get; set; }
        public long? PlanId { get; set; }
        public long? MoveId { get; set; }
    }

    private sealed class TagRow
    {
        public long TransactionId { get; set; }
        public long? SplitLineId { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>The set of lines the filter selects, as SQL over the shared CTEs, plus its parameters.</summary>
    internal static (string Selected, DynamicParameters Args) Build(LineFilter f)
    {
        var types = f.Types is { Count: > 0 } ? f.Types : null;
        var wantsSavings = types?.Contains(LineType.Savings) == true;

        var sets = new List<string>();
        switch (f.Move)
        {
            case MoveMode.Exclude:
                sets.Add($"SELECT {EffectiveSql.Columns} FROM effective");
                if (wantsSavings) sets.Add($"SELECT {EffectiveSql.Columns} FROM savings_legs");
                break;
            case MoveMode.Include:
                sets.Add($"SELECT {EffectiveSql.Columns} FROM effective");
                sets.Add($"SELECT {EffectiveSql.Columns} FROM move_legs");
                break;
            case MoveMode.Only:
                sets.Add($"SELECT {EffectiveSql.Columns} FROM move_legs");
                break;
        }

        var args = new DynamicParameters();
        var where = new StringBuilder("e.posted_on BETWEEN @from AND @to");
        args.Add("from", f.From);
        args.Add("to", f.To);

        if (types is not null)
        {
            where.Append(" AND e.type IN @types AND a.is_in_spread = 1");
            args.Add("types", types.Select(t => t.ToDb()).ToArray());
        }
        if (f.CategoryIds is { Count: > 0 })
        {
            // category 0 means "no category"
            where.Append(f.CategoryIds.Contains(0) ? " AND (e.category_id IN @categories OR e.category_id IS NULL)" : " AND e.category_id IN @categories");
            args.Add("categories", f.CategoryIds);
        }
        if (f.AccountIds is { Count: > 0 }) { where.Append(" AND e.account_id IN @accounts"); args.Add("accounts", f.AccountIds); }
        if (f.RecurringId is { } r) { where.Append(" AND e.recurring_id = @recurring"); args.Add("recurring", r); }
        if (f.RecurringNone) where.Append(" AND e.recurring_id IS NULL");
        if (f.RecurringOutsideBills) where.Append($" AND ({EffectiveSql.OutsideBillRows})");
        if (f.PlanId is { } p) { where.Append(" AND e.plan_id = @plan"); args.Add("plan", p); }
        if (f.AssetId is { } asset) { where.Append(" AND e.asset_account_id = @asset"); args.Add("asset", asset); }
        if (f.Direction == LineDirection.In) where.Append(" AND e.amount_cents > 0");
        if (f.Direction == LineDirection.Out) where.Append(" AND e.amount_cents < 0");
        if (f.MinCents is { } min) { where.Append(" AND ABS(e.amount_cents) >= @min"); args.Add("min", min); }
        if (f.MaxCents is { } max) { where.Append(" AND ABS(e.amount_cents) <= @max"); args.Add("max", max); }

        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            where.Append(" AND (LOWER(e.description) LIKE @q OR LOWER(COALESCE(e.payee, '')) LIKE @q OR LOWER(COALESCE(e.notes, '')) LIKE @q)");
            args.Add("q", "%" + f.Text.Trim().ToLowerInvariant().Replace("%", "").Replace("_", "") + "%");
        }

        var tags = (f.Tags ?? []).Concat(f.ReviewOnly ? ["review"] : []).Select(t => t.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (tags.Length > 0)
        {
            where.Append("""
                 AND (SELECT COUNT(*) FROM tags g WHERE g.name IN @tags AND (
                        (e.split_line_id IS NULL AND EXISTS (SELECT 1 FROM transaction_tags tt WHERE tt.transaction_id = e.transaction_id AND tt.tag_id = g.id))
                     OR (e.split_line_id IS NOT NULL AND EXISTS (SELECT 1 FROM split_line_tags st WHERE st.split_line_id = e.split_line_id AND st.tag_id = g.id))
                 )) = @tagCount
                """);
            args.Add("tags", tags);
            args.Add("tagCount", tags.Length);
        }

        var selected = $"""
            WITH {EffectiveSql.Ctes},
            lines AS ({string.Join(" UNION ALL ", sets)})
            SELECT e.*, a.name AS account_name FROM lines e
            JOIN accounts a ON a.id = e.account_id
            WHERE {where}
            """;
        return (selected, args);
    }

    public LinePage Query(LineFilter filter, int page = 1, int pageSize = 100)
    {
        TypeHandlers.EnsureRegistered();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (selected, args) = Build(filter);

        var summary = conn.QuerySingle<(long Count, long Sum)>(
            $"SELECT COUNT(*), COALESCE(SUM(amount_cents), 0) FROM ({selected}) s", args, tx);

        var order = filter.Sort switch
        {
            LineSort.DateAsc => "e.posted_on ASC, e.transaction_id ASC, e.split_line_id ASC",
            LineSort.AmountDesc => "e.amount_cents DESC, e.transaction_id DESC",
            LineSort.AmountAsc => "e.amount_cents ASC, e.transaction_id ASC",
            _ => "e.posted_on DESC, e.transaction_id DESC, e.split_line_id DESC",
        };
        args.Add("take", pageSize);
        args.Add("skip", (page - 1) * pageSize);

        var rows = conn.Query<Row>($"""
            SELECT e.transaction_id AS TransactionId, e.split_line_id AS SplitLineId, e.posted_on AS PostedOn, e.account_id AS AccountId,
                   e.account_name AS AccountName, e.amount_cents AS AmountCents, e.description AS Description, e.payee AS Payee,
                   e.category_id AS CategoryId, (SELECT c.name FROM categories c WHERE c.id = e.category_id) AS CategoryName,
                   e.type AS Type, e.is_split_part AS IsSplitPart, e.recurring_id AS RecurringId, e.plan_id AS PlanId, e.move_id AS MoveId
            FROM ({selected}) e
            ORDER BY {order} LIMIT @take OFFSET @skip
            """, args, tx).ToList();

        var tagsByLine = LoadTags(rows);
        var items = rows.Select(r => new LineItem(r.TransactionId, r.SplitLineId, r.PostedOn, r.AccountId, r.AccountName, r.AmountCents,
            r.Description, r.Payee, r.CategoryId, r.CategoryName, r.Type,
            tagsByLine.GetValueOrDefault((r.TransactionId, r.SplitLineId)) ?? [], r.IsSplitPart, r.RecurringId, r.PlanId, r.MoveId)).ToList();

        return new LinePage(items, page, pageSize, summary.Count, new LineSummary(summary.Count, summary.Sum));
    }

    /// <summary>Count and signed sum for a filter, without fetching lines. Used by the drill-consistency checks.</summary>
    public LineSummary Summarize(LineFilter filter)
    {
        var (selected, args) = Build(filter);
        var (count, sum) = conn.QuerySingle<(long, long)>(
            $"SELECT COUNT(*), COALESCE(SUM(amount_cents), 0) FROM ({selected}) s", args, tx);
        return new LineSummary(count, sum);
    }

    private Dictionary<(long, long?), List<string>> LoadTags(List<Row> rows)
    {
        var result = new Dictionary<(long, long?), List<string>>();
        if (rows.Count == 0) return result;

        var txIds = rows.Select(r => r.TransactionId).Distinct().ToArray();
        var splitIds = rows.Where(r => r.SplitLineId is not null).Select(r => r.SplitLineId!.Value).ToArray();

        foreach (var chunk in txIds.Chunk(500))
            foreach (var t in conn.Query<TagRow>("""
                SELECT tt.transaction_id AS TransactionId, g.name AS Name
                FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.transaction_id IN @chunk ORDER BY g.name
                """, new { chunk }, tx))
                result.GetOrAdd((t.TransactionId, null)).Add(t.Name);

        foreach (var chunk in splitIds.Chunk(500))
            foreach (var t in conn.Query<(long SplitLineId, long TransactionId, string Name)>("""
                SELECT st.split_line_id, sl.transaction_id, g.name
                FROM split_line_tags st JOIN tags g ON g.id = st.tag_id JOIN split_lines sl ON sl.id = st.split_line_id
                WHERE st.split_line_id IN @chunk ORDER BY g.name
                """, new { chunk }, tx))
                result.GetOrAdd((t.TransactionId, t.SplitLineId)).Add(t.Name);

        return result;
    }
}

internal static class DictionaryExtensions
{
    public static List<string> GetOrAdd(this Dictionary<(long, long?), List<string>> d, (long, long?) key)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = [];
        return list;
    }
}
