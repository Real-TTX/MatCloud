using MatCloud.Apps.Contacts.Api;
using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract;
using MatCloud.Contract.Contacts;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Contacts;

/// <summary>
/// ThixContacts in MatCloud: the contacts and groups of an account in the browser (<c>/Contacts</c>). No tables of its own: ThixContacts keeps them in
/// the general sync (<see cref="UsesGeneralSync"/>, collections <c>contacts</c> and <c>contact-groups</c>), the module is a web client of the same items.
/// The pictures of contacts are kept in the data volume (<see cref="ContactPhotoStore"/>, routes <c>/api/v1/contacts/photos</c>). The grants: ThixMail
/// reads and writes the contacts of its account, ThixCalendar reads them.
/// </summary>
public sealed class ContactsModule : ICloudModule
{
    public string AppId => CloudApps.Contacts;

    public string Home => "/Contacts";

    public string Icon => "users";

    public string MenuTitle => "Kontakte";

    public string MenuPartial => "_ContactsMenu";

    /// <summary>A picture of a contact, or a vCard file with pictures uploaded in the browser.</summary>
    public long? MaxRequestBodyBytes => 16L * 1024 * 1024;

    public bool UsesGeneralSync => true;

    public IReadOnlyList<string> GuestApps => ContactsContract.GuestApps;

    public IReadOnlyList<string> ReadingGuestApps => ContactsContract.ReadingGuestApps;

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<ContactPhotoStore>();
        services.AddHostedService<ContactPhotoWorker>();
    }

    public void ConfigureModel(ModelBuilder model)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => ContactsEndpoints.Map(routes);

    public Task<int> MaintainAsync(AppDbContext db, CancellationToken ct) => Task.FromResult(0);

    /// <summary>The items go; the pictures follow with the next housekeeping (no contact names them any more).</summary>
    public async Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        await db.SyncItems.Where(i => i.UserId == userId && i.App == AppId).ExecuteDeleteAsync(ct);
        RevisionSignals.Notify(Revisions.Stream(AppId, userId));
    }

    public Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct) => SyncService.UsageAsync(db, userId, AppId, ct);

    public Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct) => SyncService.TotalUsageAsync(db, AppId, ct);
}
