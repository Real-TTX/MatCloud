using MatCloud.Apps.Clip.Business;
using MatCloud.Contract;
using MatCloud.Contract.Clip;
using MatCloud.Core.Api;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using static MatCloud.Core.Api.DeviceEndpoints;

namespace MatCloud.Apps.Clip.Api;

/// <summary>
/// The routes of the module: the API of the ThixClip devices (<see cref="ClipContract"/>, only a device of ThixClip while the app is on) and what the
/// pages fetch with the cookie of the browser (<c>/live/clip</c>: the long poll of the live list and the pictures; 401 without a session, never the
/// sign-in page).
/// </summary>
public static class ClipEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var device = DeviceEndpoints.ForApp(app, CloudApps.Clip);
        device.MapGet(ClipContract.Settings, async (HttpContext http, AppDbContext db) => Json(await ClipSettingsService.GetAsync(db, ActorOf(http), http.RequestAborted)));
        device.MapPut(ClipContract.Settings, async (HttpContext http, AppDbContext db, ClipSettings? update) =>
            update is null
                ? Error(StatusCodes.Status400BadRequest, "Die Anfrage enthält keine Einstellungen.")
                : Answer(await ClipSettingsService.SetRetentionAsync(db, ActorOf(http), update.RetentionDays, http.RequestAborted)));
        device.MapGet(ClipContract.Changes, async (HttpContext http, AppDbContext db, long? since, int? limit) =>
            Json(await ClipSyncService.ChangesAsync(db, ActorOf(http), since ?? 0, limit ?? ClipSyncService.DefaultLimit, http.RequestAborted)));
        device.MapGet(ClipContract.Wait, (HttpContext http, AppDbContext db, long? since, int? timeout) =>
            WaitAsync(http, () => ClipService.WaitAsync(db, ActorOf(http), since ?? 0, WaitTime(timeout), http.RequestAborted)));
        device.MapPost(ClipContract.Push, async (HttpContext http, AppDbContext db, ClipPushRequest? request) =>
        {
            if (ClipSyncService.Validate(request) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem.Message, problem.Field);
            }

            return Json(await ClipSyncService.PushAsync(db, ActorOf(http), request!, http.RequestAborted));
        });
        device.MapPut("/api/v1/clip/{syncId:guid}/image", UploadImageAsync);
        device.MapGet("/api/v1/clip/{syncId:guid}/image", async (HttpContext http, AppDbContext db, Guid syncId) =>
            Png(await ClipSyncService.ImageAsync(db, ActorOf(http), syncId, false, http.RequestAborted)));
        device.MapGet("/api/v1/clip/{syncId:guid}/thumbnail", async (HttpContext http, AppDbContext db, Guid syncId) =>
            Png(await ClipSyncService.ImageAsync(db, ActorOf(http), syncId, true, http.RequestAborted)));

        var live = app.MapGroup("/live/clip").RequireAuthorization();

        // The list in the browser waits here for the next change of the account (another PC, a phone, the retention) and reloads itself.
        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await ClipService.WaitAsync(db, me.Actor, since ?? 0, WaitTime(timeout), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });

        live.MapGet("/{id:long}/image", async (HttpContext http, AppDbContext db, CurrentUser me, long id, bool? download) =>
        {
            var bytes = await ClipService.ImageAsync(db, me.Actor, id, thumbnail: false);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=3600";
            return download == true
                ? Results.File(bytes, "image/png", $"ThixClip-{id}.png")
                : Results.File(bytes, "image/png");
        });

        live.MapGet("/{id:long}/thumbnail", async (HttpContext http, AppDbContext db, CurrentUser me, long id) =>
        {
            var bytes = await ClipService.ImageAsync(db, me.Actor, id, thumbnail: true);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            // A picture of a clip never changes (a new picture is a new clip): the browser and the service worker may keep it.
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(bytes, "image/png");
        });
    }

    private static async Task<IResult> UploadImageAsync(HttpContext http, AppDbContext db, Guid syncId)
    {
        var bytes = await ReadBodyAsync(http.Request, ClipContract.MaxImageBytes, http.RequestAborted);
        if (bytes is null)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, $"Das Bild ist größer als {ClipContract.MaxImageBytes / 1024 / 1024} MB.", "image");
        }

        var outcome = await ClipSyncService.UploadImageAsync(db, ActorOf(http), syncId, bytes, http.RequestAborted);
        if (outcome.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Diesen Eintrag gibt es nicht (mehr).");
        }

        return outcome.Ok ? Json(new { revision = outcome.Value }) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
    }

    private static IResult Png(byte[]? bytes)
        => bytes is null
            ? Error(StatusCodes.Status404NotFound, "Dieses Bild gibt es nicht (mehr) oder es wurde noch nicht hochgeladen.")
            : Results.File(bytes, "image/png");
}
