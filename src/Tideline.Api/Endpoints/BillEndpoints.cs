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

        api.MapPost("/recurring/candidates/add", (AddRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t, Clock clock) =>
            Edits.Run(http, f, t, (c, time, who) =>
            {
                var picks = (request.Items ?? []).Select(p => (p.Key, p.Name)).ToList();
                return new { added = BillAdder.Add(c, time, who, picks, clock.Today).Select(a => new { key = a.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), a.RecurringId, a.Name, a.LinesLinked, a.RuleId }) };
            }, created: true));
    }
}
