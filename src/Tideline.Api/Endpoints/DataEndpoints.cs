using Microsoft.Data.Sqlite;
using Tideline.Api.Auth;
using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>Owner only: the backups on disk, making one now, and taking everything out as CSV (docs/11-deploy.md, docs/07-api.md).</summary>
public static class DataEndpoints
{
    public static void MapDataEndpoints(this RouteGroupBuilder api)
    {
        // Where backups go is server configuration, never part of a request. It is read here from the services rather than taken as a handler parameter.
        api.MapGet("/backups", (HttpContext http, TimeProvider clock) =>
        {
            var (outDir, _) = Folders(http);
            var files = BackupService.List(outDir);
            var last = files.FirstOrDefault(f => f.Kind == "database");
            return Results.Ok(new
            {
                folder = outDir,
                keepDays = BackupService.KeepDays,
                files = files.Select(f => new { f.Name, f.Bytes, f.Modified, f.Kind }),
                lastBackupAt = last?.Modified,
                // a nightly job is expected, so more than two days without a copy is worth saying out loud
                overdue = last is null || clock.GetUtcNow() - last.Modified > TimeSpan.FromDays(2),
            });
        });

        api.MapPost("/backups", (HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            var (outDir, keysDir) = Folders(http);
            var connection = Connection(http);
            var made = BackupService.Create(connection, outDir, keysDir, clock.GetUtcNow());
            var check = BackupService.Verify(made.DatabaseFile, connection, clock.GetUtcNow().Year);

            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();
            new AuditWriter(conn, clock, tx, http.Actor()).Write("backup.created", "backup", Path.GetFileName(made.DatabaseFile), null,
                new { file = Path.GetFileName(made.DatabaseFile), made.Bytes, keys = made.KeysFile is not null, checkedOk = check.Ok });
            tx.Commit();

            return Results.Ok(new { file = Path.GetFileName(made.DatabaseFile), made.Bytes, keysIncluded = made.KeysFile is not null, removed = made.Removed, check.Ok, check.Message });
        });

        api.MapPost("/export", (HttpContext http, SqliteConnectionFactory factory, TimeProvider clock) =>
        {
            using var conn = factory.Open();
            var zip = new MemoryStream();
            ExportService.Write(conn, zip);
            zip.Position = 0;

            using (var tx = conn.BeginTransaction())
            {
                new AuditWriter(conn, clock, tx, http.Actor()).Write("export.created", "export", null, null, new { tables = ExportService.Tables.Count, bytes = zip.Length });
                tx.Commit();
            }
            return Results.File(zip, "application/zip", $"tideline-export-{clock.GetUtcNow():yyyyMMdd}.zip");
        });
    }

    private static string Connection(HttpContext http) =>
        http.RequestServices.GetRequiredService<IConfiguration>()["Database:ConnectionString"] ?? "Data Source=tideline.db";

    private static (string Backups, string Keys) Folders(HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<TidelineOptions>();
        var db = new SqliteConnectionStringBuilder(Connection(http)).DataSource;
        var beside = Path.GetDirectoryName(Path.GetFullPath(db))!;
        return (options.BackupPath ?? Path.Combine(beside, "backups"), options.DataProtectionKeys ?? Path.Combine(beside, "keys"));
    }
}
