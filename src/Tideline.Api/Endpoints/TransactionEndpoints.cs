using System.Text.Json;
using Tideline.Data;
using Tideline.Data.Repositories;

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
    }

    private static object? ParseRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return null; }
    }
}
