using MatCloud.Apps.Drive.Data;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive.Business;

/// <summary>What a list asks for: an order (folders always first), a type (<see cref="DriveTypes"/> category), a page.</summary>
public sealed record DriveQuery(DriveSort Sort = DriveSort.Name, bool Descending = false, string? Category = null, int Offset = 0, int Limit = 100)
{
    public int SafeLimit => Math.Clamp(Limit, 1, DriveContract.MaxPageSize);

    public int SafeOffset => Math.Max(0, Offset);
}

/// <summary>
/// The items of the Drive: the lists (a folder, the search, recent, starred, the trash) and the changes of the tree (new folder, rename, move, copy,
/// trash, restore, delete for good, star). Every function checks the actor first (<see cref="DriveAccess"/>); every change runs under the lock of the
/// account's Drive (<see cref="Revisions.ChangeAsync{T}"/>: names, quota and paths stay consistent when two devices change something at once) and gives
/// what it changed a new revision (the live pages and, later, the sync of devices).
/// </summary>
public static class DriveItems
{
    // ---- lists ----

    /// <summary>A folder (null: the top) and one page of what is in it; <c>Missing</c> when the folder does not exist for the actor.</summary>
    public static async Task<DriveOutcome<DriveListing>> ListAsync(AppDbContext db, Actor actor, long? folderId, DriveQuery query, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var (found, folder) = await DriveAccess.FolderAsync(db, actor, folderId, track: false, ct);
        if (!found)
        {
            return DriveOutcome<DriveListing>.Missing();
        }

        var nodes = Live(db, actor).Where(n => n.ParentId == folderId);
        var (items, total) = await PageAsync(db, actor, Filter(nodes, query.Category), query, ct);
        var folderItem = folder is null ? null : (await ToItemsAsync(db, actor, [folder], ct))[0];
        var ancestors = folder is null ? [] : await AncestorsAsync(db, folder, includeSelf: true, ct);
        return DriveOutcome<DriveListing>.Success(new DriveListing(folderItem, ancestors, items, total, query.SafeOffset, query.SafeLimit));
    }

    /// <summary>Items whose name contains the text, anywhere in the Drive (not in the trash).</summary>
    public static async Task<DriveItemPage> SearchAsync(AppDbContext db, Actor actor, string? text, DriveQuery query, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var nodes = Live(db, actor);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = "%" + UserService.EscapeLike(DriveNames.KeyOf(text.Trim())) + "%";
            nodes = nodes.Where(n => EF.Functions.Like(n.NameKey, pattern, "\\"));
        }

