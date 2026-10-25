using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Services;
using Tideline.Notify;

namespace Tideline.Api.Endpoints;

public sealed record FlagDto(long Id, string Kind, string Severity, string Title, string Detail, string SubjectType, long SubjectId, string Period, string Link, DateTimeOffset CreatedAt,
    bool Acknowledged, string? AckNote, bool Resolved)
{
    public static FlagDto From(FlagRow f)
    {
        var (title, detail) = FlagText.Split(f.Message);
        return new FlagDto(f.Id, f.Kind, f.Severity, title, detail, f.SubjectType, f.SubjectId, f.Period, Links.For(f.Kind, f.Period), f.CreatedAt, f.AcknowledgedAt is not null, f.AckNote, f.ResolvedAt is not null);
    }
}

/// <summary>Where in the app a flag's subject can be looked at: a path, to which a host is added when it goes in a message.</summary>
public static class Links
{
    public static string For(string kind, string period)
    {
        var year = period.Length >= 4 ? period[..4] : DateTime.UtcNow.Year.ToString();
        var month = period.Length >= 7 ? period[..7] : null;
        string Range(string extra = "") => month is null ? "/lines" : $"/lines?from={month}-01&to={month}-{DateTime.DaysInMonth(int.Parse(month[..4]), int.Parse(month[5..7])):00}{extra}";

        return kind switch
        {
            FlagKinds.RecurringMissing or FlagKinds.RecurringOutOfRange or FlagKinds.OneTimeReview => $"/bills?year={year}",
            FlagKinds.CardNotPaidInFull or FlagKinds.CardInterest => month is null ? "/cards" : $"/cards?month={month}",
            FlagKinds.BalanceMismatch or FlagKinds.ImportFailed => "/settings/imports",
            FlagKinds.BudgetPop or FlagKinds.IncomeShort => $"/budget?from={year}-01-01&to={year}-12-31",
            FlagKinds.PlanWavered or FlagKinds.PlanBehind => "/planning",
            FlagKinds.RetirementGapGrew => "/retirement",
            FlagKinds.ReviewNeeded => "/lines?review=true",
            FlagKinds.NewRecurring => $"/extra?year={year}",
            _ => Range(),
        };
    }
}

public static class FlagEndpoints
{
    public sealed record AcknowledgeRequest(string? Note);

    public static void MapFlagEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/flags", (string? state, SqliteConnectionFactory f, TimeProvider t) =>
        {
            using var conn = f.Open();
            var flags = new FlagRepository(conn, t);
            return Results.Ok((state == "all" ? flags.Recent() : flags.Open()).Select(FlagDto.From));
        });

        api.MapPost("/flags/{id:long}/acknowledge", (long id, AcknowledgeRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new FlagEditor(c, clock, who).Acknowledge(id, request.Note); return new { acknowledged = true }; }));

        // run the evaluators now, instead of waiting for tonight (owner only: it is under /notify)
        api.MapPost("/notify/evaluate", (HttpContext http, SqliteConnectionFactory f, TimeProvider t, Clock clock, Evaluator evaluator) =>
            Edits.Run(http, f, t, (c, time, who) =>
            {
                var result = evaluator.Run(c, clock.Today, time);
                using var tx = c.BeginTransaction();
                new AuditWriter(c, time, tx, who).Write("evaluator.run", "evaluator", (string?)null, null, new { result.Found, result.Created, result.Resolved });
                tx.Commit();
                return new { result.Found, result.Created, result.Resolved };
            }));
    }
}
