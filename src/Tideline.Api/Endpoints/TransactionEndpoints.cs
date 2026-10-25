using System.Text.Json;
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
                t.MoveId,
                t.MovePartner,
                t.Splits,
                t.UnassignedCents,
                version = t.Version,
                audit = t.Audit,
            });
        });

        // docs/07-api.md, "Ledger": every edit is audited, sets is_manual_override, and is refused (409) if the line changed since it was read.
        api.MapPatch("/transactions/{id:long}", (long id, EditRequest request, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => new { version = e.Update(id, request) }));

        api.MapPost("/transactions/{id:long}/reset-to-rules", (long id, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => new { version = e.ResetToRules(id) }));

        api.MapPut("/transactions/{id:long}/splits", (long id, SplitsRequest request, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => new { version = e.ReplaceSplits(id, request.Splits ?? [], request.Version) }));

        api.MapPost("/transactions/bulk", (BulkRequest request, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => e.BulkUpdate(request.Ids ?? [], request.Set ?? new BulkChange())));

        api.MapPost("/transactions", (ManualEntry entry, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => new { id = e.CreateManual(entry) }, created: true));

        api.MapDelete("/transactions/{id:long}", (long id, SqliteConnectionFactory factory, TimeProvider time) =>
            Edit(factory, time, e => { e.DeleteManual(id); return new { deleted = true }; }));
    }

    public sealed record BulkRequest(IReadOnlyList<long>? Ids, BulkChange? Set);

    public sealed record SplitsRequest(string? Version, IReadOnlyList<SplitInput>? Splits);

    private static IResult Edit(SqliteConnectionFactory factory, TimeProvider time, Func<TransactionEditor, object> action, bool created = false)
    {
        try
        {
            using var conn = factory.Open();
            var result = action(new TransactionEditor(conn, time));
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
