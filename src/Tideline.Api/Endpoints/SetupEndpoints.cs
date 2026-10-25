using System.Globalization;
using System.Text.Json;
using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>Changes to how the household is set up (docs/07-api.md, "Planning and setup"). The role needed for each is in the access table.</summary>
public static class SetupEndpoints
{
    public sealed record MergeRequest(long IntoId);
    public sealed record SettingsRequest(long? OneTimeAttentionCents);
    public sealed record ReorderRequest(IReadOnlyList<long>? Ids);
    public sealed record RuleTestRequest(RuleInput? Rule, long? RuleId, string? From, string? To);
    public sealed record PeriodRequest(string? From, string? To);

    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        // money accounts: owner only
        api.MapPost("/accounts", (AccountInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new SetupEditor(c, clock, who).CreateAccount(input) }, created: true));
        api.MapPatch("/accounts/{id:long}", (long id, AccountInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).UpdateAccount(id, input); return new { updated = true }; }));

        // which transfers pair up: owner only
        api.MapPost("/move-patterns", (PatternInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new SetupEditor(c, clock, who).CreatePattern(input) }, created: true));
        api.MapPatch("/move-patterns/{id:long}", (long id, PatternInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).UpdatePattern(id, input); return new { updated = true }; }));
        api.MapDelete("/move-patterns/{id:long}", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).DeletePattern(id); return new { deleted = true }; }));

        api.MapPost("/categories", (CategoryInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new SetupEditor(c, clock, who).CreateCategory(input) }, created: true));
        api.MapPatch("/categories/{id:long}", (long id, CategoryInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).UpdateCategory(id, input); return new { updated = true }; }));
        api.MapPost("/categories/{id:long}/merge", (long id, MergeRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { rowsMoved = new SetupEditor(c, clock, who).MergeCategory(id, request.IntoId) }));

        api.MapPost("/tags", (TagInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new SetupEditor(c, clock, who).CreateTag(input) }, created: true));
        api.MapPatch("/tags/{id:long}", (long id, TagInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).UpdateTag(id, input); return new { updated = true }; }));
        api.MapDelete("/tags/{id:long}", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { removedFrom = new SetupEditor(c, clock, who).DeleteTag(id) }));

        api.MapPost("/recurring", (RecurringInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new SetupEditor(c, clock, who).CreateRecurring(input) }, created: true));
        api.MapPatch("/recurring/{id:long}", (long id, RecurringInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new SetupEditor(c, clock, who).UpdateRecurring(id, input); return new { updated = true }; }));

        // rules
        api.MapGet("/rules", (SqliteConnectionFactory f, TimeProvider t) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RuleEditor(conn, t).List());
        });
        api.MapPost("/rules", (RuleInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new RuleEditor(c, clock, who).Create(input) }, created: true));
        api.MapPatch("/rules/{id:long}", (long id, RuleInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RuleEditor(c, clock, who).Update(id, input); return new { updated = true }; }));
        api.MapDelete("/rules/{id:long}", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RuleEditor(c, clock, who).Delete(id); return new { deleted = true }; }));
        api.MapPost("/rules/reorder", (ReorderRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new RuleEditor(c, clock, who).Reorder(request.Ids ?? []); return new { reordered = true }; }));
        api.MapPost("/rules/test", (RuleTestRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) =>
            {
                if (request.Rule is null) throw new EditException(EditError.Invalid, "Send the rule to try.");
                var (from, to) = Period(request.From, request.To);
                return new RuleEditor(c, clock, who).Test(request.Rule, request.RuleId, from, to);
            }));
        api.MapPost("/rules/{id:long}/apply", (long id, PeriodRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) =>
            {
                var (from, to) = Period(request.From, request.To);
                return new { changed = new RuleEditor(c, clock, who).ApplyToPast(id, from, to) };
            }));

        // the few app settings: owner only
        api.MapGet("/settings", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(new { oneTimeAttentionCents = SetupEditor.OneTimeAttentionCents(conn) });
        });
        api.MapPut("/settings", (SettingsRequest request, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) =>
            {
                if (request.OneTimeAttentionCents is { } cents) new SetupEditor(c, clock, who).SetOneTimeAttention(cents);
                return new { oneTimeAttentionCents = SetupEditor.OneTimeAttentionCents(c) };
            }));
    }

    private static (DateOnly From, DateOnly To) Period(string? from, string? to)
    {
        static DateOnly Parse(string? text, string what) =>
            DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new EditException(EditError.Invalid, $"'{what}' must be a date like 2026-03-31.");
        return (Parse(from, "from"), Parse(to, "to"));
    }
}
