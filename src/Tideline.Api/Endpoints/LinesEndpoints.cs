using System.Globalization;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Reports;

namespace Tideline.Api.Endpoints;

public static class LinesEndpoints
{
    public static void MapLinesEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/lines", (HttpRequest request, SqliteConnectionFactory factory) =>
        {
            if (!LineFilterParser.TryParse(request.Query, out var filter, out var errors))
                return Results.ValidationProblem(errors);

            var page = int.TryParse(request.Query["page"], out var p) ? p : 1;
            var pageSize = int.TryParse(request.Query["pageSize"], out var ps) ? ps : 100;

            using var conn = factory.Open();
            var result = new LinesRepository(conn).Query(filter!, page, pageSize);
            return Results.Ok(new
            {
                items = result.Items.Select(LineDto.From),
                page = result.Page,
                pageSize = result.PageSize,
                total = result.Total,
                summary = new { count = result.Summary.Count, sumCents = result.Summary.SumCents },
            });
        });
    }
}

public sealed record LineDto(
    long TransactionId, long? SplitLineId, string PostedOn, long AccountId, string AccountName, long AmountCents,
    string Description, string? Payee, long? CategoryId, string? CategoryName, string? Type, IReadOnlyList<string> Tags,
    bool IsSplitPart, long? RecurringId, long? PlanId, long? MoveId, bool IsLeftOut)
{
    public static LineDto From(LineItem l) => new(l.TransactionId, l.SplitLineId, l.PostedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        l.AccountId, l.AccountName, l.AmountCents, l.Description, l.Payee, l.CategoryId, l.CategoryName, l.Type, l.Tags,
        l.IsSplitPart, l.RecurringId, l.PlanId, l.MoveId, l.IsLeftOut);
}

/// <summary>Turns the query string of the drill-down contract into a <see cref="LineFilter"/>.</summary>
public static class LineFilterParser
{
    public static bool TryParse(IQueryCollection q, out LineFilter? filter, out Dictionary<string, string[]> errors)
    {
        var errs = new Dictionary<string, string[]>();
        errors = errs;
        filter = null;

        DateOnly? Date(string key)
        {
            if (!q.TryGetValue(key, out var v) || v.Count == 0) { errs[key] = [$"'{key}' is required (YYYY-MM-DD)."]; return null; }
            if (DateOnly.TryParseExact(v[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            errs[key] = [$"'{key}' must be YYYY-MM-DD."];
            return null;
        }

        string[] List(string key) => q.TryGetValue(key, out var v)
            ? v.SelectMany(x => (x ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray()
            : [];

        var from = Date("from");
        var to = Date("to");
        if (from is not null && to is not null && from > to) errs["to"] = ["'to' is before 'from'."];

        List<LineType>? types = null;
        var typeTexts = List("type");
        if (typeTexts.Length > 0)
        {
            types = [];
            foreach (var t in typeTexts)
            {
                try { types.Add(TypeHandlers.FromDb<LineType>(t)); }
                catch (InvalidDataException) { errs["type"] = [$"Unknown type '{t}'."]; }
            }
        }

        List<long>? Longs(string key)
        {
            var texts = List(key);
            if (texts.Length == 0) return null;
            var result = new List<long>();
            foreach (var t in texts)
            {
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) result.Add(n);
                else errs[key] = [$"'{t}' is not a number."];
            }
            return result;
        }

        long? OneLong(string key)
        {
            var text = q[key].ToString();
            if (text.Length == 0) return null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            errs[key] = [$"'{key}' must be a number."];
            return null;
        }

        var categories = Longs("category");
        var accounts = Longs("account");

        var recurringText = q["recurring"].ToString();
        var recurringNone = string.Equals(recurringText, "none", StringComparison.OrdinalIgnoreCase);
        var recurringOther = string.Equals(recurringText, "other", StringComparison.OrdinalIgnoreCase);
        long? recurring = recurringNone || recurringOther ? null : OneLong("recurring");

        var move = MoveMode.Exclude;
        var moveText = q["move"].ToString();
        if (moveText.Length > 0 && !Enum.TryParse(moveText, ignoreCase: true, out move)) errs["move"] = ["'move' must be exclude, include or only."];

        var sort = LineSort.DateDesc;
        var sortText = q["sort"].ToString().Replace("_", "");
        if (sortText.Length > 0 && !Enum.TryParse(sortText, ignoreCase: true, out sort)) errs["sort"] = ["'sort' must be date_desc, date_asc, amount_desc or amount_asc."];

        var direction = LineDirection.Any;
        var dirText = q["direction"].ToString();
        if (dirText.Length > 0 && !Enum.TryParse(dirText, ignoreCase: true, out direction)) errs["direction"] = ["'direction' must be in or out."];

        var min = OneLong("minCents");
        var max = OneLong("maxCents");
        var plan = OneLong("plan");
        var asset = OneLong("asset");

        if (errs.Count > 0) return false;

        filter = new LineFilter(from!.Value, to!.Value, types, categories, List("tag"), accounts, recurring, recurringNone, plan, move,
            string.Equals(q["review"].ToString(), "true", StringComparison.OrdinalIgnoreCase),
            q["q"].ToString() is { Length: > 0 } text ? text : null, min, max, sort, direction, recurringOther, asset,
            string.Equals(q["leftOut"].ToString(), "true", StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
