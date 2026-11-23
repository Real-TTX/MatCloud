using MatCloud.Apps.Drive.Data;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive.Business;

/// <summary>Why a change of the Drive did not happen; the API answers each with its status (<see cref="DriveContract"/>), the pages with the message.</summary>
public enum DriveFailure
{
    None = 0,

    /// <summary>400: a bad value (the message names it, <see cref="DriveOutcome{T}.Field"/> the field).</summary>
    Invalid,

    /// <summary>404: no such item – or one of another account (both answer the same, so nothing can be probed).</summary>
    NotFound,

    /// <summary>409: the name is taken in the folder (<see cref="DriveOutcome{T}.Current"/> is the item that has it).</summary>
    NameTaken,

    /// <summary>409: a folder would go into itself, or a delete for good of an item that is not in the trash.</summary>
    Conflict,

    /// <summary>412: the file was saved by somebody else since it was opened (<see cref="DriveOutcome{T}.Current"/>: the file as it is now).</summary>
    Changed,

    /// <summary>428: a new version without If-Match.</summary>
    IfMatchMissing,

    /// <summary>413: larger than the largest file of the installation (or than one request may carry).</summary>
    TooLarge,

    /// <summary>507: the space of the account is full.</summary>
    QuotaFull,
}

/// <summary>The answer of the business layer of the Drive: the value, or why not (worded for the person, in German).</summary>
public sealed record DriveOutcome<T>(T? Value, DriveFailure Failure = DriveFailure.None, string? Error = null, string? Field = null, DriveItem? Current = null)
{
    public bool Ok => Failure == DriveFailure.None;

    public static DriveOutcome<T> Success(T value) => new(value);

    public static DriveOutcome<T> Fail(DriveFailure failure, string error, string? field = null, DriveItem? current = null) => new(default, failure, error, field, current);

    public static DriveOutcome<T> Missing() => new(default, DriveFailure.NotFound, "Diese Datei oder diesen Ordner gibt es nicht (mehr).");

    /// <summary>The same failure for another value type.</summary>
    public DriveOutcome<TOther> As<TOther>() => new(default, Failure, Error, Field, Current);
}

/// <summary>
/// Who may reach the Drive, and whose items an actor sees. The browser of the account and the devices of ThixDrive, ThixWrite, ThixCalc, ThixShow and ThixSync
/// (the grant of the module, <see cref="DriveContract.GuestApps"/>) reach the Drive of THEIR account; a device of any other app gets
/// <see cref="NotAllowedException"/> (403), and so does everybody while an administrator has switched the Drive off. An administrator has no access to
/// the files of others: an item of another account does not exist for the actor (404). Every function of the business layer starts here.
/// </summary>
public static class DriveAccess
{
    /// <summary>Throws <see cref="NotAllowedException"/> when the actor may not use the Drive (another app, or the Drive switched off).</summary>
    public static async Task RequireAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        if (!actor.MayUse(CloudApps.Drive))
        {
            throw new NotAllowedException($"Die Ablage ist für ThixDrive, ThixWrite, ThixCalc, ThixShow und ThixSync; dieses Gerät ist für {CloudApps.NameOf(actor.App)} angemeldet.");
        }

        if (!await AppService.IsEnabledAsync(db, CloudApps.Drive, ct))
        {
            throw new NotAllowedException(AppService.SwitchedOffText(CloudApps.Drive));
        }
    }

    /// <summary>Whether the actor may use the Drive (for the pages: they show 404 instead of an error).</summary>
    public static async Task<bool> MayUseAsync(AppDbContext db, Actor actor, CancellationToken ct)
        => actor.MayUse(CloudApps.Drive) && await AppService.IsEnabledAsync(db, CloudApps.Drive, ct);

    /// <summary>The personal drive of the account, or null while it has none (it is made at the first change).</summary>
    public static Task<DriveSpace?> DriveOfAsync(AppDbContext db, long userId, CancellationToken ct)
        => db.DriveSpaces.AsNoTracking().FirstOrDefaultAsync(d => d.Kind == DriveKind.Personal && d.OwnerUserId == userId, ct);

    /// <summary>The nodes the account reaches (its personal drive), the trash included, the tombstones not.</summary>
    public static IQueryable<DriveNode> NodesOf(AppDbContext db, long userId)
        => db.DriveNodes.Where(n => n.Drive!.Kind == DriveKind.Personal && n.Drive.OwnerUserId == userId);

    /// <summary>A node of the actor's drive, or null (also for an item of another account). Not in the trash unless <paramref name="trashed"/>.</summary>
    public static async Task<DriveNode?> FindAsync(AppDbContext db, Actor actor, long id, bool track = false, bool trashed = false, CancellationToken ct = default)
    {
        var query = NodesOf(db, actor.UserId).Where(n => n.Id == id);
        var node = await (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
        return node is null || (node.InTrash && !trashed) ? null : node;
    }

    /// <summary>A folder to put something into: null for the top, the folder, or <c>Missing</c> (no such folder, a file, or in the trash).</summary>
    public static async Task<(bool Found, DriveNode? Folder)> FolderAsync(AppDbContext db, Actor actor, long? id, bool track, CancellationToken ct)
    {
        if (id is null)
        {
            return (true, null);
        }

        var node = await FindAsync(db, actor, id.Value, track, ct: ct);
        return node is { Kind: DriveItemKind.Folder } ? (true, node) : (false, null);
    }

    /// <summary>The ids of a path (<c>/12/40/41/</c> → 12, 40, 41).</summary>
    public static long[] IdsOf(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => long.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}
