using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tideline.Data;
using Tideline.Data.Repositories;

namespace Tideline.Import;

public sealed record InboxRunSummary(int Imported, int Failed, int Waiting);

/// <summary>
/// The CSV inbox (docs/ARCHITECTURE.md): root files only, dot files ignored, files modified in the last minute left for the
/// next run, good files to <c>Done/YYYY-MM/</c>, bad files to <c>Failed/</c> with a <c>.error.txt</c> beside them.
/// </summary>
public sealed class InboxProcessor(
    Func<string?> currentPath,
    ImportPipeline pipeline,
    SqliteConnectionFactory factory,
    TimeProvider clock,
    ILogger<InboxProcessor>? logger = null)
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>A folder that never changes (the tests use this).</summary>
    public InboxProcessor(string inboxPath, ImportPipeline pipeline, SqliteConnectionFactory factory, TimeProvider clock, ILogger<InboxProcessor>? logger = null)
        : this(() => inboxPath, pipeline, factory, clock, logger)
    {
    }

    /// <summary>The folder is asked for on every run, so changing it in the app applies at the next one. No folder means nothing to do.</summary>
    public async Task<InboxRunSummary> RunOnceAsync(CancellationToken ct = default)
    {
        var inboxPath = currentPath();
        if (string.IsNullOrWhiteSpace(inboxPath)) return new InboxRunSummary(0, 0, 0);
        if (!await _lock.WaitAsync(0, ct)) return new InboxRunSummary(0, 0, 0); // a run is already going
        try
        {
            Directory.CreateDirectory(inboxPath);
            int imported = 0, failed = 0, waiting = 0;

            foreach (var path in ImportOrder.OldestFirst(Directory.EnumerateFiles(inboxPath)))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.StartsWith('.')) continue;
                if (clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) < SettleTime) { waiting++; continue; }

                try
                {
                    var result = await ProcessAsync(inboxPath, path, name, ct);
                    if (result is null) continue; // database unreachable: leave it for the next run
                    if (result.Succeeded) imported++; else failed++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogError(e, "Could not process {File}; leaving it in the inbox", name);
                }
            }

            return new InboxRunSummary(imported, failed, waiting);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ImportResult?> ProcessAsync(string inboxPath, string path, string name, CancellationToken ct)
    {
        ImportResult result;
        if (!string.Equals(Path.GetExtension(name), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            result = new ImportResult(name, ImportStatus.Failed, 0, 0, 0, 0, 0, 0, $"{name} is not a CSV file.");
        }
        else
        {
            var text = await File.ReadAllTextAsync(path, ct);
            try
            {
                result = await pipeline.ImportTruistAsync(name, text, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException e)
            {
                logger?.LogError(e, "Database error importing {File}; it stays in the inbox", name);
                return null;
            }
        }

        var now = clock.GetUtcNow();
        if (result.Succeeded)
        {
            var dest = Path.Combine(inboxPath, "Done", now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dest);
            File.Move(path, UniquePath(dest, name, now));
            Log(EventLevel.Info, $"Imported {name}: {result.RowsInserted} new, {result.RowsExisting} already there, {result.RowsReview} to review.");
        }
        else
        {
            var dest = Path.Combine(inboxPath, "Failed");
            Directory.CreateDirectory(dest);
            var target = UniquePath(dest, name, now);
            File.Move(path, target);
            await File.WriteAllTextAsync(target + ".error.txt", result.Error + Environment.NewLine, ct);
            Log(EventLevel.Error, $"Import failed for {name}: {result.Error}");
        }
        return result;
    }

    private static string UniquePath(string dir, string name, DateTimeOffset now)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        return Path.Combine(dir, $"{stem}-{now:yyyyMMddHHmmss}{Path.GetExtension(name)}");
    }

    private void Log(EventLevel level, string message)
    {
        using var conn = factory.Open();
        new SystemEventRepository(conn, clock).Add(level, "inbox", message);
    }
}

/// <summary>
/// Runs the inbox every <paramref name="interval"/> (default five minutes). The interval is asked for each time round, so a change made in the
/// app applies without a restart; with no folder set a run does nothing.
/// </summary>
public sealed class InboxWatcher(InboxProcessor processor, Func<TimeSpan> interval, ILogger<InboxWatcher>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = await processor.RunOnceAsync(stoppingToken);
                if (s.Imported + s.Failed > 0) logger?.LogInformation("Inbox: {Imported} imported, {Failed} failed", s.Imported, s.Failed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogError(e, "Inbox run failed");
            }

            try { await Task.Delay(interval(), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
