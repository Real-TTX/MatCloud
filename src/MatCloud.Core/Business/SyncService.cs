using MatCloud.Contract;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>
/// The general sync (<see cref="SyncContract"/>): the data of every app without a module of its own, as items an app names by collection and key.
/// A device reads and writes only the items of its own app and account. The last writer wins by the time of the change on the device (a delete too,
/// and a later write brings a deleted item back); every change takes the next number of the sequence <c>SyncRevision</c> while the stream of the app
/// and account is locked (<see cref="Revisions"/>), so a device that asks for everything above its last revision never misses one.
/// </summary>
public static class SyncService
{
    public const int DefaultLimit = 100;

    /// <summary>A change set stops before it carries more than this (characters of data), and says HasMore.</summary>
    public const long MaxChangeSetChars = 8_000_000;

    /// <summary>What is wrong with a push (the field names the item: <c>items[3].key</c>), or null.</summary>
    public static (string Message, string Field)? Validate(SyncPushRequest? request)
    {
        if (request?.Items is null)
        {
            return ("Die Anfrage enthält keine Einträge.", "items");
        }

        if (request.Items.Count > SyncContract.MaxBatchSize)
        {
            return ($"Höchstens {SyncContract.MaxBatchSize} Einträge auf einmal.", "items");
        }

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            if (item is null)
            {
                return ("Ein Eintrag fehlt.", $"items[{i}]");
            }

            if (!SyncContract.IsValidCollection(item.Collection))
            {
                return ("Die Sammlung hat 1 bis 64 Zeichen: a-z, 0-9, Punkt, Unterstrich und Bindestrich.", $"items[{i}].collection");
            }

            if (!SyncContract.IsValidKey(item.Key))
            {
                return ($"Der Schlüssel hat 1 bis {SyncContract.MaxKeyLength} Zeichen ohne Steuerzeichen.", $"items[{i}].key");
            }

            if (!item.IsDeleted && item.Data is null)
            {
                return ("Ein Eintrag ohne Inhalt muss gelöscht sein.", $"items[{i}].data");
            }

            if (item.Data?.Length > SyncContract.MaxDataLength)
            {
                return ($"Ein Eintrag hat höchstens {SyncContract.MaxDataLength / 1_000_000} Millionen Zeichen.", $"items[{i}].data");
            }

            if (item.ContentHash?.Length > 128)
            {
                return ("Der Hash hat höchstens 128 Zeichen.", $"items[{i}].contentHash");
            }
        }

