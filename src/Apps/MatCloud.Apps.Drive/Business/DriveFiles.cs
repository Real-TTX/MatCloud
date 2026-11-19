using MatCloud.Apps.Drive.Data;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive.Business;

/// <summary>A file (or one version of it) to stream: where its bytes lie, and what the answer says about them.</summary>
public sealed record DriveContent(string PhysicalPath, string Name, string ContentType, long Size, string ETag, DateTimeOffset LastModified);

/// <summary>
/// The content of the files: a new file, a new version (only with the ETag it was opened with: If-Match), the download (from the store, streamed,
/// ranges by the caller), the versions, uploads in pieces, the space of the account. The bytes arrive in the temporary folder first (hashed while they
/// are written, never in memory); only when everything is checked do they become a version, under the lock of the account's Drive.
/// </summary>
public static class DriveFiles
{
    // ---- new files and new versions ----

    /// <summary>
    /// A new file in a folder (null: the top) from a stream. <paramref name="declaredSize"/> (Content-Length) lets a too large file be refused before its
    /// first byte. <see cref="DriveNameConflict.Fail"/>: 409 with the item of that name; <see cref="DriveNameConflict.Rename"/>: a free name.
    /// </summary>
    public static async Task<DriveOutcome<DriveItem>> CreateAsync(AppDbContext db, DriveStore store, Actor actor, long? parentId, string? name,
        DriveNameConflict onConflict, Stream body, long? declaredSize, long maxBytes, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var (clean, problem) = DriveNames.Check(name);
        if (problem is not null)
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.Invalid, problem, "name");
        }

        var early = await CheckTargetAsync(db, actor, parentId, clean!, onConflict, declaredSize ?? 0, maxBytes, ct);
        if (early is not null)
        {
            return early;
        }

        var arrived = await ReceiveAsync(store, body, maxBytes, ct);
        if (arrived is null)
        {
            return TooLarge<DriveItem>(maxBytes);
        }

        return await CommitNewAsync(db, store, actor, parentId, clean!, onConflict, arrived, ct);
    }

    /// <summary>
    /// A new version of a file. <paramref name="ifMatch"/> is the ETag the file was opened with (or "*"): without it 428, when another version became the
    /// current one meanwhile 412 with the file as it is now. The check runs before the bytes are read and again under the lock.
    /// </summary>
    public static async Task<DriveOutcome<DriveItem>> SaveVersionAsync(AppDbContext db, DriveStore store, Actor actor, long id, string? ifMatch,
        Stream body, long? declaredSize, long maxBytes, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var early = await CheckVersionAsync(db, actor, id, ifMatch, declaredSize ?? 0, maxBytes, ct);
        if (early is not null)
        {
            return early;
        }

        var arrived = await ReceiveAsync(store, body, maxBytes, ct);
        if (arrived is null)
        {
            return TooLarge<DriveItem>(maxBytes);
        }

        return await CommitVersionAsync(db, store, actor, id, ifMatch, arrived, ct);
    }

    // ---- downloads ----

    /// <summary>
    /// A file to stream: its current version, or <paramref name="versionId"/>. <paramref name="recordVisit"/>: it counts as opened (the list
    /// "Zuletzt verwendet"). <c>Missing</c> for a folder, an item of another account, a version of another file.
    /// </summary>
    public static async Task<DriveOutcome<DriveContent>> OpenAsync(AppDbContext db, DriveStore store, Actor actor, long id, long? versionId = null,
        bool recordVisit = true, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var node = await DriveAccess.FindAsync(db, actor, id, trashed: versionId is not null, ct: ct);
        if (node is not { Kind: DriveItemKind.File } || (versionId ?? node.CurrentVersionId) is not { } wanted)
        {
            return DriveOutcome<DriveContent>.Missing();
        }

        var version = await db.DriveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == wanted && v.NodeId == node.Id, ct);
        if (version is null || !store.Exists(version.Sha256))
        {
            return DriveOutcome<DriveContent>.Missing();
        }

        if (recordVisit && versionId is null)
        {
            await VisitAsync(db, actor, node.Id, ct);
        }

        var stamp = versionId is null ? node.ModifiedDate : version.CreateDate;
        return DriveOutcome<DriveContent>.Success(new DriveContent(store.PathOf(version.Sha256), node.Name, DriveTypes.ContentTypeOf(node.Name), version.Size,
            DriveItems.ETagOf(version.Id), DriveItems.Utc(stamp)));
    }

    // ---- versions ----

    public static async Task<DriveOutcome<DriveVersionList>> VersionsAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var node = await DriveAccess.FindAsync(db, actor, id, trashed: true, ct: ct);
        if (node is not { Kind: DriveItemKind.File })
        {
            return DriveOutcome<DriveVersionList>.Missing();
        }

        var versions = await db.DriveVersions.AsNoTracking().Where(v => v.NodeId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
        var list = versions.Select(v => new Contract.Drive.DriveVersion(v.Id, v.Number, v.Size, DriveItems.ETagOf(v.Id), v.Sha256, DriveItems.Utc(v.CreateDate), v.Source, v.App,
            v.Id == node.CurrentVersionId)).ToList();
        return DriveOutcome<DriveVersionList>.Success(new DriveVersionList(id, list));
    }

    /// <summary>An older version becomes the current one again – as a new version, so the history stays whole (and nobody's save is lost).</summary>
    public static async Task<DriveOutcome<DriveItem>> RestoreVersionAsync(AppDbContext db, Actor actor, long id, long versionId, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await DriveItems.ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, ct: ct);
            var version = node is null ? null : await db.DriveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.NodeId == id, ct);
            if (node is null || version is null)
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            if (version.Id == node.CurrentVersionId)
            {
                return DriveOutcome<DriveItem>.Success(await DriveItems.ItemAsync(db, actor, id, ct));
            }

            if (await DriveItems.QuotaProblemAsync(db, actor.UserId, version.Size, ct) is { } full)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.QuotaFull, full, "quota");
            }

            await AddVersionAsync(db, actor, node, version.Sha256, version.Size, ct);
            await DriveItems.TouchFolderAsync(db, node.ParentId, actor, ct);
            return DriveOutcome<DriveItem>.Success(await DriveItems.ItemAsync(db, actor, id, ct));
        }, ct);
    }

    /// <summary>Removes an older version (never the current one).</summary>
    public static async Task<DriveOutcome<bool>> DeleteVersionAsync(AppDbContext db, Actor actor, long id, long versionId, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        return await DriveItems.ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, trashed: true, ct: ct);
            if (node is null || node.CurrentVersionId == versionId)
            {
                return node is null ? DriveOutcome<bool>.Missing() : DriveOutcome<bool>.Fail(DriveFailure.Conflict, "Die aktuelle Version kann nicht gelöscht werden.");
            }

            var removed = await db.DriveVersions.Where(v => v.Id == versionId && v.NodeId == id).ExecuteDeleteAsync(ct);
            return removed == 0 ? DriveOutcome<bool>.Missing() : DriveOutcome<bool>.Success(true);
        }, ct);
    }

    // ---- uploads in pieces ----

    /// <summary>Starts an upload in pieces: every check of the target (name, folder, ETag, size, space) runs now, before the first byte.</summary>
    public static async Task<DriveOutcome<DriveUploadState>> StartUploadAsync(AppDbContext db, Actor actor, DriveUploadRequest? request, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        if (request is null || request.Size < 0)
        {
            return DriveOutcome<DriveUploadState>.Fail(DriveFailure.Invalid, "Die Anfrage braucht die Größe der Datei.", "size");
        }

        var maxBytes = DriveLimits.Current.MaxFileBytes;
        string? name = null;
        if (request.ItemId is { } itemId)
        {
            if (await CheckVersionAsync(db, actor, itemId, request.IfMatch, request.Size, maxBytes, ct) is { } refused)
            {
                return refused.As<DriveUploadState>();
            }
        }
        else
        {
            var (clean, problem) = DriveNames.Check(request.Name);
            if (problem is not null)
            {
                return DriveOutcome<DriveUploadState>.Fail(DriveFailure.Invalid, problem, "name");
            }

            name = clean;
            if (await CheckTargetAsync(db, actor, request.ParentId, clean!, request.OnConflict, request.Size, maxBytes, ct) is { } refused)
            {
                return refused.As<DriveUploadState>();
            }
        }

        var upload = new DriveUpload
        {
            UserId = actor.UserId,
            ParentId = request.ItemId is null ? request.ParentId : null,
            Name = name,
            OnConflict = request.OnConflict,
            NodeId = request.ItemId,
            IfMatch = request.IfMatch,
            Size = request.Size,
            ExpiresDate = DateTime.UtcNow.AddHours(DriveContract.UploadHours),
            Source = actor.Source,
            App = actor.App,
        };
        db.DriveUploads.Add(upload);
        await db.SaveChangesAsync(ct);
        return DriveOutcome<DriveUploadState>.Success(StateOf(upload));
    }

    /// <summary>How much of an upload arrived (to resume it); <c>Missing</c> for an upload of another account, finished or expired.</summary>
    public static async Task<DriveOutcome<DriveUploadState>> UploadStateAsync(AppDbContext db, Actor actor, Guid token, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var upload = await FindUploadAsync(db, actor, token, track: false, ct);
        return upload is null ? DriveOutcome<DriveUploadState>.Missing() : DriveOutcome<DriveUploadState>.Success(StateOf(upload));
    }

    /// <summary>
    /// Writes a piece at <paramref name="offset"/>: the pieces come in order; a piece sent again (after a broken connection) overwrites what it covers.
    /// A gap (an offset beyond what arrived) and bytes beyond the size are refused.
    /// </summary>
    public static async Task<DriveOutcome<DriveUploadState>> AppendAsync(AppDbContext db, DriveStore store, Actor actor, Guid token, long offset, Stream piece,
        long? declaredSize, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var upload = await FindUploadAsync(db, actor, token, track: true, ct);
        if (upload is null)
        {
            return DriveOutcome<DriveUploadState>.Missing();
        }

        if (offset < 0 || offset > upload.Received)
        {
            return DriveOutcome<DriveUploadState>.Fail(DriveFailure.Invalid, $"Es fehlen Daten: angekommen sind {upload.Received} Bytes.", "offset");
        }

        if (declaredSize > DriveContract.MaxChunkBytes || offset + (declaredSize ?? 0) > upload.Size)
        {
            return TooLarge<DriveUploadState>(Math.Min(DriveContract.MaxChunkBytes, upload.Size - offset));
        }

        var path = store.UploadPath(upload.Token);
        long length;
        await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            if (offset > file.Length)
            {
                upload.Received = file.Length;
                await db.SaveChangesAsync(ct);
                return DriveOutcome<DriveUploadState>.Fail(DriveFailure.Invalid, $"Es fehlen Daten: angekommen sind {file.Length} Bytes.", "offset");
            }

            file.Position = offset;
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await piece.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > DriveContract.MaxChunkBytes || offset + written > upload.Size)
                {
                    file.SetLength(offset);
                    return TooLarge<DriveUploadState>(Math.Min(DriveContract.MaxChunkBytes, upload.Size - offset));
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            file.SetLength(file.Position);
            length = file.Length;
        }

        upload.Received = length;
        await db.SaveChangesAsync(ct);
        return DriveOutcome<DriveUploadState>.Success(StateOf(upload));
    }

    /// <summary>Every byte arrived: the file becomes a new file or a new version, with the checks of a single request (409, 412, the space).</summary>
    public static async Task<DriveOutcome<DriveItem>> FinishUploadAsync(AppDbContext db, DriveStore store, Actor actor, Guid token, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var upload = await FindUploadAsync(db, actor, token, track: true, ct);
        if (upload is null)
        {
            return DriveOutcome<DriveItem>.Missing();
        }

        var path = store.UploadPath(upload.Token);
        if (upload.Size == 0 && !File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, [], ct);
        }

        if (!File.Exists(path) || new FileInfo(path).Length != upload.Size)
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.Invalid, $"Die Datei ist noch nicht vollständig ({upload.Received} von {upload.Size} Bytes).", "size");
        }

        var arrived = await DriveStore.HashAsync(path, ct);
        var outcome = upload.NodeId is { } nodeId
            ? await CommitVersionAsync(db, store, actor, nodeId, upload.IfMatch, arrived, ct)
            : await CommitNewAsync(db, store, actor, upload.ParentId, upload.Name!, upload.OnConflict, arrived, ct);
        await db.DriveUploads.Where(u => u.Id == upload.Id).ExecuteDeleteAsync(ct);
        DriveStore.TryDelete(path);
        return outcome;
    }

    public static async Task<bool> CancelUploadAsync(AppDbContext db, DriveStore store, Actor actor, Guid token, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var removed = await db.DriveUploads.Where(u => u.Token == token && u.UserId == actor.UserId).ExecuteDeleteAsync(ct);
        DriveStore.TryDelete(store.UploadPath(token));
        return removed > 0;
    }

    // ---- the space ----

    public static async Task<DriveQuota> QuotaAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await DriveAccess.RequireAsync(db, actor, ct);
        var (used, quota) = await DriveItems.UsedAndQuotaAsync(db, actor.UserId, ct);
        var nodes = DriveAccess.NodesOf(db, actor.UserId);
        var trash = await db.DriveVersions.Where(v => nodes.Any(n => n.Id == v.NodeId && n.InTrash)).SumAsync(v => (long?)v.Size, ct) ?? 0;
        var older = await db.DriveVersions.Where(v => nodes.Any(n => n.Id == v.NodeId && !n.InTrash && n.CurrentVersionId != v.Id)).SumAsync(v => (long?)v.Size, ct) ?? 0;
        return new DriveQuota(used, quota, trash, older, DriveLimits.Current.MaxFileBytes, DriveContract.MaxSimpleUploadBytes, DriveContract.ChunkBytes);
    }

    // ---- shared with the items (copy) ----

    /// <summary>A version of bytes already in the store becomes the current version of a file (inside the lock).</summary>
    internal static async Task AddVersionAsync(AppDbContext db, Actor actor, DriveNode node, string sha256, long size, CancellationToken ct)
    {
        var number = (await db.DriveVersions.Where(v => v.NodeId == node.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var version = new DriveNodeVersion
        {
            NodeId = node.Id,
            Number = number,
            Sha256 = sha256,
            Size = size,
            ContentType = DriveTypes.ContentTypeOf(node.Name),
            Source = actor.Source,
            App = actor.App,
        };
        db.DriveVersions.Add(version);
        await db.SaveChangesAsync(ct);

        var tracked = db.Entry(node).State == EntityState.Detached ? await db.DriveNodes.FirstAsync(n => n.Id == node.Id, ct) : node;
        tracked.CurrentVersionId = version.Id;
        tracked.VersionNumber = number;
        tracked.Sha256 = sha256;
        tracked.Size = size;
        tracked.ModifiedDate = DateTime.UtcNow;
        tracked.ModifiedBy = actor.Source;
        tracked.Revision = await DriveItems.NextRevisionAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---- the steps ----

    /// <summary>The checks of a new file that need no bytes: the folder, the name (with <see cref="DriveNameConflict.Fail"/>), the size, the space.</summary>
    private static async Task<DriveOutcome<DriveItem>?> CheckTargetAsync(AppDbContext db, Actor actor, long? parentId, string name, DriveNameConflict onConflict,
        long size, long maxBytes, CancellationToken ct)
    {
        var (found, _) = await DriveAccess.FolderAsync(db, actor, parentId, track: false, ct);
        if (!found)
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.NotFound, "Diesen Ordner gibt es nicht (mehr).", "parentId");
        }

        if (onConflict == DriveNameConflict.Fail && await DriveAccess.DriveOfAsync(db, actor.UserId, ct) is { } drive
            && await DriveItems.NamedAsync(db, drive.Id, parentId, name, null, ct) is { } taken)
        {
            return await DriveItems.NameTakenAsync<DriveItem>(db, actor, taken, ct);
        }

        return await SizeProblemAsync(db, actor, size, maxBytes, ct);
    }

    /// <summary>The checks of a new version that need no bytes: the file, If-Match, the size, the space.</summary>
    private static async Task<DriveOutcome<DriveItem>?> CheckVersionAsync(AppDbContext db, Actor actor, long id, string? ifMatch, long size, long maxBytes, CancellationToken ct)
    {
        var node = await DriveAccess.FindAsync(db, actor, id, ct: ct);
        if (node is not { Kind: DriveItemKind.File })
        {
            return DriveOutcome<DriveItem>.Missing();
        }

        if (await IfMatchProblemAsync(db, actor, node, ifMatch, ct) is { } refused)
        {
            return refused;
        }

        return await SizeProblemAsync(db, actor, size, maxBytes, ct);
    }

    private static async Task<DriveOutcome<DriveItem>?> SizeProblemAsync(AppDbContext db, Actor actor, long size, long maxBytes, CancellationToken ct)
    {
        if (size > maxBytes)
        {
            return TooLarge<DriveItem>(maxBytes);
        }

        return await DriveItems.QuotaProblemAsync(db, actor.UserId, size, ct) is { } full ? DriveOutcome<DriveItem>.Fail(DriveFailure.QuotaFull, full, "quota") : null;
    }

    private static async Task<DriveOutcome<DriveItem>?> IfMatchProblemAsync(AppDbContext db, Actor actor, DriveNode node, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return DriveOutcome<DriveItem>.Fail(DriveFailure.IfMatchMissing,
                "Eine neue Version braucht den Stand, auf dem sie beruht (If-Match mit dem ETag, oder * zum Überschreiben).", "If-Match");
        }

        if (Matches(ifMatch, node.CurrentVersionId))
        {
            return null;
        }

        var current = await DriveItems.ItemAsync(db, actor, node.Id, ct);
        return DriveOutcome<DriveItem>.Fail(DriveFailure.Changed,
            $"„{node.Name}“ wurde inzwischen geändert ({node.ModifiedBy ?? "anderswo"}, Version {node.VersionNumber}).", "If-Match", current);
    }

    /// <summary>Whether an If-Match header names the current version: "*", or one of its ETags (quoted or not, weak or strong).</summary>
    internal static bool Matches(string ifMatch, long? currentVersionId)
    {
        foreach (var part in ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == DriveContract.AnyVersion)
            {
                return true;
            }

            var tag = part.StartsWith("W/", StringComparison.Ordinal) ? part[2..] : part;
            if (currentVersionId is { } current && tag.Trim('"') == current.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ArrivedFile?> ReceiveAsync(DriveStore store, Stream body, long maxBytes, CancellationToken ct)
    {
        try
        {
            return await store.ReceiveAsync(body, maxBytes, ct);
        }
        catch (TooLargeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The arrived bytes go into the store, known to the table DriveBlob: a new content is moved into place, a known one is touched (so that the
    /// housekeeping keeps it) and the arrived copy is dropped. Before the lock of the Drive: hashing and moving never hold it.
    /// </summary>
    private static async Task KeepBlobAsync(AppDbContext db, DriveStore store, ArrivedFile arrived, CancellationToken ct)
    {
        var inserted = await Revisions.ScalarAsync(db,
            "INSERT INTO \"DriveBlob\" (\"Sha256\", \"Size\", \"CreateDate\", \"UpdateDate\") VALUES (@p0, @p1, now(), now()) " +
            "ON CONFLICT (\"Sha256\") DO UPDATE SET \"UpdateDate\" = now() RETURNING CASE WHEN xmax = 0 THEN 1 ELSE 0 END",
            ct, arrived.Sha256, arrived.Size);
        store.Keep(arrived, isNew: inserted == 1);
    }

    private static async Task<DriveOutcome<DriveItem>> CommitNewAsync(AppDbContext db, DriveStore store, Actor actor, long? parentId, string name,
        DriveNameConflict onConflict, ArrivedFile arrived, CancellationToken ct)
    {
        await KeepBlobAsync(db, store, arrived, ct);
        return await DriveItems.ChangeAsync(db, actor, async () =>
        {
            var (found, parent) = await DriveAccess.FolderAsync(db, actor, parentId, track: false, ct);
            if (!found)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.NotFound, "Diesen Ordner gibt es nicht (mehr).", "parentId");
            }

            if (await DriveItems.QuotaProblemAsync(db, actor.UserId, arrived.Size, ct) is { } full)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.QuotaFull, full, "quota");
            }

            var drive = await DriveItems.EnsureDriveAsync(db, actor.UserId, ct);
            var finalName = name;
            if (await DriveItems.NamedAsync(db, drive.Id, parentId, name, null, ct) is { } taken)
            {
                if (onConflict == DriveNameConflict.Fail)
                {
                    return await DriveItems.NameTakenAsync<DriveItem>(db, actor, taken, ct);
                }

                finalName = await DriveItems.FreeNameAsync(db, drive.Id, parentId, name, DriveItemKind.File, null, ct);
            }

            var node = await DriveItems.AddNodeAsync(db, actor, drive.Id, parent, DriveItemKind.File, finalName, ct);
            await AddVersionAsync(db, actor, node, arrived.Sha256, arrived.Size, ct);
            await DriveItems.TouchFolderAsync(db, parentId, actor, ct);
            await VisitAsync(db, actor, node.Id, ct);
            return DriveOutcome<DriveItem>.Success(await DriveItems.ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    private static async Task<DriveOutcome<DriveItem>> CommitVersionAsync(AppDbContext db, DriveStore store, Actor actor, long id, string? ifMatch,
        ArrivedFile arrived, CancellationToken ct)
    {
        await KeepBlobAsync(db, store, arrived, ct);
        return await DriveItems.ChangeAsync(db, actor, async () =>
        {
            var node = await DriveAccess.FindAsync(db, actor, id, track: true, ct: ct);
            if (node is not { Kind: DriveItemKind.File })
            {
                return DriveOutcome<DriveItem>.Missing();
            }

            if (await IfMatchProblemAsync(db, actor, node, ifMatch, ct) is { } refused)
            {
                return refused;
            }

            if (await DriveItems.QuotaProblemAsync(db, actor.UserId, arrived.Size, ct) is { } full)
            {
                return DriveOutcome<DriveItem>.Fail(DriveFailure.QuotaFull, full, "quota");
            }

            await AddVersionAsync(db, actor, node, arrived.Sha256, arrived.Size, ct);
            await DriveItems.TouchFolderAsync(db, node.ParentId, actor, ct);
            await VisitAsync(db, actor, node.Id, ct);
            return DriveOutcome<DriveItem>.Success(await DriveItems.ItemAsync(db, actor, node.Id, ct));
        }, ct);
    }

    /// <summary>The account opened or saved the file now (the list "Zuletzt verwendet").</summary>
    private static Task VisitAsync(AppDbContext db, Actor actor, long nodeId, CancellationToken ct)
        => Revisions.ExecuteAsync(db,
            "INSERT INTO \"DriveVisit\" (\"UserId\", \"NodeId\", \"VisitDate\", \"App\", \"CreateDate\", \"CreateUserId\", \"UpdateDate\", \"UpdateUserId\") " +
            "VALUES (@p0, @p1, now(), @p2, now(), @p0, now(), @p0) " +
            "ON CONFLICT (\"UserId\", \"NodeId\") DO UPDATE SET \"VisitDate\" = now(), \"App\" = EXCLUDED.\"App\", \"UpdateDate\" = now()",
            ct, actor.UserId, nodeId, (object?)actor.App ?? DBNull.Value);

    private static async Task<DriveUpload?> FindUploadAsync(AppDbContext db, Actor actor, Guid token, bool track, CancellationToken ct)
    {
        var query = db.DriveUploads.Where(u => u.Token == token && u.UserId == actor.UserId && u.ExpiresDate > DateTime.UtcNow);
        return await (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private static DriveUploadState StateOf(DriveUpload upload)
        => new(upload.Token, upload.Size, upload.Received, DriveContract.ChunkBytes, DriveItems.Utc(upload.ExpiresDate));

    private static DriveOutcome<T> TooLarge<T>(long maxBytes)
        => DriveOutcome<T>.Fail(DriveFailure.TooLarge, $"Die Datei ist zu groß (höchstens {Core.Web.Ui.Bytes(maxBytes)}).", "size");
}
