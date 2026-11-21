using System.Globalization;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;

namespace MatCloud.Core.Api;

/// <summary>
/// The API of the devices (<see cref="CloudContract"/>): the sign-in (password or browser), the account and the general sync (<see cref="SyncContract"/>).
/// Every route but the sign-in needs the device token as a bearer token. Answers are JSON (camelCase, enums as text); an error is an
/// <see cref="ApiError"/> with a German message. Every answer carries the protocol version (<see cref="CloudContract.ProtocolHeader"/>); a device that
/// speaks a newer protocol than the server gets 400. A module maps its own device routes with <see cref="ForApp"/>.
/// </summary>
public static class DeviceEndpoints
{
    public const string DeviceItem = "MatCloud.Device";

    /// <summary>Where the version of the server comes from (the host sets it at start-up).</summary>
    public static string ServerVersion { get; set; } = "local";

    public static void MapDeviceApi(this IEndpointRouteBuilder app)
    {
        var api = Base(app);
        api.MapPost(CloudContract.DeviceLogin, LoginAsync).RequireRateLimiting("signin");
        api.MapPost(CloudContract.DeviceLink, StartLinkAsync).RequireRateLimiting("signin");
        api.MapPost(CloudContract.DeviceLinkPoll, async (HttpContext http, AppDbContext db, DeviceLinkPollRequest? request) =>
            Json(await DeviceLinkService.PollAsync(db, request?.PollToken, ServerVersion, http.RequestAborted)));

        var device = api.MapGroup(string.Empty).AddEndpointFilter(DeviceAsync);
        device.MapPost(CloudContract.DeviceLogout, async (HttpContext http, AppDbContext db) =>
        {
            await DeviceService.LogoutAsync(db, ActorOf(http));
            return Results.NoContent();
        });
        device.MapPost(CloudContract.DeviceHandoff, async (HttpContext http, AppDbContext db, DeviceHandoffRequest? request) =>
            Answer(await DeviceLinkService.HandOffAsync(db, ActorOf(http), request, ClientAddress(http), http.RequestAborted)));
        device.MapGet(CloudContract.Account,async (HttpContext http, AppDbContext db) => Answer(await AccountService.InfoAsync(db, ActorOf(http), ServerVersion)));

        // The general sync: only the apps without a module of their own (a module has its own routes).
        var sync = device.MapGroup(string.Empty).AddEndpointFilter(GeneralSyncAsync);
        // ?app= names another app's data (a grant of its module: ThixMail in the contacts and the calendar); the business layer checks the grant.
        sync.MapGet(SyncContract.Changes, async (HttpContext http, AppDbContext db, long? since, int? limit) =>
        {
            var actor = ActorOf(http);
            return Json(await SyncService.ChangesAsync(db, actor, TargetApp(http), since ?? 0, limit ?? SyncService.DefaultLimit, http.RequestAborted));
        });
        sync.MapGet(SyncContract.Wait, async (HttpContext http, AppDbContext db, long? since, int? timeout) =>
        {
            var actor = ActorOf(http);
            var app = TargetApp(http);
            return await WaitAsync(http, () => SyncService.WaitAsync(db, actor, app, since ?? 0, WaitTime(timeout), http.RequestAborted));
        });
        sync.MapPost(SyncContract.Push, async (HttpContext http, AppDbContext db, SyncPushRequest? request) =>
        {
            if (SyncService.Validate(request) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem.Message, problem.Field);
            }

            var actor = ActorOf(http);
            return Json(await SyncService.PushAsync(db, actor, TargetApp(http), request!, http.RequestAborted));
        });
    }

    /// <summary>The app whose general sync a request reaches: <c>?app=</c> (<see cref="SyncContract.AppParameter"/>), else the device's own.</summary>
    private static string TargetApp(HttpContext http)
    {
        var asked = http.Request.Query[SyncContract.AppParameter].ToString();
        return asked.Length == 0 ? ActorOf(http).App! : asked;
    }

    /// <summary>
    /// The group for the device routes of a module: the protocol header, a valid device token, a device of exactly this app – or of an app the module
    /// lets in (<see cref="ICloudModule.GuestApps"/>) – and the app switched on (else 401 / 403 with a German message).
    /// </summary>
    public static RouteGroupBuilder ForApp(IEndpointRouteBuilder app, string appId)
        => Base(app).MapGroup(string.Empty).AddEndpointFilter(DeviceAsync).AddEndpointFilter((context, next) => RequireAppAsync(context, next, appId));

    public static IResult Error(int status, string message, string? field = null) => Results.Json(new ApiError(message, field), CloudContract.Json, statusCode: status);

    public static IResult Json<T>(T value) => Results.Json(value, CloudContract.Json);

    /// <summary>The answer of an outcome: the value, 404, or 400 with the message at its field.</summary>
    public static IResult Answer<T>(Outcome<T> outcome)
    {
        if (outcome.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Nicht gefunden.");
        }

        return outcome.Ok ? Json(outcome.Value) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
    }

    /// <summary>The device of the request (after the filter of the group).</summary>
    public static Actor ActorOf(HttpContext http) => ((DeviceIdentity)http.Items[DeviceItem]!).Actor;

    /// <summary>The wait of a long poll: the asked one, between 1 and <see cref="CloudContract.MaxWaitSeconds"/> seconds (25 without one).</summary>
    public static TimeSpan WaitTime(int? timeout) => TimeSpan.FromSeconds(Math.Clamp(timeout ?? 25, 1, CloudContract.MaxWaitSeconds));

    /// <summary>A long poll: <c>{ latest }</c>, or nothing when the device went away.</summary>
    public static async Task<IResult> WaitAsync(HttpContext http, Func<Task<long>> wait)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            return Json(new WaitResult(await wait()));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
    }

    /// <summary>The body of a request, at most <paramref name="limit"/> bytes; null when it is longer.</summary>
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The address of the caller as people read it: an IPv4 address without the <c>::ffff:</c> of a dual-stack socket.</summary>
    public static string? ClientAddress(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();
    }

    /// <summary>The address of this MatCloud for a browser: the public address of the installation, else the address of the request.</summary>
    public static async Task<string> BaseUrlAsync(HttpContext http, AppDbContext db)
    {
        var platform = http.RequestServices.GetService<MatPlatform.MatPlatformSettings>();
        return platform?.AppPublicUrl?.TrimEnd('/')
               ?? await SystemService.PublicUrlAsync(db, http.RequestAborted)
               ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
    }

    private static RouteGroupBuilder Base(IEndpointRouteBuilder app)
        => app.MapGroup(string.Empty).AllowAnonymous().DisableAntiforgery().AddEndpointFilter(ProtocolAsync);

    private static async Task<IResult> LoginAsync(HttpContext http, AppDbContext db, DeviceLoginRequest? request)
    {
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Die Anmeldung braucht Benutzername, Passwort, Gerätename und App.");
        }

        var outcome = await DeviceService.LoginAsync(db, request, ClientAddress(http), ServerVersion, http.RequestAborted);
        if (outcome.Ok)
        {
            return Json(outcome.Value!);
        }

        // A wrong name or password is 401; a switched-off app 403; a missing device name is a request the app has to fix (400).
        var status = outcome.Field switch
        {
            "password" => StatusCodes.Status401Unauthorized,
            "app" when CloudApps.IsKnown(request.App) => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, outcome.Error!, outcome.Field);
    }

    private static async Task<IResult> StartLinkAsync(HttpContext http, AppDbContext db, DeviceLinkRequest? request)
    {
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Die Anmeldung braucht Gerätename, Plattform und App.");
        }

        var outcome = await DeviceLinkService.StartAsync(db, request, ClientAddress(http), await BaseUrlAsync(http, db), http.RequestAborted);
        if (outcome.Ok)
        {
            return Json(outcome.Value!);
        }

        var status = outcome.Field == "app" && CloudApps.IsKnown(request.App) ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
        return Error(status, outcome.Error!, outcome.Field);
    }

    /// <summary>Every answer names the protocol of the server; a device with a newer protocol is told to wait for a server update.</summary>
    private static async ValueTask<object?> ProtocolAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers[CloudContract.ProtocolHeader] = CloudContract.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        var asked = http.Request.Headers[CloudContract.ProtocolHeader].ToString();
        if (int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version > CloudContract.ProtocolVersion)
        {
            return Error(StatusCodes.Status400BadRequest, "Diese App ist neuer als die MatCloud. Bitte die MatCloud aktualisieren.");
        }

        try
        {
            return await next(context);
        }
        catch (NotAllowedException exception)
        {
            return Error(StatusCodes.Status403Forbidden, exception.Message);
        }
    }

    /// <summary>The device of the bearer token, or 401; a device of a switched-off app gets 403.</summary>
    private static async ValueTask<object?> DeviceAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var device = await DeviceService.AuthenticateAsync(db, token, http.RequestAborted);
        if (device is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(StatusCodes.Status401Unauthorized, "Dieses Gerät ist nicht (mehr) angemeldet. Bitte in der App erneut anmelden.");
        }

        if (!await AppService.IsEnabledAsync(db, device.App, http.RequestAborted))
        {
            return Error(StatusCodes.Status403Forbidden, AppService.SwitchedOffText(device.App));
        }

        http.Items[DeviceItem] = device;
        http.Items[AppDbContext.ActorItem] = device.UserId;
        await DeviceService.TouchAsync(db, device, ClientAddress(http), AppVersionOf(http.Request), http.RequestAborted);
        return await next(context);
    }

    /// <summary>
    /// The routes of a module are for the devices of its app, and for the devices of the apps it lets in while the module's app is on (the filter of the
    /// device checked the device's own app already).
    /// </summary>
    private static async ValueTask<object?> RequireAppAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next, string appId)
    {
        var http = context.HttpContext;
        var device = (DeviceIdentity)http.Items[DeviceItem]!;
        if (device.App == appId)
        {
            return await next(context);
        }

        var reading = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (!(reading ? CloudModules.IsReadingGuest(appId, device.App) : CloudModules.IsGuest(appId, device.App)))
        {
            return Error(StatusCodes.Status403Forbidden, $"Diese Daten gehören zu {CloudApps.NameOf(appId)}; dieses Gerät ist für {CloudApps.NameOf(device.App)} angemeldet.");
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return await AppService.IsEnabledAsync(db, appId, http.RequestAborted)
            ? await next(context)
            : Error(StatusCodes.Status403Forbidden, AppService.SwitchedOffText(appId));
    }

    /// <summary>
    /// An app with a module of its own has no data in the general sync. A request for another app's data (<c>?app=</c>) needs that app known, granted to
    /// the device's app (read: <see cref="ICloudModule.ReadingGuestApps"/> too; push: <see cref="ICloudModule.GuestApps"/> only) and switched on.
    /// </summary>
    private static async ValueTask<object?> GeneralSyncAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var device = (DeviceIdentity)http.Items[DeviceItem]!;
        var app = TargetApp(http);
        if (!CloudModules.UsesGeneralSync(app) || !CloudApps.IsKnown(app))
        {
            return Error(StatusCodes.Status403Forbidden, $"{CloudApps.NameOf(app)} hat in der MatCloud eine eigene Schnittstelle.");
        }

        if (app == device.App)
        {
            return await next(context);
        }

        var reading = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (!(reading ? CloudModules.IsReadingGuest(app, device.App) : CloudModules.IsGuest(app, device.App)))
        {
            return Error(StatusCodes.Status403Forbidden, $"Diese Daten gehören zu {CloudApps.NameOf(app)}; dieses Gerät ist für {CloudApps.NameOf(device.App)} angemeldet.");
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return await AppService.IsEnabledAsync(db, app, http.RequestAborted)
            ? await next(context)
            : Error(StatusCodes.Status403Forbidden, AppService.SwitchedOffText(app));
    }

    /// <summary>The version of the app from its user agent ("ThixNote/1.1.42 (windows)").</summary>
    private static string? AppVersionOf(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        var slash = agent.IndexOf('/');
        if (slash <= 0 || !agent.StartsWith("Thix", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = agent[(slash + 1)..];
        var end = version.IndexOf(' ');
        return end < 0 ? version : version[..end];
    }
}
