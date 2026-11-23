using MatCloud.Apps.Drive.Api;
using MatCloud.Apps.Drive.Business;
using MatCloud.Apps.Drive.Data;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive;

/// <summary>
/// ThixDrive in MatCloud: the files and folders of an account – in the browser (<c>/Drive</c>) and for the devices of ThixDrive and of the office
/// apps ThixWrite, ThixCalc and ThixShow (<see cref="GuestApps"/>: they open and save their documents here) and ThixSync (a source or target of its sync jobs). Tables DriveSpace, DriveNode,
/// DriveNodeVersion, DriveBlob, DriveStar, DriveVisit, DriveUpload and the sequence DriveRevision; the bytes in the data volume (<see cref="DriveStore"/>);
/// the device API <c>/api/v1/drive</c>; the pages <c>/Drive</c> with the live list.
/// </summary>
public sealed class DriveModule : ICloudModule
{
    public string AppId => CloudApps.Drive;

    public string Home => "/Drive";

    public string Icon => "folder";

    public string MenuTitle => "Ablage";

    public string MenuPartial => "_DriveMenu";

    /// <summary>
    /// The default body limit of the server: one piece of an upload (and room around it). The routes that take a whole file in one request raise their
    /// own limit to <see cref="DriveContract.MaxSimpleUploadBytes"/>; larger files come in pieces.
    /// </summary>
    public long? MaxRequestBodyBytes => DriveContract.MaxChunkBytes + 1024 * 1024;

    /// <summary>The office apps open and save their documents in the Drive of their account (the grant, see docs/ARCHITECTURE.md).</summary>
    public IReadOnlyList<string> GuestApps => DriveContract.GuestApps;

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<DriveStore>();
        services.AddHostedService<DriveWorker>();
    }

    public void ConfigureModel(ModelBuilder model) => DriveModel.Configure(model);

    public void MapEndpoints(IEndpointRouteBuilder routes) => DriveEndpoints.Map(routes);

    public Task<int> MaintainAsync(AppDbContext db, CancellationToken ct) => DriveHousekeeping.MaintainAsync(db, ct);

    /// <summary>Everything of the account goes (the bytes with the next housekeeping, when no other file names them).</summary>
    public async Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        await db.DriveUploads.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DriveStars.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DriveVisits.Where(v => v.UserId == userId).ExecuteDeleteAsync(ct);
        await db.DriveSpaces.IgnoreQueryFilters().Where(d => d.OwnerUserId == userId).ExecuteDeleteAsync(ct);
        RevisionSignals.Notify(Revisions.Stream(CloudApps.Drive, userId));
    }

    public async Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct)
        => await UsageOfAsync(db, db.DriveNodes.IgnoreQueryFilters().Where(n => n.Drive!.OwnerUserId == userId), ct);

    public async Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct)
        => await UsageOfAsync(db, db.DriveNodes.IgnoreQueryFilters(), ct);

    /// <summary>Files (the trash included) and the bytes of every version; the last change of any node, tombstones included.</summary>
    private static async Task<AppUsage> UsageOfAsync(AppDbContext db, IQueryable<DriveNode> nodes, CancellationToken ct)
    {
        var files = await nodes.LongCountAsync(n => n.UpdateState != SoftEntity.Deleted && n.Kind == DriveItemKind.File, ct);
        var bytes = await db.DriveVersions.Where(v => nodes.Any(n => n.Id == v.NodeId)).SumAsync(v => (long?)v.Size, ct) ?? 0;
        var last = await nodes.MaxAsync(n => (DateTime?)n.UpdateDate, ct);
        return new AppUsage(files, bytes, last);
    }
}
