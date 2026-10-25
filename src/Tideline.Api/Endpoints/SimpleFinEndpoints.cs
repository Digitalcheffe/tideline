using System.Globalization;
using Dapper;
using Tideline.Api.Auth;
using Tideline.Data;
using Tideline.Data.Services;
using Tideline.Import.SimpleFin;

namespace Tideline.Api.Endpoints;

/// <summary>Connecting SimpleFIN, matching its accounts to ours, syncing, and a dry run. Owner only: it all lives under /imports.</summary>
public static class SimpleFinEndpoints
{
    public sealed record ConnectRequest(string? SetupToken);
    public sealed record DryRunRequest(long AccountId, string? From, string? To);

    public static void MapSimpleFinEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/imports/simplefin", (SqliteConnectionFactory f, SimpleFinSync sync) =>
        {
            TypeHandlers.EnsureRegistered();
            var status = sync.Status();
            using var conn = f.Open();
            var matched = conn.Query<(long Id, string Name, string Remote, DateOnly? From)>("SELECT id, name, simplefin_account_id, simplefin_from_on FROM accounts WHERE simplefin_account_id IS NOT NULL ORDER BY id")
                .Select(a => new { accountId = a.Id, name = a.Name, simplefinAccountId = a.Remote, takesOverOn = a.From });
            return Results.Ok(new
            {
                status.Connected, status.LastSyncAt, status.LastError, requestsToday = status.RequestsToday, requestLimit = SimpleFinSync.MaxRequestsPerDay,
                everyHours = (int)SimpleFinHost.Every.TotalHours, matched,
            });
        });

        api.MapPost("/imports/simplefin/connect", async (ConnectRequest request, HttpContext http, SimpleFinSync sync, SqliteConnectionFactory f, TimeProvider t, CancellationToken ct) =>
        {
            try
            {
                var id = await sync.ConnectAsync(request.SetupToken ?? "", http.Actor(), ct);
                return Results.Json(new { id }, statusCode: 201);
            }
            catch (EditException e)
            {
                return Results.Problem(title: "Check the request", detail: e.Message, statusCode: e.Kind == EditError.NotFound ? 404 : 400);
            }
        });

        api.MapDelete("/imports/simplefin", (HttpContext http, SimpleFinSync sync) =>
        {
            try { sync.Disconnect(http.Actor()); return Results.Ok(new { disconnected = true }); }
            catch (EditException e) { return Results.Problem(title: "Not found", detail: e.Message, statusCode: 404); }
        });

        api.MapGet("/imports/simplefin/accounts", async (SimpleFinSync sync, CancellationToken ct) =>
        {
            try { return Results.Ok(await sync.RemoteAccountsAsync(ct)); }
            catch (EditException e) { return Problem(e); }
        });

        api.MapPost("/imports/simplefin/sync", async (HttpContext http, SimpleFinSync sync, SqliteConnectionFactory f, TimeProvider t, CancellationToken ct) =>
        {
            try
            {
                var report = await sync.SyncAsync(ct);
                using var conn = f.Open();
                using var tx = conn.BeginTransaction();
                new AuditWriter(conn, t, tx, http.Actor()).Write("simplefin.sync", "simplefin", (string?)null, null,
                    new { accounts = report.Accounts.Count, inserted = report.Accounts.Sum(a => a.Inserted), report.Requests, errors = report.Errors.Count });
                tx.Commit();
                return Results.Ok(report);
            }
            catch (EditException e) { return Problem(e); }
        });

        // sets SimpleFIN's lines beside the CSV's for the same days and shows what would land differently; nothing is written
        api.MapPost("/imports/simplefin/dry-run", async (DryRunRequest request, SimpleFinSync sync, CancellationToken ct) =>
        {
            if (!DateOnly.TryParseExact(request.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateOnly.TryParseExact(request.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                return Results.Problem(title: "Check the request", detail: "'from' and 'to' must be dates like 2026-03-31.", statusCode: 400);
            try { return Results.Ok(await sync.DryRunAsync(request.AccountId, from, to, ct)); }
            catch (EditException e) { return Problem(e); }
        });
    }

    private static IResult Problem(EditException e) => e.Kind switch
    {
        EditError.NotFound => Results.Problem(title: "Not found", detail: e.Message, statusCode: 404),
        EditError.Unprocessable => Results.Problem(title: "Cannot do that", detail: e.Message, statusCode: 422),
        _ => Results.Problem(title: "Check the request", detail: e.Message, statusCode: 400),
    };
}
