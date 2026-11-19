using MatCloud.Apps.Drive.Data;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive.Business;

/// <summary>
/// The housekeeping of the Drive. In the database (the hourly housekeeping of MatCloud, <see cref="DriveModule.MaintainAsync"/>): the trash after
/// <see cref="Contract.Drive.DriveContract.TrashDays"/> days, tombstones after a year, uploads in pieces that expired. In the data volume
/// (<see cref="DriveWorker"/>, which has the store): blobs no version names any more (an hour after their last use) and temporary files of broken uploads.
/// </summary>
public static class DriveHousekeeping
{
    /// <summary>A blob nobody names is kept this long after its last use: an upload between its bytes and its version always finds it.</summary>
    public static readonly TimeSpan BlobGrace = TimeSpan.FromHours(1);

    public static async Task<int> MaintainAsync(AppDbContext db, CancellationToken ct)
    {
        var trash = await DriveItems.PurgeTrashAsync(db, ct);
        var yearAgo = DateTime.UtcNow.AddYears(-1);
        var tombstones = await db.DriveNodes.IgnoreQueryFilters().Where(n => n.UpdateState == SoftEntity.Deleted && n.UpdateDate < yearAgo).ExecuteDeleteAsync(ct);
        var now = DateTime.UtcNow;
        var uploads = await db.DriveUploads.Where(u => u.ExpiresDate < now).ExecuteDeleteAsync(ct);
        return trash + tombstones + uploads;
    }

    /// <summary>
    /// Removes the blobs no version names (one at a time: the row is deleted and the file removed in one transaction, so an upload that wants the same
    /// content at that moment waits for it and then puts its own copy in place) and the temporary files of uploads that are no longer running.
    /// </summary>
    public static async Task<int> SweepAsync(AppDbContext db, DriveStore store, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - BlobGrace;
        var candidates = await db.DriveBlobs.AsNoTracking()
            .Where(b => b.UpdateDate < cutoff && !db.Set<DriveNodeVersion>().Any(v => v.Sha256 == b.Sha256))
            .Select(b => new { b.Id, b.Sha256 }).Take(1000).ToListAsync(ct);
        var removed = 0;
        foreach (var blob in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deleted = await Revisions.ColumnAsync(db,
                "DELETE FROM \"DriveBlob\" b WHERE b.\"Id\" = @p0 AND b.\"UpdateDate\" < @p1 " +
                "AND NOT EXISTS (SELECT 1 FROM \"DriveNodeVersion\" v WHERE v.\"Sha256\" = b.\"Sha256\") RETURNING b.\"Id\"",
                ct, blob.Id, cutoff);
            if (deleted.Count == 1)
            {
                // the file goes before the row: an upload of the same content waits for the commit and then puts its own copy in place
                store.Delete(blob.Sha256);
                removed++;
            }

            await transaction.CommitAsync(ct);
        }

        var running = (await db.DriveUploads.AsNoTracking().Select(u => u.Token).ToListAsync(ct)).Select(t => t.ToString("N") + ".upload").ToHashSet();
        return removed + store.CleanTemp(TimeSpan.FromHours(Contract.Drive.DriveContract.UploadHours + 1), running);
    }
}

/// <summary>Runs <see cref="DriveHousekeeping.SweepAsync"/> every hour (the store lives in the data volume, the hourly housekeeping of MatCloud only has the database).</summary>
public sealed class DriveWorker(IServiceScopeFactory scopes, DriveStore store, ILogger<DriveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the app stops
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var removed = await DriveHousekeeping.SweepAsync(db, store, ct);
            if (removed > 0)
            {
                logger.LogInformation("Drive housekeeping: {Count} unused blobs and temporary files removed.", removed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Drive housekeeping failed; it runs again in an hour.");
        }
    }
}