        var (items, total) = await PageAsync(db, actor, Filter(nodes, query.Category), query, ct);
        return new DriveItemPage(items, total, query.SafeOffset, query.SafeLimit);
    }

    /// <summary>The files the account opened or saved last, newest first (in any app, on any device, in the browser).</summary>
    public static async Task<DriveItemPage> RecentAsync(AppDbContext db, Actor actor, int limit, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var take = Math.Clamp(limit, 1, DriveContract.MaxPageSize);
        var live = Live(db, actor);
        var visits = db.DriveVisits.AsNoTracking().Where(v => v.UserId == actor.UserId && live.Any(n => n.Id == v.NodeId));
        var total = await visits.CountAsync(ct);
        var ids = await visits.OrderByDescending(v => v.VisitDate).Take(take).Select(v => v.NodeId).ToListAsync(ct);
        var nodes = await live.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        var ordered = ids.Select(id => nodes.First(n => n.Id == id)).ToList();
        return new DriveItemPage(await ToItemsAsync(db, actor, ordered, ct), total, 0, take);
    }

    public static async Task<DriveItemPage> StarredAsync(AppDbContext db, Actor actor, DriveQuery query, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var stars = db.DriveStars.Where(s => s.UserId == actor.UserId);
        var nodes = Live(db, actor).Where(n => stars.Any(s => s.NodeId == n.Id));
        var (items, total) = await PageAsync(db, actor, Filter(nodes, query.Category), query, ct);
        return new DriveItemPage(items, total, query.SafeOffset, query.SafeLimit);
    }

    /// <summary>What was put into the trash (the items themselves, not what lies below them), latest first unless another order is asked for.</summary>
    public static async Task<DriveItemPage> TrashAsync(AppDbContext db, Actor actor, DriveQuery query, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var nodes = Filter(DriveAccess.NodesOf(db, actor.UserId).AsNoTracking().Where(n => n.TrashedDate != null), query.Category);
        var total = await nodes.CountAsync(ct);
        var ordered = query.Sort == DriveSort.Name
            ? (query.Descending ? nodes.OrderByDescending(n => n.NameKey) : nodes.OrderBy(n => n.NameKey))
            : (query.Descending ? nodes.OrderBy(n => n.TrashedDate) : nodes.OrderByDescending(n => n.TrashedDate));
        var page = await ordered.ThenBy(n => n.Id).Skip(query.SafeOffset).Take(query.SafeLimit).ToListAsync(ct);
        return new DriveItemPage(await ToItemsAsync(db, actor, page, ct), total, query.SafeOffset, query.SafeLimit);
    }

    /// <summary>One item with the folders above it; <c>Missing</c> for an item of another account. Items in the trash are found too.</summary>
    public static async Task<DriveOutcome<DriveItem>> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var node = await DriveAccess.FindAsync(db, actor, id, trashed: true, ct: ct);
        if (node is null)
        {
            return DriveOutcome<DriveItem>.Missing();
        }

        var item = (await ToItemsAsync(db, actor, [node], ct))[0];
        return DriveOutcome<DriveItem>.Success(item with { Ancestors = await AncestorsAsync(db, node, includeSelf: false, ct) });
    }

    /// <summary>The folders a move can go to: every folder of the Drive outside the trash, with its path, by path (the move dialog of the pages).</summary>
    public static async Task<IReadOnlyList<(long Id, string Path)>> FoldersAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var folders = await Live(db, actor).AsNoTracking().Where(n => n.Kind == DriveItemKind.Folder).ToListAsync(ct);
        var items = await ToItemsAsync(db, actor, folders, ct);
        return items.Select(i => (i.Id, (i.Location == "/" ? "/" : i.Location + "/") + i.Name)).OrderBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The latest revision of the account's Drive (the live pages wait for a higher one).</summary>
    public static async Task<long> LatestAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.DriveNodes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Drive!.Kind == DriveKind.Personal && n.Drive.OwnerUserId == userId)
            .MaxAsync(n => (long?)n.Revision, ct) ?? 0;

    /// <summary>Waits (long poll) until the Drive of the account changes, at the latest after <paramref name="timeout"/>.</summary>
    public static async Task<long> WaitAsync(AppDbContext db, Actor actor, long since, TimeSpan timeout, CancellationToken ct)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await Revisions.WaitAsync(Revisions.Stream(CloudApps.Drive, actor.UserId), since, () => LatestAsync(db, actor.UserId, ct), timeout, ct);
    }

    // ---- changes of the tree ----

    public static async Task<DriveOutcome<DriveItem>> CreateFolderAsync(AppDbContext db, Actor actor, long? parentId, string? name, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var (clean, problem) = DriveNames.Check(name);
        if (problem is not null)
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.Invalid, problem, "name");
        }

        return await ChangeAsync(db, actor, async () =>
        {
            var (found, parent) = await DriveAccess.FolderAsync(db, actor, parentId, track: false, ct);
            if (!found)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.NotFound, "Diesen Ordner gibt es nicht (mehr).", "parentId");
            }

            var drive = await EnsureDriveAsync(db, actor.UserId, ct);
            if (await NamedAsync(db, drive.Id, parentId, clean!, null, ct) is { } taken)
            {
                return await NameTakenAsync<DriveItem>(db, actor, taken, ct);
            }

            var folder = await AddNodeAsync(db, actor, drive.Id, parent, DriveItemKind.Folder, clean!, ct);
            await TouchFolderAsync(db, parentId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, folder.Id, ct));
        }, ct);
    }

    public static async Task<DriveOutcome<DriveItem>> RenameAsync(AppDbContext db, Actor actor, long id, string? name, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var (clean, problem) = DriveNames.Check(name);
        if (problem is not null)
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.Invalid, problem, "name");
        }

        return await ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, ct: ct);
            if (node is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            if (node.Name != clean)
            {
                if (await NamedAsync(db, node.DriveId, node.ParentId, clean!, node.Id, ct) is { } taken)
                {
                    return await NameTakenAsync<DriveItem>(db, actor, taken, ct);
                }

                SetName(node, clean!);
                node.Revision = await NextRevisionAsync(db, ct);
                await db.SaveChangesAsync(ct);
            }

            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    /// <summary>Moves an item into a folder (null: the top). The name must be free there; a folder never goes into itself.</summary>
    public static async Task<DriveOutcome<DriveItem>> MoveAsync(AppDbContext db, Actor actor, long id, long? targetId, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, ct: ct);
            var (found, target) = await DriveAccess.FolderAsync(db, actor, targetId, track: false, ct);
            if (node is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            if (!found)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.NotFound, "Diesen Ordner gibt es nicht (mehr).", "parentId");
            }

            if (target is not null && target.Path.StartsWith(node.Path, StringComparison.Ordinal))
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.Conflict, $"„{node.Name}“ kann nicht in sich selbst verschoben werden.", "parentId");
            }

            if (node.ParentId == targetId)
            {
                return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, node.Id, ct));
            }

            if (await NamedAsync(db, node.DriveId, targetId, node.Name, node.Id, ct) is { } taken)
            {
                return await NameTakenAsync<DriveItem>(db, actor, taken, ct);
            }

            var oldParent = node.ParentId;
            await RepathAsync(db, node, target, ct);
            await TouchFolderAsync(db, oldParent, actor, ct);
            await TouchFolderAsync(db, targetId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    /// <summary>
    /// Copies an item (a folder with everything in it, a file with its current version) into a folder (null: the top). Without a name a copy in the same
    /// folder is called "Kopie von …"; a taken name gets a number. The bytes are not copied: the copy names the same blob.
    /// </summary>
    public static async Task<DriveOutcome<DriveItem>> CopyAsync(AppDbContext db, Actor actor, long id, long? targetId, string? name, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        string? chosen = null;
        if (name is not null)
        {
            var (clean, problem) = DriveNames.Check(name);
            if (problem is not null)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.Invalid, problem, "name");
            }

            chosen = clean;
        }

        return await ChangeAsync(db, actor, async () =>
        {
            var source = await DriveAccess.FindAsync(db, actor, id, ct: ct);
            var (found, target) = await DriveAccess.FolderAsync(db, actor, targetId, track: false, ct);
            if (source is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            if (!found)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.NotFound, "Diesen Ordner gibt es nicht (mehr).", "parentId");
            }

            if (target is not null && target.Path.StartsWith(source.Path, StringComparison.Ordinal))
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.Conflict, $"„{source.Name}“ kann nicht in sich selbst kopiert werden.", "parentId");
            }

            var subtree = await db.DriveNodes.AsNoTracking()
                .Where(n => n.DriveId == source.DriveId && n.Path.StartsWith(source.Path) && !n.InTrash).ToListAsync(ct);
            var bytes = subtree.Where(n => n.Kind == DriveItemKind.File).Sum(n => n.Size);
            if (await QuotaProblemAsync(db, actor.UserId, bytes, ct) is { } full)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.QuotaFull, full, "quota");
            }

            var wanted = chosen ?? (targetId == source.ParentId ? DriveNames.CopyOf(source.Name) : source.Name);
            var copyName = await FreeNameAsync(db, source.DriveId, targetId, wanted, source.Kind, null, ct);
            var copies = new Dictionary<long, DriveNode>();
            foreach (var original in subtree.OrderBy(n => n.Path.Length))
            {
                var parent = original.Id == source.Id ? target : copies[original.ParentId!.Value];
                var copy = await AddNodeAsync(db, actor, source.DriveId, parent, original.Kind, original.Id == source.Id ? copyName : original.Name, ct);
                copies[original.Id] = copy;
                if (original.Kind == DriveItemKind.File && original.Sha256 is not null)
                {
                    await DriveFiles.AddVersionAsync(db, actor, copy, original.Sha256, original.Size, ct);
                }
            }

            await TouchFolderAsync(db, targetId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, copies[source.Id].Id, ct));
        }, ct);
    }

    /// <summary>Puts an item into the trash, with everything below it. It goes for good after <see cref="DriveContract.TrashDays"/> days.</summary>
    public static async Task<DriveOutcome<DriveItem>> TrashAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, ct: ct);
            if (node is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            var revision = await NextRevisionAsync(db, ct);
            node.TrashedDate = DateTime.UtcNow;
            node.InTrash = true;
            node.Revision = revision;
            await db.SaveChangesAsync(ct);
            var prefix = node.Path;
            await db.DriveNodes.Where(n => n.DriveId == node.DriveId && n.Path.StartsWith(prefix) && n.Id != node.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.InTrash, true).SetProperty(n => n.Revision, revision), ct);
            await TouchFolderAsync(db, node.ParentId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    /// <summary>
    /// Takes an item out of the trash: back into its folder, or to the top when that folder is gone or in the trash itself; a name that is taken
    /// meanwhile gets a number. What was put into the trash on its own below it stays there.
    /// </summary>
    public static async Task<DriveOutcome<DriveItem>> RestoreAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, trashed: true, ct: ct);
            if (node?.TrashedDate is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            var parent = node.ParentId is { } pid ? await db.DriveNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == pid, ct) : null;
            if (node.ParentId is not null && (parent is null || parent.InTrash))
            {
                await RepathAsync(db, node, null, ct);
            }

            var revision = await NextRevisionAsync(db, ct);
            SetName(node, await FreeNameAsync(db, node.DriveId, node.ParentId, node.Name, node.Kind, node.Id, ct));
            node.TrashedDate = null;
            node.InTrash = false;
            node.Revision = revision;
            await db.SaveChangesAsync(ct);

            var below = await db.DriveNodes.AsNoTracking().Where(n => n.DriveId == node.DriveId && n.Path.StartsWith(node.Path) && n.Id != node.Id)
                .Select(n => new { n.Id, n.Path, n.TrashedDate }).ToListAsync(ct);
            var stillTrashed = below.Where(n => n.TrashedDate != null).Select(n => n.Path).ToList();
            var back = below.Where(n => !stillTrashed.Any(p => n.Path.StartsWith(p, StringComparison.Ordinal))).Select(n => n.Id).ToList();
            if (back.Count > 0)
            {
                await db.DriveNodes.Where(n => back.Contains(n.Id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.InTrash, false).SetProperty(n => n.Revision, revision), ct);
            }

            await TouchFolderAsync(db, node.ParentId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    /// <summary>Deletes an item of the trash for good, with every version and everything below it (the bytes go with the next housekeeping).</summary>
    public static async Task<DriveOutcome<bool>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, trashed: true, ct: ct);
            if (node is null)
            {
                return DriveOutcome<bool>.Missing();
            }

            if (!node.InTrash)
            {
                return DriveOutcome<bool>.Fail(DriveFailure.Conflict, "Nur was im Papierkorb liegt, kann endgültig gelöscht werden.");
            }

            await RemoveAsync(db, node.DriveId, node.Path, ct);
            return DriveOutcome<bool>.Success(true);
        }, ct);
    }

    /// <summary>Empties the trash of the account; answers how many items went.</summary>
    public static async Task<int> EmptyTrashAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await ChangeAsync(db, actor, async () =>
        {
            var trashed = await DriveAccess.NodesOf(db, actor.UserId).AsNoTracking().Where(n => n.TrashedDate != null)
                .Select(n => new { n.DriveId, n.Path }).ToListAsync(ct);
            foreach (var node in trashed.OrderBy(n => n.Path.Length))
            {
                await RemoveAsync(db, node.DriveId, node.Path, ct);
            }

            return trashed.Count;
        }, ct);
    }

    /// <summary>The housekeeping: what lies in the trash longer than <see cref="DriveContract.TrashDays"/> days goes for good, per account under its lock.</summary>
    internal static async Task<int> PurgeTrashAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-DriveContract.TrashDays);
        var old = await db.DriveNodes.AsNoTracking().Where(n => n.TrashedDate != null && n.TrashedDate < cutoff)
            .Select(n => new { n.Id, n.DriveId, n.Path, Owner = n.Drive!.OwnerUserId }).ToListAsync(ct);
        var count = 0;
        foreach (var group in old.Where(n => n.Owner is not null).GroupBy(n => n.Owner!.Value))
        {
            var system = new Actor(group.Key, false);
            count += await ChangeAsync(db, system, async () =>
            {
                var removed = 0;
                foreach (var node in group.OrderBy(n => n.Path.Length))
                {
                    // a folder that went before took this one with it
                    if (await db.DriveNodes.AnyAsync(n => n.Id == node.Id, ct))
                    {
                        await RemoveAsync(db, node.DriveId, node.Path, ct);
                        removed++;
                    }
                }

                return removed;
            }, ct);
        }

        return count;
    }

    /// <summary>Stars an item or takes the star away (for the account; it changes nothing of the item).</summary>
    public static async Task<DriveOutcome<bool>> StarAsync(AppDbContext db, Actor actor, long id, bool starred, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        if (await DriveAccess.FindAsync(db, actor, id, ct: ct) is null)
        {
            return DriveOutcome<bool>.Missing();
        }

        if (!starred)
        {
            await db.DriveStars.Where(s => s.UserId == actor.UserId && s.NodeId == id).ExecuteDeleteAsync(ct);
            return DriveOutcome<bool>.Success(false);
        }

        await Revisions.ExecuteAsync(db,
            "INSERT INTO \"DriveStar\" (\"UserId\", \"NodeId\", \"CreateDate\", \"CreateUserId\", \"UpdateDate\", \"UpdateUserId\") VALUES (@p0, @p1, now(), @p0, now(), @p0) ON CONFLICT (\"UserId\", \"NodeId\") DO NOTHING",
            ct, actor.UserId, id);
        return DriveOutcome<bool>.Success(true);
    }

    // ---- what the other parts of the module share ----

    /// <summary>Runs a change under the lock of the account's Drive, in one transaction; the live pages are woken afterwards.</summary>
    internal static Task<T> ChangeAsync<T>(AppDbContext db, Actor actor, Func<Task<T>> change, CancellationToken ct)
        => Revisions.ChangeAsync(db, CloudApps.Drive, actor.UserId, change, ct);

    internal static Task<long> NextRevisionAsync(AppDbContext db, CancellationToken ct) => Revisions.NextAsync(db, DriveModel.RevisionSequence, ct);

    /// <summary>The live nodes of the actor's drive (not in the trash).</summary>
    internal static IQueryable<DriveNode> Live(AppDbContext db, Actor actor) => DriveAccess.NodesOf(db, actor.UserId).Where(n => !n.InTrash);

    /// <summary>The personal drive of the account; made now when it has none (only inside <see cref="ChangeAsync{T}"/>).</summary>
    internal static async Task<DriveSpace> EnsureDriveAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var drive = await DriveAccess.DriveOfAsync(db, userId, ct);
        if (drive is not null)
        {
            return drive;
        }

        drive = new DriveSpace { Kind = DriveKind.Personal, OwnerUserId = userId };
        db.DriveSpaces.Add(drive);
        await db.SaveChangesAsync(ct);
        return drive;
    }

    /// <summary>A new node in a folder (null: the top), with its path (two writes: the id is known after the first).</summary>
    internal static async Task<DriveNode> AddNodeAsync(AppDbContext db, Actor actor, long driveId, DriveNode? parent, DriveItemKind kind, string name, CancellationToken ct)
    {
        var node = new DriveNode
        {
            DriveId = driveId,
            ParentId = parent?.Id,
            Kind = kind,
            ModifiedDate = DateTime.UtcNow,
            ModifiedBy = actor.Source,
            Revision = await NextRevisionAsync(db, ct),
        };
        SetName(node, name);
        db.DriveNodes.Add(node);
        await db.SaveChangesAsync(ct);
        node.Path = (parent?.Path ?? "/") + node.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        await db.SaveChangesAsync(ct);
        return node;
    }

    internal static void SetName(DriveNode node, string name)
    {
        node.Name = name;
        node.NameKey = DriveNames.KeyOf(name);
        node.Category = DriveTypes.CategoryOf(node.Kind, name);
        node.ContentType = node.Kind == DriveItemKind.File ? DriveTypes.ContentTypeOf(name) : null;
    }

    /// <summary>The live item of a name in a folder (whatever the case), but <paramref name="except"/>; null when the name is free.</summary>
    internal static Task<DriveNode?> NamedAsync(AppDbContext db, long driveId, long? parentId, string name, long? except, CancellationToken ct)
    {
        var key = DriveNames.KeyOf(name);
        return db.DriveNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.DriveId == driveId && n.ParentId == parentId && !n.InTrash && n.NameKey == key && n.Id != except, ct);
    }

    /// <summary>The name itself when it is free in the folder, else "name (2).ext", "name (3).ext" …</summary>
    internal static async Task<string> FreeNameAsync(AppDbContext db, long driveId, long? parentId, string name, DriveItemKind kind, long? except, CancellationToken ct)
    {
        var taken = (await db.DriveNodes.AsNoTracking()
                .Where(n => n.DriveId == driveId && n.ParentId == parentId && !n.InTrash && n.Id != except)
                .Select(n => n.NameKey).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(DriveNames.KeyOf(name)))
        {
            return name;
        }

        for (var number = 2; ; number++)
        {
            var candidate = DriveNames.Numbered(name, kind, number);
            if (!taken.Contains(DriveNames.KeyOf(candidate)))
            {
                return candidate;
            }
        }
    }

    internal static async Task<DriveOutcome<T>> NameTakenAsync<T>(AppDbContext db, Actor actor, DriveNode taken, CancellationToken ct)
    {
        var current = await ItemAsync(db, actor, taken.Id, ct);
        var what = taken.Kind == DriveItemKind.Folder ? "einen Ordner" : "eine Datei";
        return DriveOutcome<T>.Fail(DriveFailure.NameTaken, $"Hier gibt es schon {what} namens „{taken.Name}“.", "name", current);
    }

    /// <summary>What is wrong when <paramref name="bytes"/> more do not fit into the account's space, or null.</summary>
    internal static async Task<string?> QuotaProblemAsync(AppDbContext db, long userId, long bytes, CancellationToken ct)
    {
        var (used, quota) = await UsedAndQuotaAsync(db, userId, ct);
        return quota is { } limit && used + bytes > limit
            ? $"Der Speicherplatz ist voll ({Core.Web.Ui.Bytes(used)} von {Core.Web.Ui.Bytes(limit)} belegt). Bitte Papierkorb leeren oder alte Versionen löschen."
            : null;
    }

    /// <summary>The bytes of every version of every file of the account (the trash included) and its quota (null: no limit).</summary>
    internal static async Task<(long Used, long? Quota)> UsedAndQuotaAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var drive = await DriveAccess.DriveOfAsync(db, userId, ct);
        if (drive is null)
        {
            return (0, DriveLimits.Current.DefaultQuotaBytes);
        }

        var used = await db.DriveVersions.Where(v => v.Node!.DriveId == drive.Id).SumAsync(v => (long?)v.Size, ct) ?? 0;
        return (used, drive.QuotaBytes ?? DriveLimits.Current.DefaultQuotaBytes);
    }

    /// <summary>A folder changed (something went in or out): its date moves on.</summary>
    internal static async Task TouchFolderAsync(AppDbContext db, long? folderId, Actor actor, CancellationToken ct)
    {
        if (folderId is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var revision = await NextRevisionAsync(db, ct);
        await db.DriveNodes.Where(n => n.Id == folderId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ModifiedDate, now).SetProperty(n => n.ModifiedBy, actor.Source).SetProperty(n => n.Revision, revision), ct);
    }

    /// <summary>Puts a node into another folder (null: the top): its path and the paths of everything below it.</summary>
    private static async Task RepathAsync(AppDbContext db, DriveNode node, DriveNode? target, CancellationToken ct)
    {
        var oldPath = node.Path;
        var newPath = (target?.Path ?? "/") + node.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        node.ParentId = target?.Id;
        node.Path = newPath;
        node.Revision = await NextRevisionAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await db.DriveNodes.IgnoreQueryFilters().Where(n => n.DriveId == node.DriveId && n.Id != node.Id && n.Path.StartsWith(oldPath))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Path, n => newPath + n.Path.Substring(oldPath.Length)), ct);
    }

    /// <summary>A node and everything below it go for good: the versions, stars and visits are removed, the nodes stay as tombstones.</summary>
    private static async Task RemoveAsync(AppDbContext db, long driveId, string path, CancellationToken ct)
    {
        var subtree = db.DriveNodes.Where(n => n.DriveId == driveId && n.Path.StartsWith(path));
        var ids = subtree.Select(n => n.Id);
        await db.DriveVersions.Where(v => ids.Contains(v.NodeId)).ExecuteDeleteAsync(ct);
        await db.DriveStars.Where(s => ids.Contains(s.NodeId)).ExecuteDeleteAsync(ct);
        await db.DriveVisits.Where(v => ids.Contains(v.NodeId)).ExecuteDeleteAsync(ct);
        var revision = await NextRevisionAsync(db, ct);
        var now = DateTime.UtcNow;
        await subtree.ExecuteUpdateAsync(s => s
            .SetProperty(n => n.UpdateState, SoftEntity.Deleted)
            .SetProperty(n => n.UpdateDate, now)
            .SetProperty(n => n.CurrentVersionId, (long?)null)
            .SetProperty(n => n.Sha256, (string?)null)
            .SetProperty(n => n.Size, 0)
            .SetProperty(n => n.Revision, revision), ct);
    }

    // ---- the items as the contract shows them ----

    internal static async Task<DriveItem> ItemAsync(AppDbContext db, Actor actor, long id, CancellationToken ct)
    {
        var node = await db.DriveNodes.AsNoTracking().FirstAsync(n => n.Id == id, ct);
        return (await ToItemsAsync(db, actor, [node], ct))[0];
    }

    /// <summary>Nodes as items: with the path of their folder (one query for every folder above them) and their star.</summary>
    internal static async Task<IReadOnlyList<DriveItem>> ToItemsAsync(AppDbContext db, Actor actor, IReadOnlyList<DriveNode> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        var above = nodes.SelectMany(n => DriveAccess.IdsOf(n.Path).Where(id => id != n.Id)).Distinct().ToList();
        var names = above.Count == 0
            ? new Dictionary<long, string>()
            : await db.DriveNodes.AsNoTracking().Where(n => above.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, ct);
        var ids = nodes.Select(n => n.Id).ToList();
        var stars = (await db.DriveStars.AsNoTracking().Where(s => s.UserId == actor.UserId && ids.Contains(s.NodeId)).Select(s => s.NodeId).ToListAsync(ct)).ToHashSet();
        return nodes.Select(n => ToItem(n, LocationOf(n, names), stars.Contains(n.Id))).ToList();
    }

    internal static string ETagOf(long versionId) => "\"" + versionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";

    internal static DateTimeOffset Utc(DateTime date) => new(DateTime.SpecifyKind(date, DateTimeKind.Utc));

    private static DriveItem ToItem(DriveNode n, string location, bool starred) => new()
    {
        Id = n.Id,
        ParentId = n.ParentId,
        Kind = n.Kind,
        Name = n.Name,
        Size = n.Size,
        ContentType = n.ContentType,
        ETag = n.CurrentVersionId is { } version ? ETagOf(version) : null,
        Sha256 = n.Sha256,
        Version = n.VersionNumber,
        CreatedAt = Utc(n.CreateDate),
        ModifiedAt = Utc(n.ModifiedDate),
        ModifiedBy = n.ModifiedBy,
        Location = location,
        IsStarred = starred,
        IsTrashed = n.InTrash,
        TrashedAt = n.TrashedDate is { } trashed ? Utc(trashed) : null,
    };

    private static string LocationOf(DriveNode node, Dictionary<long, string> names)
    {
        var parts = DriveAccess.IdsOf(node.Path).Where(id => id != node.Id).Select(id => names.GetValueOrDefault(id, "…"));
        return "/" + string.Join("/", parts);
    }

    private static async Task<IReadOnlyList<DriveCrumb>> AncestorsAsync(AppDbContext db, DriveNode node, bool includeSelf, CancellationToken ct)
    {
        var ids = DriveAccess.IdsOf(node.Path).Where(id => includeSelf || id != node.Id).ToList();
        var names = await db.DriveNodes.AsNoTracking().Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, ct);
        return ids.Where(names.ContainsKey).Select(id => new DriveCrumb(id, names[id])).ToList();
    }

    private static IQueryable<DriveNode> Filter(IQueryable<DriveNode> nodes, string? category)
        => string.IsNullOrEmpty(category) ? nodes : nodes.Where(n => n.Category == category);

    /// <summary>One page of nodes as items: folders first, then in the asked order (the name breaks ties).</summary>
    private static async Task<(IReadOnlyList<DriveItem> Items, int Total)> PageAsync(AppDbContext db, Actor actor, IQueryable<DriveNode> nodes, DriveQuery query, CancellationToken ct)
    {
        nodes = nodes.AsNoTracking();
        var total = await nodes.CountAsync(ct);
        var folders = nodes.OrderBy(n => n.Kind);
        var ordered = (query.Sort, query.Descending) switch
        {
            (DriveSort.Modified, false) => folders.ThenBy(n => n.ModifiedDate),
            (DriveSort.Modified, true) => folders.ThenByDescending(n => n.ModifiedDate),
            (DriveSort.Size, false) => folders.ThenBy(n => n.Size),
            (DriveSort.Size, true) => folders.ThenByDescending(n => n.Size),
            (DriveSort.Type, false) => folders.ThenBy(n => n.Category).ThenBy(n => n.ContentType),
            (DriveSort.Type, true) => folders.ThenByDescending(n => n.Category).ThenByDescending(n => n.ContentType),
            (_, true) => folders.ThenByDescending(n => n.NameKey),
            _ => folders.ThenBy(n => n.NameKey),
        };
        var page = await ordered.ThenBy(n => n.NameKey).ThenBy(n => n.Id).Skip(query.SafeOffset).Take(query.SafeLimit).ToListAsync(ct);
        return (await ToItemsAsync(db, actor, page, ct), total);
    }
}
