using MatCloud.Apps.Clip.Api;
using MatCloud.Apps.Clip.Business;
using MatCloud.Apps.Clip.Data;
using MatCloud.Contract;
using MatCloud.Contract.Clip;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Clip;

/// <summary>
/// ThixClip in MatCloud: the clipboard history of an account, the same on every PC and Mac with ThixClip and in the browser. Tables ClipEntry, ClipImage,
/// ClipAccount and the sequence ClipRevision; the device API <c>/api/v1/clip</c>; the pages <c>/Clip</c> (history, entry, settings) with the live list.
/// </summary>
public sealed class ClipModule : ICloudModule
{
    public string AppId => CloudApps.Clip;

    public string Home => "/Clip";

    public string Icon => "clipboard";

    public string MenuTitle => "Zwischenablage";

    public string MenuPartial => "_ClipMenu";

    /// <summary>Pictures up to the limit of the contract (plus room for a form around them).</summary>
    public long? MaxRequestBodyBytes => ClipContract.MaxImageBytes + 2 * 1024 * 1024;

    public void AddServices(IServiceCollection services)
    {
    }

    public void ConfigureModel(ModelBuilder model) => ClipModel.Configure(model);

    public void MapEndpoints(IEndpointRouteBuilder routes) => ClipEndpoints.Map(routes);

    public Task<int> MaintainAsync(AppDbContext db, CancellationToken ct) => ClipService.ApplyRetentionForAllAsync(db, ct);

    public async Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        await db.Clips.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ClipAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        RevisionSignals.Notify(Revisions.Stream(CloudApps.Clip, userId));
    }

    public async Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct)
        => await UsageOfAsync(db.Clips.AsNoTracking().Where(c => c.UserId == userId), ct);

    public async Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct)
        => await UsageOfAsync(db.Clips.AsNoTracking(), ct);

    private static async Task<AppUsage> UsageOfAsync(IQueryable<ClipEntry> clips, CancellationToken ct)
    {
        var live = clips.Where(c => c.UpdateState != SoftEntity.Deleted);
        var count = await live.LongCountAsync(ct);
        var bytes = await live.SumAsync(c => (long)(c.Text == null ? 0 : c.Text.Length) + (c.ImageBytes ?? 0), ct);
        var last = await clips.MaxAsync(c => (DateTime?)c.UpdateDate, ct);
        return new AppUsage(count, bytes, last);
    }
}
