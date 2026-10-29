using System.Text.Json;
using Tideline.Api.Auth;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/transactions/{id:long}", (long id, SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            var t = new TransactionDetailRepository(conn).Get(id);
            if (t is null) return Results.Problem(title: "Not found", detail: $"There is no transaction {id}.", statusCode: 404);

            return Results.Ok(new
            {
                t.Id,
                bank = new
                {
                    t.AccountId,
                    t.AccountName,
                    t.PostedOn,
                    t.AmountCents,
                    description = t.OriginalDescription,
                    t.BankBalanceCents,
                    t.CheckNumber,
                    t.ExternalId,
                    t.Source,
                },
                raw = ParseRaw(t.RawJson),
                t.Payee,
                t.CategoryId,
                t.CategoryName,
                t.Type,
                t.Tags,
                t.Notes,
                t.IsManualOverride,
                rule = t.RuleId is null ? null : new { id = t.RuleId, name = t.RuleName },
                recurring = t.RecurringId is null ? null : new { id = t.RecurringId, name = t.RecurringName },
                t.PlanId,
                asset = t.AssetId is null ? null : new { id = t.AssetId, name = t.AssetName },
                t.MoveId,
                t.MovePartner,
                t.Splits,
                t.UnassignedCents,
                version = t.Version,
                audit = t.Audit,
                leftOut = t.LeftOutAt is null ? null : new { at = t.LeftOutAt, reason = t.LeftOutReason, importId = t.LeftOutImportId },
            });
        });

        // docs/ARCHITECTURE.md: every edit is audited, sets is_manual_override, and is refused (409) if the line changed since it was read.
        api.MapPatch("/transactions/{id:long}", (long id, EditRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { version = e.Update(id, request) }));

        // the quick answer to "what was this line?" from the needs-a-category list: a real category and, if known, who it was
        api.MapPost("/transactions/{id:long}/answer", (long id, AnswerRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new
            {
                version = e.Answer(id, request.CategoryId ?? throw new EditException(EditError.Invalid, "Pick a category."), request.Payee),
            }));

        api.MapPost("/transactions/{id:long}/reset-to-rules", (long id, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { version = e.ResetToRules(id) }));

        api.MapPut("/transactions/{id:long}/splits", (long id, SplitsRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { version = e.ReplaceSplits(id, request.Splits ?? [], request.Version) }));

        api.MapPost("/transactions/bulk", (BulkRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => e.BulkUpdate(request.Ids ?? [], request.Set ?? new BulkChange())));

        // docs/ARCHITECTURE.md: pair two lines by hand, or take a move apart (both lines go back to the rules)
        api.MapPost("/moves", (PairRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { id = e.Pair(request.TransactionIds ?? []) }, created: true));

        api.MapDelete("/moves/{id:long}", (long id, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { unpaired = e.Unpair(id) }));

        api.MapPost("/transactions", (ManualEntry entry, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { id = e.CreateManual(entry) }, created: true));

        api.MapDelete("/transactions/{id:long}", (long id, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => { e.DeleteManual(id); return new { deleted = true }; }));

        // leave lines out of every total without touching them, and bring them back (docs/ARCHITECTURE.md)
        api.MapPost("/transactions/leave-out", (LeaveOutRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => e.LeaveOut(request.Ids ?? [], request.Reason)));

        api.MapPost("/transactions/bring-back", (IdsRequest request, SqliteConnectionFactory factory, TimeProvider time, HttpContext http) =>
            Edit(http, factory, time, e => new { broughtBack = e.BringBack(request.Ids ?? []) }));
    }

    public sealed record LeaveOutRequest(IReadOnlyList<long>? Ids, string? Reason);

    public sealed record IdsRequest(IReadOnlyList<long>? Ids);

    public sealed record BulkRequest(IReadOnlyList<long>? Ids, BulkChange? Set);

    public sealed record PairRequest(IReadOnlyList<long>? TransactionIds);

    public sealed record SplitsRequest(string? Version, IReadOnlyList<SplitInput>? Splits);

    private static IResult Edit(HttpContext http, SqliteConnectionFactory factory, TimeProvider time, Func<TransactionEditor, object> action, bool created = false)
    {
        try
        {
            using var conn = factory.Open();
            var result = action(new TransactionEditor(conn, time, http.Actor()));
            return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
        }
        catch (EditException e)
        {
            var (status, title) = e.Kind switch
            {
                EditError.NotFound => (404, "Not found"),
                EditError.Conflict => (409, "Conflict"),
                EditError.Unprocessable => (422, "Cannot do that"),
                _ => (400, "Check the request"),
            };
            return Results.Problem(title: title, detail: e.Message, statusCode: status);
        }
    }

    private static object? ParseRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return null; }
    }
}
