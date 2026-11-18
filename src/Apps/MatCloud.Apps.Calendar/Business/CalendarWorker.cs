using MatCloud.Apps.Calendar.Data;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>
/// Keeps the accounts in step that MatCloud holds: every 30 seconds (or at once when <see cref="Wake"/> is called – a hand-over, "Jetzt abgleichen") it
/// syncs the connections that are due, each on a fresh database context, one after the other. Not while ThixCalendar is switched off in this MatCloud.
/// </summary>
public sealed class CalendarWorker(IServiceScopeFactory scopes, ILogger<CalendarWorker> logger) : BackgroundService
{
    /// <summary>The most accounts of one round (the rest follow in the next).</summary>
    public const int RoundSize = 20;

    private static readonly SemaphoreSlim Signal = new(0, 1);

    /// <summary>Tests switch the loop off and call <see cref="RunOnceAsync"/> themselves.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MATCLOUD_CALENDAR_WORKER") != "off";

    public static void Wake()
    {
        if (Signal.CurrentCount == 0)
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // woken already
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Action<string> sink = message => logger.LogWarning("Calendar provider: {Message}", message);
        ThixCalendar.Core.Infrastructure.Log.Sink = sink;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Enabled)
                {
                    try
                    {
                        await RunOnceAsync(scopes, logger, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "The calendar sync failed; it tries again later.");
                    }
                }

                await Signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
        finally
        {
            // the logger goes with the app: the providers must not write into it afterwards (another host, the tests)
            if (ThixCalendar.Core.Infrastructure.Log.Sink == sink)
            {
                ThixCalendar.Core.Infrastructure.Log.Sink = null;
            }
        }
    }

    /// <summary>Syncs the connections that are due; returns how many.</summary>
    public static async Task<int> RunOnceAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct)
    {
        List<long> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await AppService.IsEnabledAsync(db, CalendarItems.App, ct))
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            due = await db.CalendarConnections.AsNoTracking().Where(c => c.NextSync <= now).OrderBy(c => c.NextSync).Take(RoundSize)
                .Select(c => c.Id).ToListAsync(ct);
        }

        foreach (var id in due)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var keys = scope.ServiceProvider.GetRequiredService<CalendarKeys>();
            var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (connection is null)
            {
                continue;
            }

            try
            {
                await CalendarCloudSync.SyncAsync(db, keys, connection, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The calendar account {Id} could not be synced.", id);
                await db.CalendarConnections.Where(c => c.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextSync, DateTime.UtcNow.AddMinutes(15)), ct);
            }
        }

        return due.Count;
    }
}
