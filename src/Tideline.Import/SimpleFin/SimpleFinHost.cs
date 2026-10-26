using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Import.SimpleFin;

/// <summary>Syncs every 6 hours at a minute past 17 that is the same each time but not the same for everyone (docs/ARCHITECTURE.md), so the bridge is not hit on the hour.</summary>
public sealed class SimpleFinHost(SimpleFinSync sync, TimeProvider clock, Func<DateTime> localNow, ILogger<SimpleFinHost>? logger = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (EditException e) { logger?.LogWarning("SimpleFIN sync did not run: {Reason}", e.Message); }
            catch (Exception e) when (e is not OperationCanceledException) { logger?.LogError(e, "SimpleFIN sync failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The minute of the hour to go at: 17 to 23, chosen from the connection so it does not change from run to run.</summary>
    public static int MinuteFor(long connectionId) => 17 + (int)(connectionId % 7);

    public async Task<SyncReport?> TickAsync(CancellationToken ct = default)
    {
        var status = sync.Status();
        if (!status.Connected) return null;
        var now = clock.GetUtcNow();
        if (status.LastSyncAt is { } last && now - last < Every) return null;
        if (localNow().Minute < MinuteFor(1) && status.LastSyncAt is not null) return null;
        return await sync.SyncAsync(ct);
    }
}
