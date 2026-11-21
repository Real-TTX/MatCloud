using MatCloud.Apps.Calendar.Api;
using MatCloud.Apps.Calendar.Business;
using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Calendar;

/// <summary>
/// ThixCalendar in MatCloud: the calendars of an account in the browser (pages <c>/Calendar</c>) and the accounts MatCloud keeps in step on the server
/// (<see cref="CalendarWorker"/>). ThixCalendar keeps its data in the general sync (<see cref="UsesGeneralSync"/>); the table CalendarConnection holds what
/// only the server needs (the protected sign-ins and how the last sync went).
/// </summary>
public sealed class CalendarModule : ICloudModule
{
    public string AppId => CloudApps.Calendar;

    public string Home => "/Calendar";

    public string Icon => "calendar";

    public string MenuTitle => "Kalender";

    public string MenuPartial => "_CalendarMenu";

    public long? MaxRequestBodyBytes => null;

    public bool UsesGeneralSync => true;

    /// <summary>ThixMail shows the calendar of its account and answers invitations (<see cref="MatCloud.Contract.Calendar.CalendarContract.GuestApps"/>).</summary>
    public IReadOnlyList<string> GuestApps => MatCloud.Contract.Calendar.CalendarContract.GuestApps;

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<CalendarKeys>();
        services.AddHostedService<CalendarWorker>();
    }

    public void ConfigureModel(ModelBuilder model) => CalendarModel.Configure(model);

    public void MapEndpoints(IEndpointRouteBuilder routes) => CalendarEndpoints.Map(routes);

    public Task<int> MaintainAsync(AppDbContext db, CancellationToken ct) => Task.FromResult(0);

    public async Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        await db.CalendarConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SyncItems.Where(i => i.UserId == userId && i.App == AppId).ExecuteDeleteAsync(ct);
    }

    public Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct) => SyncService.UsageAsync(db, userId, AppId, ct);

    public Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct) => SyncService.TotalUsageAsync(db, AppId, ct);
}
