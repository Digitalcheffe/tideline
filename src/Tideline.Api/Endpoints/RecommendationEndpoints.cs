using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Reports;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>Suggestions on flags (docs/07-api.md, "Recommendations"). Anyone signed in can read; members can change the rules and dismiss cards.</summary>
public static class RecommendationEndpoints
{
    public sealed record DismissRequest(long RuleId, long FlagId);

    public static void MapRecommendationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/recommendations", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RecommendationReader(conn).Cards().Select(c => new
            {
                c.RuleId, c.RuleName, c.Suggestion, c.FlagId, c.FlagKind, c.Severity, c.FlagTitle, c.FlagDetail, c.Acknowledged,
                what = FlagKinds.Describe(c.FlagKind), link = Links.For(c.FlagKind, c.Period),
            }));
        });

        api.MapPost("/recommendations/dismiss", (DismissRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RecommendationEditor(c, clock, who).Dismiss(request.RuleId, request.FlagId); return new { dismissed = true }; }));

        api.MapGet("/recommendation-rules", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(new { kinds = FlagKinds.All.Select(k => new { kind = k, label = FlagKinds.Describe(k) }), rules = new RecommendationReader(conn).Rules() });
        });

        api.MapPost("/recommendation-rules", (RecommendationRuleInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new RecommendationEditor(c, clock, who).Create(input) }, created: true));

        api.MapPatch("/recommendation-rules/{id:long}", (long id, RecommendationRuleInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RecommendationEditor(c, clock, who).Update(id, input); return new { updated = true }; }));

        api.MapDelete("/recommendation-rules/{id:long}", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RecommendationEditor(c, clock, who).Delete(id); return new { deleted = true }; }));
    }
}
