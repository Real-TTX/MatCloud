using MatCloud.Apps.Note.Api;
using MatCloud.Apps.Note.Business;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Note;

/// <summary>
/// ThixNote in MatCloud: the notes of an account in the browser. No tables of its own: ThixNote keeps its notes in the general sync, and this module is a
/// web client of the same items (<see cref="UsesGeneralSync"/>). Pages <c>/Note</c> (list, a note, write); the live list waits on <c>/live/note/revision</c>.
/// </summary>
public sealed class NoteModule : ICloudModule
{
    public string AppId => CloudApps.Note;

    public string Home => "/Note";

    public string Icon => "file-text";

    public string MenuTitle => "Notizen";

    public string MenuPartial => "_NoteMenu";

    public long? MaxRequestBodyBytes => null;

    public bool UsesGeneralSync => true;

    /// <summary>
    /// A protected note becomes plain text only through a deliberate "Schutz aufheben", and its marker stays on the note while it stays plain
    /// (<see cref="NoteService.ReviewChange"/>).
    /// </summary>
    public SyncItemDto? ReviewSyncChange(SyncItemDto current, SyncItemDto incoming) => NoteService.ReviewChange(current, incoming);

    public void AddServices(IServiceCollection services)
    {
    }

    public void ConfigureModel(ModelBuilder model)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => NoteEndpoints.Map(routes);

    public Task<int> MaintainAsync(AppDbContext db, CancellationToken ct) => Task.FromResult(0);

    public async Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct)
        => await db.SyncItems.Where(i => i.UserId == userId && i.App == AppId).ExecuteDeleteAsync(ct);

    public Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct) => SyncService.UsageAsync(db, userId, AppId, ct);

    public Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct) => SyncService.TotalUsageAsync(db, AppId, ct);
}
