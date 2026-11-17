using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MatCloud;
using MatCloud.Contract;
using MatCloud.Core.Api;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using MatCloud.Core.Web;
using MatCloud.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

// The app modules first: the model of the database is built from them.
InstalledModules.Install();

var builder = WebApplication.CreateBuilder(args);

// ---- the database (PostgreSQL): the default fits the compose files, so the container needs no variable; MATCLOUD_DB overrides it ----
var connection = Environment.GetEnvironmentVariable("MATCLOUD_DB")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? "Host=db;Port=5432;Database=matcloud;Username=matcloud;Password=matcloud";
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AppDbContext>(o => AppDatabase.Use(o, connection));

// The keys that protect the cookie (and the bindings of MatPlatform) are in the volume: a session survives a restart of the container.
var dataDirectory = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "/data", builder.Environment.ContentRootPath);
var keys = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("MatCloud");

builder.Services.AddMemoryCache();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddSingleton<Assets>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHostedService<MaintenanceService>();
foreach (var module in CloudModules.All)
{
    module.AddServices(builder.Services);
}

// The JSON of the API is the one of the contract (camelCase, enums as text, no nulls).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// The Mat platform contract (MatDesign docs/PLATFORM.md, contract 2): /api, /api/health, /api/state, /api/configure, /api/actions with MAT_API_KEY or a
// token of MatIdentity, the Connections area and the contributions to a shell. MatCloudPlatform describes MatCloud and supplies the values.
var publicUrl = MatPlatformSettings.From(builder.Configuration, MatCloudPlatform.AppId).AppPublicUrl;
var platform = builder.AddMatPlatform<MatCloudPlatform>(o => MatCloudPlatform.Describe(o, publicUrl), o =>
{
    o.DataDirectory = dataDirectory;
    o.ConnectionsAccess = http => Task.FromResult(http.User.IsInRole("Admin"));
    o.UiLanguage = _ => "de";
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = CookieSignIn.CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.IsEssential = true;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Error?code=403";
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = CookieSignIn.ValidateAsync;
        o.Events.OnRedirectToLogin = context =>
        {
            // a script or an API call gets a status, not the sign-in page
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/live")
                || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    })

    // Sign-in through MatIdentity, only when MATIDENTITY_AUTHORITY is set (nothing changes without it); with it also the back-channel logout.
    .AddMatIdentitySignIn<MatCloudIdentityUsers>(platform, o =>
    {
        o.CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login?local=1&denied=1";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
}).AddMvcOptions(o =>
{
    o.ModelBinderProviders.Insert(0, new EmptyStringBinderProvider());
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

// The sign-in (browser and devices): 10 attempts a minute per address. An API caller gets the error as JSON.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("signin", http => HttpMethods.IsGet(http.Request.Method)
        ? RateLimitPartition.GetNoLimiter("show")
        : RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            await DeviceEndpoints.Error(StatusCodes.Status429TooManyRequests, "Zu viele Anmeldeversuche. Bitte in einer Minute noch einmal versuchen.").ExecuteAsync(context.HttpContext);
        }
    };
});

// The largest body: what the modules take (pictures of the clipboard), at least a full push of the general sync.
var bodyLimit = Math.Max(SyncContract.MaxPushBytes, CloudModules.All.Max(m => m.MaxRequestBodyBytes) ?? 0);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = bodyLimit);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = bodyLimit);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // behind a reverse proxy (Matcad, Caddy, nginx, Traefik) the address and the scheme of the visitor are in the headers
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await Database.MigrateWithRetryAsync(app);
await Database.SeedAdminAsync(app);
DeviceEndpoints.ServerVersion = AppInfo.Version;

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/live"),
    branch => branch.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path;
        if (path == "/sw.js" || path == "/manifest.webmanifest")
        {
            // the service worker and the manifest are always asked again, so a new version is used at once
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
        else if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            // files are versioned with ?v= (asp-append-version): they can be kept for a long time
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
    },
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SetupRedirect>();
app.UseMiddleware<ForcePasswordChange>();
app.UseAuthorization();

app.MapGet("/health", async (AppDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();
app.MapDeviceApi();
foreach (var module in CloudModules.All)
{
    module.MapEndpoints(app);
}

AppManifests.Map(app);

app.MapMatPlatform();
app.MapRazorPages();

app.Run();
return 0;

public partial class Program;
