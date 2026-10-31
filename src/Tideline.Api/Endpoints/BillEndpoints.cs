using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>
/// Find my bills (docs/ARCHITECTURE.md): the bills and subscriptions hiding in untracked lines, and adding the ones a person picks. Reading needs any
/// signed-in person; adding is a change, so a member or the owner. Nothing is added without being asked.
/// </summary>
public static class BillEndpoints
{
    public sealed record Pick(long Key, string? Name);

    public sealed record AddRequest(IReadOnlyList<Pick>? Items);

    public static void MapBillEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/recurring/candidates", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(BillFinder.Find(conn, clock.Today).Select(c => new
            {
                key = c.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), // a number too big for the browser to hold exactly, so it travels as text
                c.Name, c.CategoryName, c.Kind, c.Frequency, c.EveryN, c.MinCents, c.MaxCents, c.DueDay, c.Occurrences, c.LastPaid, c.MonthlyCents,
                lines = c.LineIds.Count, linksNextOnes = c.Match is not null,
                drill = Drill.Range(c.FirstDueOn, c.LastPaid, ("category", c.CategoryId), ("q", c.Match ?? c.Name)),
            }));
        });

        // the quick form: filled in from the lines it is opened with (a category, or lines a person selected)
        api.MapGet("/recurring/suggest", (HttpRequest req, SqliteConnectionFactory f, Clock clock) =>
        {
            long? category = long.TryParse(req.Query["categoryId"], out var c) ? c : null;
            var lines = req.Query["lineIds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => long.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToList();
            using var conn = f.Open();
            try
            {
                var s = QuickRecurring.Suggest(conn, clock.Today, category, lines, req.Query["name"].ToString() is { Length: > 0 } n ? n : null);
                return Results.Ok(new
                {
                    s.Name, s.CategoryId, s.CategoryName, s.Frequency, s.EveryN, s.UsualCents, s.MinCents, s.MaxCents, s.DueDay,
                    firstDueOn = s.FirstDueOn.ToString("yyyy-MM-dd"), linksNextOnes = s.Match is not null,
                    lines = s.Lines.Select(l => new { l.Id, postedOn = l.PostedOn.ToString("yyyy-MM-dd"), l.Description, l.Payee, l.Cents, l.Selected }),
                });
            }
            catch (EditException e)
            {
                return Results.Problem(title: e.Kind == EditError.NotFound ? "Not found" : "Check the request", detail: e.Message, statusCode: e.Kind == EditError.NotFound ? 404 : 400);
            }
        });

        api.MapPost("/recurring/quick", (QuickInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Clock clock) =>
            Edits.Run(http, f, t, (c, time, who) => QuickRecurring.Create(c, time, who, input, clock.Today), created: true));

        api.MapPost("/recurring/candidates/add", (AddRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Clock clock) =>
            Edits.Run(http, f, t, (c, time, who) =>
            {
                var picks = (request.Items ?? []).Select(p => (p.Key, p.Name)).ToList();
                return new { added = BillAdder.Add(c, time, who, picks, clock.Today).Select(a => new { key = a.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), a.RecurringId, a.Name, a.LinesLinked, a.RuleId }) };
            }, created: true));
    }
}