        return request.Items.GroupBy(i => (i.Collection, i.Key)).Any(g => g.Count() > 1)
            ? ("Ein Eintrag kommt zweimal vor.", "items")
            : null;
    }

    /// <summary>The changes of the app above <paramref name="since"/>, oldest first (tombstones included).</summary>
    public static async Task<SyncChangeSet> ChangesAsync(AppDbContext db, Actor actor, string app, long since, int limit, CancellationToken ct = default)
    {
        Require(actor, app);
        limit = Math.Clamp(limit, 1, SyncContract.MaxBatchSize);
        var rows = await db.SyncItems.AsNoTracking()
            .Where(i => i.UserId == actor.UserId && i.App == app && i.Revision > since)
            .OrderBy(i => i.Revision)
            .Take(limit + 1)
            .ToListAsync(ct);

        var items = new List<SyncItemDto>();
        long chars = 0;
        var hasMore = rows.Count > limit;
        foreach (var row in rows.Take(limit))
        {
            if (items.Count > 0 && chars + row.Size > MaxChangeSetChars)
            {
                hasMore = true;
                break;
            }

            chars += row.Size;
            items.Add(ToDto(row));
        }

        var latest = items.Count > 0 ? items[^1].Revision : since;
        return new SyncChangeSet(items, latest, hasMore);
    }

    /// <summary>The highest revision of the app's data of the account (0 when there is none).</summary>
    public static async Task<long> LatestAsync(AppDbContext db, long userId, string app, CancellationToken ct = default)
        => await db.SyncItems.Where(i => i.UserId == userId && i.App == app).MaxAsync(i => (long?)i.Revision, ct) ?? 0;

    /// <summary>The long poll: the latest revision as soon as it is not <paramref name="since"/> any more (a change, or the data deleted), at the latest after <paramref name="timeout"/>.</summary>
    public static Task<long> WaitAsync(AppDbContext db, Actor actor, string app, long since, TimeSpan timeout, CancellationToken ct)
    {
        Require(actor, app);
        return Revisions.WaitAsync(Revisions.Stream(app, actor.UserId), since, () => LatestAsync(db, actor.UserId, app, ct), timeout, ct);
    }

    /// <summary>Applies the changes of a device (validated with <see cref="Validate"/> before).</summary>
    public static async Task<SyncPushResponse> PushAsync(AppDbContext db, Actor actor, string app, SyncPushRequest request, CancellationToken ct = default)
    {
        Require(actor, app);
        return await Revisions.ChangeAsync(db, app, actor.UserId, async () =>
        {
            var keys = request.Items.Select(i => i.Collection + "\n" + i.Key).ToList();
            var existing = await db.SyncItems
                .Where(i => i.UserId == actor.UserId && i.App == app && keys.Contains(i.Collection + "\n" + i.Key))
                .ToDictionaryAsync(i => (i.Collection, i.Key), ct);

            var results = new List<SyncPushResult>();
            foreach (var item in request.Items)
            {
                var when = item.UpdatedAt.UtcDateTime;
                if (existing.TryGetValue((item.Collection, item.Key), out var row) && row.ClientUpdateDate > when)
                {
                    // A newer change is there already: the device takes it.
                    results.Add(new SyncPushResult(item.Collection, item.Key, row.Revision, false, ToDto(row)));
                    continue;
                }

                if (row is null)
                {
                    row = new SyncItem { UserId = actor.UserId, App = app, Collection = item.Collection, Key = item.Key };
                    db.SyncItems.Add(row);
                    existing[(item.Collection, item.Key)] = row;
                }

                row.Data = item.IsDeleted ? null : item.Data;
                row.ContentHash = item.IsDeleted ? null : item.ContentHash;
                row.Size = row.Data?.Length ?? 0;
                row.ClientUpdateDate = when;
                row.Source = Clip(actor.Source, 100);
                row.UpdateState = item.IsDeleted ? SoftEntity.Deleted : row.Id == 0 ? SoftEntity.Created : SoftEntity.Updated;
                row.Revision = await Revisions.NextAsync(db, AppDbContext.SyncRevisionSequence, ct);
                results.Add(new SyncPushResult(item.Collection, item.Key, row.Revision, true));
            }

            // (the audit stamp keeps a tombstone a tombstone: it only marks live rows as updated)
            await db.SaveChangesAsync(ct);
            return new SyncPushResponse(results, await LatestAsync(db, actor.UserId, app, ct));
        }, ct);
    }

    /// <summary>What an account keeps in an app of the general sync (tombstones not counted).</summary>
    public static async Task<AppUsage> UsageAsync(AppDbContext db, long userId, string app, CancellationToken ct = default)
    {
        var items = db.SyncItems.AsNoTracking().Where(i => i.UserId == userId && i.App == app);
        var live = items.Where(i => i.UpdateState != SoftEntity.Deleted);
        var count = await live.LongCountAsync(ct);
        var bytes = await live.SumAsync(i => (long)i.Size, ct);
        var last = await items.MaxAsync(i => (DateTime?)i.UpdateDate, ct);
        return new AppUsage(count, bytes, last);
    }

    /// <summary>What all accounts keep in an app of the general sync.</summary>
    public static async Task<AppUsage> TotalUsageAsync(AppDbContext db, string app, CancellationToken ct = default)
    {
        var items = db.SyncItems.AsNoTracking().Where(i => i.App == app);
        var live = items.Where(i => i.UpdateState != SoftEntity.Deleted);
        return new AppUsage(await live.LongCountAsync(ct), await live.SumAsync(i => (long)i.Size, ct), await items.MaxAsync(i => (DateTime?)i.UpdateDate, ct));
    }

    /// <summary>The collections of an app of the account with their counts (the page of the app shows them).</summary>
    public static async Task<IReadOnlyList<(string Collection, long Items, long Bytes, DateTime? LastChange)>> CollectionsAsync(
        AppDbContext db, Actor actor, string app, CancellationToken ct = default)
    {
        Require(actor, app);
        var rows = await db.SyncItems.AsNoTracking()
            .Where(i => i.UserId == actor.UserId && i.App == app && i.UpdateState != SoftEntity.Deleted)
            .GroupBy(i => i.Collection)
            .Select(g => new { g.Key, Count = g.LongCount(), Bytes = g.Sum(i => (long)i.Size), Last = g.Max(i => (DateTime?)i.UpdateDate) })
            .OrderBy(g => g.Key)
            .ToListAsync(ct);
        return rows.Select(r => (r.Key, r.Count, r.Bytes, r.Last)).ToList();
    }

    /// <summary>Removes tombstones older than a year (every device has long seen them). The housekeeping.</summary>
    public static async Task<int> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddYears(-1);
        return await db.SyncItems.Where(i => i.UpdateState == SoftEntity.Deleted && i.UpdateDate < limit).ExecuteDeleteAsync(ct);
    }

    public static SyncItemDto ToDto(SyncItem row) => new()
    {
        Collection = row.Collection,
        Key = row.Key,
        Data = row.UpdateState == SoftEntity.Deleted ? null : row.Data,
        ContentHash = row.UpdateState == SoftEntity.Deleted ? null : row.ContentHash,
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(row.ClientUpdateDate, DateTimeKind.Utc)),
        IsDeleted = row.UpdateState == SoftEntity.Deleted,
        Source = row.Source,
        Revision = row.Revision,
    };

    /// <summary>A device of another app, and an app with a module of its own, may not use the general sync of <paramref name="app"/>.</summary>
    private static void Require(Actor actor, string app)
    {
        if (!actor.MayUse(app) || !CloudModules.UsesGeneralSync(app) || !CloudApps.IsKnown(app))
        {
            throw new NotAllowedException("Dieses Gerät darf diese Daten nicht lesen.");
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
