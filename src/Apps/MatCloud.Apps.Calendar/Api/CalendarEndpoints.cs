using MatCloud.Apps.Calendar.Business;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Api;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using static MatCloud.Core.Api.DeviceEndpoints;

namespace MatCloud.Apps.Calendar.Api;

/// <summary>
/// The routes of the module: the API of the ThixCalendar devices (<see cref="CalendarContract"/>: hand a sign-in over, let an account go, sync now, change
/// an event of an account MatCloud keeps; only a device of ThixCalendar while the app is on) and what the pages fetch with the cookie of the browser
/// (<c>/live/calendar</c>: the long poll of the live pages, the poll of the sign-in at Microsoft with a code).
/// </summary>
public static class CalendarEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var device = DeviceEndpoints.ForApp(app, CloudApps.Calendar);
        device.MapPut(CalendarContract.Connections, async (HttpContext http, AppDbContext db, CalendarKeys keys, CalendarHandover? handover) =>
            Answer(await CalendarConnections.HandOverAsync(db, keys, ActorOf(http), handover, http.RequestAborted)));
        device.MapPost(CalendarContract.Disconnect, async (HttpContext http, AppDbContext db, CalendarAccountRequest? request) =>
        {
            var outcome = await CalendarConnections.DisconnectAsync(db, ActorOf(http), request?.Account, http.RequestAborted);
            return outcome.Ok ? Results.NoContent() : Error(StatusCodes.Status400BadRequest, outcome.Error ?? "Nicht gefunden.", outcome.Field);
        });
        device.MapPost(CalendarContract.SyncNow, async (HttpContext http, AppDbContext db, CalendarAccountRequest? request) =>
        {
            await CalendarConnections.SyncNowAsync(db, ActorOf(http), request?.Account, http.RequestAborted);
            return Results.Accepted();
        });
        device.MapPost(CalendarContract.EventChange, async (HttpContext http, AppDbContext db, CalendarKeys keys, CalendarEventChange? change) =>
            Answer(await CalendarEvents.ChangeCloudAsync(db, keys, ActorOf(http), change, http.RequestAborted)));

        var live = app.MapGroup("/live/calendar").RequireAuthorization();
        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await SyncService.WaitAsync(db, me.Actor, CalendarItems.App, since ?? 0, WaitTime(timeout), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });
        live.MapGet("/microsoft/{id}", async (HttpContext http, AppDbContext db, CalendarKeys keys, CurrentUser me, string id) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var outcome = await CalendarConnections.PollMicrosoftAsync(db, keys, me.Actor, id, http.RequestAborted);
            return outcome.Ok
                ? Results.Json(new { done = outcome.Value is not null, account = outcome.Value })
                : Results.Json(new { done = true, message = outcome.Error }, statusCode: StatusCodes.Status400BadRequest);
        });
    }
}
