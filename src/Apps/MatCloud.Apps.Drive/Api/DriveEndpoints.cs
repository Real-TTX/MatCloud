using MatCloud.Apps.Drive.Business;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Api;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using static MatCloud.Core.Api.DeviceEndpoints;

namespace MatCloud.Apps.Drive.Api;

/// <summary>
/// The routes of the Drive: the device API (<see cref="DriveContract"/>; a device of ThixDrive, ThixWrite, ThixCalc or ThixShow while the Drive is on)
/// and what the pages fetch with the cookie of the browser (<c>/live/drive</c>: the long poll of the live list, downloads, uploads in pieces; 401
/// without a session). Every route hands the request to the business layer, which checks the actor again.
/// </summary>
public static class DriveEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapDevices(app);
        MapLive(app);
    }

    private static void MapDevices(IEndpointRouteBuilder app)
    {
        var device = ForApp(app, CloudApps.Drive);

        device.MapGet(DriveContract.Items, async (HttpContext http, AppDbContext db, long? parentId, string? sort, bool? descending, int? offset, int? limit) =>
            Answer(await DriveItems.ListAsync(db, ActorOf(http), parentId, Query(sort, descending, offset, limit), http.RequestAborted)));
        device.MapGet(DriveContract.Items + "/{id:long}", async (HttpContext http, AppDbContext db, long id) =>
            Answer(await DriveItems.GetAsync(db, ActorOf(http), id, http.RequestAborted)));
        device.MapGet(DriveContract.Search, async (HttpContext http, AppDbContext db, string? q, int? offset, int? limit) =>
            Json(await DriveItems.SearchAsync(db, ActorOf(http), q, Query(null, null, offset, limit), http.RequestAborted)));
        device.MapGet(DriveContract.Recent, async (HttpContext http, AppDbContext db, int? limit) =>
            Json(await DriveItems.RecentAsync(db, ActorOf(http), limit ?? 30, http.RequestAborted)));
        device.MapGet(DriveContract.Starred, async (HttpContext http, AppDbContext db, int? offset, int? limit) =>
            Json(await DriveItems.StarredAsync(db, ActorOf(http), Query(null, null, offset, limit), http.RequestAborted)));
        device.MapGet(DriveContract.TrashList, async (HttpContext http, AppDbContext db, int? offset, int? limit) =>
            Json(await DriveItems.TrashAsync(db, ActorOf(http), Query("Modified", null, offset, limit), http.RequestAborted)));
        device.MapGet(DriveContract.Quota, async (HttpContext http, AppDbContext db) => Json(await DriveFiles.QuotaAsync(db, ActorOf(http), http.RequestAborted)));

        device.MapPost(DriveContract.Folders, async (HttpContext http, AppDbContext db, DriveFolderRequest? request) =>
            Answer(await DriveItems.CreateFolderAsync(db, ActorOf(http), request?.ParentId, request?.Name, http.RequestAborted), StatusCodes.Status201Created));
        device.MapPost(DriveContract.Items + "/{id:long}/rename", async (HttpContext http, AppDbContext db, long id, DriveRenameRequest? request) =>
            Answer(await DriveItems.RenameAsync(db, ActorOf(http), id, request?.Name, http.RequestAborted)));
        device.MapPost(DriveContract.Items + "/{id:long}/move", async (HttpContext http, AppDbContext db, long id, DriveMoveRequest? request) =>
            Answer(await DriveItems.MoveAsync(db, ActorOf(http), id, request?.ParentId, http.RequestAborted)));
        device.MapPost(DriveContract.Items + "/{id:long}/copy", async (HttpContext http, AppDbContext db, long id, DriveCopyRequest? request) =>
            Answer(await DriveItems.CopyAsync(db, ActorOf(http), id, request?.ParentId, request?.Name, http.RequestAborted), StatusCodes.Status201Created));
        device.MapPost(DriveContract.Items + "/{id:long}/trash", async (HttpContext http, AppDbContext db, long id) =>
            Answer(await DriveItems.TrashAsync(db, ActorOf(http), id, http.RequestAborted)));
        device.MapPost(DriveContract.Items + "/{id:long}/restore", async (HttpContext http, AppDbContext db, long id) =>
            Answer(await DriveItems.RestoreAsync(db, ActorOf(http), id, http.RequestAborted)));
        device.MapDelete(DriveContract.Items + "/{id:long}", async (HttpContext http, AppDbContext db, long id) =>
            NoContent(await DriveItems.DeleteAsync(db, ActorOf(http), id, http.RequestAborted)));
        device.MapPost(DriveContract.Items + "/{id:long}/star", async (HttpContext http, AppDbContext db, long id) =>
            NoContent(await DriveItems.StarAsync(db, ActorOf(http), id, true, http.RequestAborted)));
        device.MapDelete(DriveContract.Items + "/{id:long}/star", async (HttpContext http, AppDbContext db, long id) =>
            NoContent(await DriveItems.StarAsync(db, ActorOf(http), id, false, http.RequestAborted)));

        device.MapGet(DriveContract.Items + "/{id:long}/content", async (HttpContext http, AppDbContext db, DriveStore store, long id) =>
            Content(await DriveFiles.OpenAsync(db, store, ActorOf(http), id, ct: http.RequestAborted), download: false));
        device.MapPut(DriveContract.Items + "/{id:long}/content", async (HttpContext http, AppDbContext db, DriveStore store, long id) =>
        {
            AllowBody(http, DriveContract.MaxSimpleUploadBytes);
            var limit = Math.Min(DriveContract.MaxSimpleUploadBytes, DriveLimits.Current.MaxFileBytes);
            return Answer(await DriveFiles.SaveVersionAsync(db, store, ActorOf(http), id, http.Request.Headers.IfMatch.ToString(), http.Request.Body,
                http.Request.ContentLength, limit, http.RequestAborted));
        });
        device.MapPost(DriveContract.Files, async (HttpContext http, AppDbContext db, DriveStore store, long? parentId, string? name, string? onConflict) =>
        {
            AllowBody(http, DriveContract.MaxSimpleUploadBytes);
            var limit = Math.Min(DriveContract.MaxSimpleUploadBytes, DriveLimits.Current.MaxFileBytes);
            return Answer(await DriveFiles.CreateAsync(db, store, ActorOf(http), parentId, name, ConflictOf(onConflict), http.Request.Body,
                http.Request.ContentLength, limit, http.RequestAborted), StatusCodes.Status201Created);
        });

        device.MapGet(DriveContract.Items + "/{id:long}/versions", async (HttpContext http, AppDbContext db, long id) =>
            Answer(await DriveFiles.VersionsAsync(db, ActorOf(http), id, http.RequestAborted)));
        device.MapGet(DriveContract.Items + "/{id:long}/versions/{versionId:long}/content", async (HttpContext http, AppDbContext db, DriveStore store, long id, long versionId) =>
            Content(await DriveFiles.OpenAsync(db, store, ActorOf(http), id, versionId, ct: http.RequestAborted), download: false));
        device.MapPost(DriveContract.Items + "/{id:long}/versions/{versionId:long}/restore", async (HttpContext http, AppDbContext db, long id, long versionId) =>
            Answer(await DriveFiles.RestoreVersionAsync(db, ActorOf(http), id, versionId, http.RequestAborted)));

        device.MapPost(DriveContract.Uploads, async (HttpContext http, AppDbContext db, DriveUploadRequest? request) =>
            Answer(await DriveFiles.StartUploadAsync(db, ActorOf(http), request, http.RequestAborted), StatusCodes.Status201Created));
        device.MapGet(DriveContract.Uploads + "/{token:guid}", async (HttpContext http, AppDbContext db, Guid token) =>
            Answer(await DriveFiles.UploadStateAsync(db, ActorOf(http), token, http.RequestAborted)));
        device.MapPut(DriveContract.Uploads + "/{token:guid}/chunk", async (HttpContext http, AppDbContext db, DriveStore store, Guid token, long? offset) =>
            Answer(await DriveFiles.AppendAsync(db, store, ActorOf(http), token, offset ?? 0, http.Request.Body, http.Request.ContentLength, http.RequestAborted)));
        device.MapPost(DriveContract.Uploads + "/{token:guid}/finish", async (HttpContext http, AppDbContext db, DriveStore store, Guid token) =>
            Answer(await DriveFiles.FinishUploadAsync(db, store, ActorOf(http), token, http.RequestAborted), StatusCodes.Status200OK));
        device.MapDelete(DriveContract.Uploads + "/{token:guid}", async (HttpContext http, AppDbContext db, DriveStore store, Guid token) =>
            await DriveFiles.CancelUploadAsync(db, store, ActorOf(http), token, http.RequestAborted) ? Results.NoContent() : NotFound());
    }

    /// <summary>What the pages fetch: the live list, downloads (always as attachment: nothing of a file is shown as a page of MatCloud) and the uploads.</summary>
    private static void MapLive(IEndpointRouteBuilder app)
    {
        var live = app.MapGroup("/live/drive").RequireAuthorization().DisableAntiforgery();

        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await DriveItems.WaitAsync(db, me.Actor, since ?? 0, WaitTime(timeout), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (NotAllowedException)
            {
                return Results.NotFound();
            }
        });

        live.MapGet("/{id:long}/download", async (HttpContext http, AppDbContext db, DriveStore store, CurrentUser me, long id, long? version) =>
            await GuardAsync(async () => Content(await DriveFiles.OpenAsync(db, store, me.Actor, id, version, ct: http.RequestAborted), download: true)));

        live.MapPost("/uploads", async (HttpContext http, AppDbContext db, CurrentUser me, IAntiforgery antiforgery, DriveUploadRequest? request) =>
            !await antiforgery.IsRequestValidAsync(http)
                ? Results.BadRequest()
                : await GuardAsync(async () => Answer(await DriveFiles.StartUploadAsync(db, me.Actor, request, http.RequestAborted), StatusCodes.Status201Created)));
        live.MapPut("/uploads/{token:guid}/chunk", async (HttpContext http, AppDbContext db, DriveStore store, CurrentUser me, IAntiforgery antiforgery, Guid token, long? offset) =>
            !await antiforgery.IsRequestValidAsync(http)
                ? Results.BadRequest()
                : await GuardAsync(async () => Answer(await DriveFiles.AppendAsync(db, store, me.Actor, token, offset ?? 0, http.Request.Body, http.Request.ContentLength, http.RequestAborted))));
        live.MapPost("/uploads/{token:guid}/finish", async (HttpContext http, AppDbContext db, DriveStore store, CurrentUser me, IAntiforgery antiforgery, Guid token) =>
            !await antiforgery.IsRequestValidAsync(http)
                ? Results.BadRequest()
                : await GuardAsync(async () => Answer(await DriveFiles.FinishUploadAsync(db, store, me.Actor, token, http.RequestAborted))));
        live.MapDelete("/uploads/{token:guid}", async (HttpContext http, AppDbContext db, DriveStore store, CurrentUser me, IAntiforgery antiforgery, Guid token) =>
            !await antiforgery.IsRequestValidAsync(http)
                ? Results.BadRequest()
                : await GuardAsync(async () => await DriveFiles.CancelUploadAsync(db, store, me.Actor, token, http.RequestAborted) ? Results.NoContent() : Results.NotFound()));
    }

    /// <summary>The answer of an outcome of the Drive: the value (200 or <paramref name="success"/>), or the error with its status (<see cref="DriveContract"/>).</summary>
    public static IResult Answer<T>(DriveOutcome<T> outcome, int success = StatusCodes.Status200OK)
    {
        if (outcome.Ok)
        {
            return Results.Json(outcome.Value, CloudContract.Json, statusCode: success);
        }

        return outcome.Failure switch
        {
            DriveFailure.NotFound => Error(StatusCodes.Status404NotFound, outcome.Error!, outcome.Field),
            DriveFailure.NameTaken or DriveFailure.Conflict => Conflict(StatusCodes.Status409Conflict, outcome),
            DriveFailure.Changed => Conflict(StatusCodes.Status412PreconditionFailed, outcome),
            DriveFailure.IfMatchMissing => Error(StatusCodes.Status428PreconditionRequired, outcome.Error!, outcome.Field),
            DriveFailure.TooLarge => Error(StatusCodes.Status413PayloadTooLarge, outcome.Error!, outcome.Field),
            DriveFailure.QuotaFull => Error(StatusCodes.Status507InsufficientStorage, outcome.Error!, outcome.Field),
            _ => Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field),
        };
    }

    private static IResult Conflict<T>(int status, DriveOutcome<T> outcome)
        => Results.Json(new DriveConflict(outcome.Error!, outcome.Field, outcome.Current), CloudContract.Json, statusCode: status);

    private static IResult NoContent<T>(DriveOutcome<T> outcome) => outcome.Ok ? Results.NoContent() : Answer(outcome);

    private static IResult NotFound() => Error(StatusCodes.Status404NotFound, "Diesen Upload gibt es nicht (mehr).");

    /// <summary>The bytes of a file, streamed from the store: ETag, Last-Modified, ranges and If-None-Match by the framework.</summary>
    private static IResult Content(DriveOutcome<DriveContent> outcome, bool download)
    {
        if (!outcome.Ok)
        {
            return Answer(outcome);
        }

        var file = outcome.Value!;
        return Results.File(file.PhysicalPath, download ? "application/octet-stream" : file.ContentType, download ? file.Name : null, file.LastModified,
            EntityTagHeaderValue.Parse(file.ETag), enableRangeProcessing: true);
    }

    /// <summary>A route of the pages: a browser of an account whose Drive is switched off gets 404, not an error page.</summary>
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NotAllowedException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>A route that takes a whole file raises the body limit of its request (the server's limit is one piece of an upload).</summary>
    private static void AllowBody(HttpContext http, long bytes)
    {
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = bytes + 64 * 1024;
        }
    }

    private static DriveQuery Query(string? sort, bool? descending, int? offset, int? limit)
        => new(Enum.TryParse<DriveSort>(sort, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : DriveSort.Name, descending ?? false, null, offset ?? 0,
            limit ?? 200);

    private static DriveNameConflict ConflictOf(string? value)
        => Enum.TryParse<DriveNameConflict>(value, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : DriveNameConflict.Fail;
}
