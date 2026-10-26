using System.Text;
using Dapper;
using Tideline.Api.Auth;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Services;
using Tideline.Import;

namespace Tideline.Api.Endpoints;

/// <summary>
/// The Imports page (docs/05-import.md): what came in and what failed, how the inbox folder looks, whether each account's ledger
/// agrees with the bank, and a way to send a CSV from a phone through the same pipeline the inbox uses. Owner only.
/// </summary>
public static class ImportEndpoints
{
    public const long MaxUploadBytes = 5 * 1024 * 1024;

    public static void MapImportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/imports", (SqliteConnectionFactory factory, TimeProvider clock, TidelineOptions options) =>
        {
            TypeHandlers.EnsureRegistered();
            using var conn = factory.Open();

            var batches = conn.Query<(long Id, string Source, string? FileName, long? AccountId, string? AccountName, string StartedAt, string? FinishedAt, string Status, int Read, int Inserted, int Existing, int Review, string? Error)>("""
                SELECT b.id, b.source, b.file_name, b.account_id, a.name, b.started_at, b.finished_at, b.status, b.rows_read, b.rows_inserted, b.rows_existing, b.rows_review, b.error
                FROM import_batches b LEFT JOIN accounts a ON a.id = b.account_id
                WHERE b.dismissed_at IS NULL
                ORDER BY b.id DESC LIMIT 100
                """).Select(b => new
            {
                b.Id, b.Source, b.FileName, b.AccountId, accountName = b.AccountName, b.StartedAt, b.FinishedAt, b.Status,
                rowsRead = b.Read, rowsInserted = b.Inserted, rowsExisting = b.Existing, rowsReview = b.Review, b.Error,
            }).ToList();

            // each account that has bank balances: the latest one the bank gave, against what the ledger adds up to on that day
            var ledger = new LedgerRepository(conn, clock);
            var checks = conn.Query<(long Id, string Name, string AsOf, long Bank)>("""
                SELECT a.id, a.name, s.as_of_on, s.balance_cents
                FROM accounts a JOIN balance_snapshots s ON s.account_id = a.id AND s.source = 'bank'
                WHERE s.as_of_on = (SELECT MAX(as_of_on) FROM balance_snapshots x WHERE x.account_id = a.id AND x.source = 'bank')
                ORDER BY a.sort_order, a.id
                """).Select(c =>
            {
                var ledgerCents = ledger.BalanceAsOf(c.Id, DateOnly.ParseExact(c.AsOf, "yyyy-MM-dd"));
                return new { accountId = c.Id, accountName = c.Name, asOf = c.AsOf, bankCents = c.Bank, ledgerCents, matches = ledgerCents == c.Bank, differenceCents = ledgerCents - c.Bank };
            }).ToList();

            return Results.Ok(new { inbox = Inbox(options, clock), batches, balanceChecks = checks, maxUploadBytes = MaxUploadBytes });
        });

        api.MapGet("/imports/{id:long}", (long id, SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            var batch = new ImportBatchRepository(conn, TimeProvider.System).Get(id);
            return batch is null ? Results.Problem(title: "Not found", detail: "There is no such import.", statusCode: 404) : Results.Ok(batch);
        });

        // clear a failed import from the list (and stop it raising the alert); the record and the audit entry stay
        api.MapPost("/imports/{id:long}/dismiss", (long id, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new ImportBatchEditor(c, clock, who).Dismiss(id); return new { dismissed = true }; }));

        api.MapPost("/imports/upload",async (HttpRequest request, HttpContext http, ImportPipeline pipeline, SqliteConnectionFactory factory, TimeProvider clock, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.Problem(title: "Check the request", detail: "Send the CSV as a file in a form.", statusCode: 400);
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1) return Results.Problem(title: "Check the request", detail: "Send exactly one CSV file.", statusCode: 400);

            var file = form.Files[0];
            var name = Path.GetFileName(file.FileName);
            if (!string.Equals(Path.GetExtension(name), ".csv", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(title: "Check the request", detail: $"{name} is not a CSV file.", statusCode: 400);
            if (file.Length == 0) return Results.Problem(title: "Check the request", detail: $"{name} is empty.", statusCode: 400);
            if (file.Length > MaxUploadBytes) return Results.Problem(title: "Too large", detail: $"{name} is over 5 MB. A bank export is far smaller than that.", statusCode: 413);

            string text;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = await reader.ReadToEndAsync(ct);

            var result = await pipeline.ImportTruistAsync(name, text, ct);

            using (var conn = factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                new AuditWriter(conn, clock, tx, http.Actor()).Write("import.upload", "import_batch", result.BatchId?.ToString(), null,
                    new { file = name, status = result.Status.ToString().ToLowerInvariant(), result.RowsRead, result.RowsInserted, result.RowsExisting, result.Error });
                tx.Commit();
            }

            return Results.Ok(new
            {
                fileName = result.FileName, status = result.Status.ToString().ToLowerInvariant(), result.RowsRead, result.RowsInserted, result.RowsExisting, result.RowsReview,
                result.MovesPaired, result.MovesPending, result.Error, result.BatchId,
                reconciliation = result.Reconciliation is { } r ? new { r.AccountId, asOf = r.AsOf.ToString("yyyy-MM-dd"), r.BankCents, r.LedgerCents, matches = r.Matches } : null,
            });
        }).DisableAntiforgery();
    }

    /// <summary>The inbox folder, looked at without touching it.</summary>
    private static object Inbox(TidelineOptions options, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(options.InboxPath)) return new { configured = false, path = (string?)null, waiting = 0, settling = 0, failed = 0 };
        var path = options.InboxPath;
        var waiting = 0;
        var settling = 0;
        var failed = 0;
        try
        {
            if (Directory.Exists(path))
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    if (Path.GetFileName(f).StartsWith('.')) continue;
                    if (clock.GetUtcNow() - File.GetLastWriteTimeUtc(f) < InboxProcessor.SettleTime) settling++; else waiting++;
                }
            var failedDir = Path.Combine(path, "Failed");
            if (Directory.Exists(failedDir)) failed = Directory.EnumerateFiles(failedDir, "*.csv").Count();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new { configured = true, path, waiting, settling, failed };
    }
}
