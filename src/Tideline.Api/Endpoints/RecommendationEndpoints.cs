using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Reports;
using Tideline.Data.Services;
using Tideline.Notify;

namespace Tideline.Api.Endpoints;

/// <summary>
/// Suggestions (docs/ARCHITECTURE.md). The app ships them; anyone signed in can read; members can switch one on or off and mark a card reviewed.
/// Whether one sends a message is the owner's, set with the other notification choices.
/// </summary>
public static class RecommendationEndpoints
{
    public sealed record DismissRequest(long RuleId, long FlagId);

    public sealed record SwitchRequest(bool? IsActive);

    public static void MapRecommendationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/recommendations", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RecommendationReader(conn).Cards().Select(c => new
            {
                c.RuleId, c.RuleName, c.Suggestion, c.Note, c.Action, c.FlagId, c.FlagKind, c.Severity, c.FlagTitle, c.FlagDetail, c.Acknowledged, c.Period,
                what = FlagKinds.Describe(c.FlagKind), link = Links.For(c.FlagKind, c.Period, c.SubjectType, c.SubjectId),
            }));
        });

        api.MapPost("/recommendations/dismiss", (DismissRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RecommendationEditor(c, clock, who).Dismiss(request.RuleId, request.FlagId); return new { dismissed = true }; }));

        // every suggestion the app ships, with its switch, and for the ones that send their own message, whether it is sent
        api.MapGet("/recommendation-rules", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            var modes = Notifier.Modes(conn);
            return Results.Ok(new
            {
                rules = new RecommendationReader(conn).Rules().Select(r => new
                {
                    r.Id, r.Key, r.Group, r.Title, r.Advice, r.Note, r.Action, r.Recurrence, r.IsActive, r.HasOwnMessage, r.SortOrder,
                    notify = r.HasOwnMessage ? modes[r.Key] != NotifyMode.Off : (bool?)null,
                }),
            });
        });

        api.MapPatch("/recommendation-rules/{id:long}", (long id, SwitchRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) =>
            {
                if (request.IsActive is not { } on) throw new EditException(EditError.Invalid, "Say whether the suggestion is on or off.");
                new RecommendationEditor(c, clock, who).SetOn(id, on);
                return new { updated = true };
            }));
    }
}
