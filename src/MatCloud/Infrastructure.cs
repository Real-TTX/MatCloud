using System.Text.RegularExpressions;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using MatPlatform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatCloud;

/// <summary>Docker <c>HEALTHCHECK</c> without curl: the app asks its own <c>/api/health</c> (the platform contract) and answers with an exit code.</summary>
public static partial class HealthCheck
{
    [GeneratedRegex(@":(\d+)")]
    private static partial Regex PortRegex();

    public static async Task<int> RunAsync()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var match = PortRegex().Match(urls ?? string.Empty);
        var port = match.Success ? match.Groups[1].Value : "4950";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"http://localhost:{port}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

public static class Database
{
    /// <summary>Creates and updates the tables. The database container may still be starting: try again for a while before giving up.</summary>
    public static async Task MigrateWithRetryAsync(WebApplication app)
    {
        const int attempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                app.Logger.LogInformation("The database is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                app.Logger.LogWarning("The database is not ready yet (attempt {Attempt}/{Attempts}): {Message}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    /// <summary>
    /// A fresh installation that names <c>MATCLOUD_ADMIN_PASSWORD</c> (Mattiverse generates one) gets its administrator at once (user name
    /// <c>MATCLOUD_ADMIN_USER</c>, default <c>admin</c>), so nothing is set up after the install. Without the variable the setup assistant does it;
    /// once there is an account the variable is ignored.
    /// </summary>
    public static async Task SeedAdminAsync(WebApplication app)
    {
        var password = app.Configuration["MATCLOUD_ADMIN_PASSWORD"];
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await UserService.NeedsSetupAsync(db))
        {
            return;
        }

        var userName = app.Configuration["MATCLOUD_ADMIN_USER"] is { Length: > 0 } name ? name : "admin";
        var outcome = await UserService.SetupAsync(db, userName, "Administrator", password, password);
        if (outcome.Ok)
        {
            app.Logger.LogInformation("The administrator {UserName} was created from MATCLOUD_ADMIN_PASSWORD.", outcome.Value!.UserName);
        }
        else
        {
            app.Logger.LogWarning("MATCLOUD_ADMIN_PASSWORD was not used: {Problem}", outcome.Error);
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration; the modules are installed as at start-up.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        InstalledModules.Install();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDatabase.Use(options, "Host=localhost;Port=4952;Database=matcloud;Username=matcloud;Password=matcloud");
        return new(options.Options);
    }
}

/// <summary>Whether the app has an account yet. Without one, every page leads to the setup assistant (not with MatIdentity: it makes the first account).</summary>
public sealed class SetupState
{
    private volatile bool _done;

    public async Task<bool> NeedsSetupAsync(AppDbContext db)
    {
        if (_done)
        {
            return false;
        }

        var needs = await UserService.NeedsSetupAsync(db);
        _done = !needs;
        return needs;
    }

    public void Done() => _done = true;
}

/// <summary>On a fresh database the first visit goes to the setup assistant, which creates the first administrator.</summary>
public sealed class SetupRedirect(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SetupState state, AppDbContext db, MatPlatformSettings platform)
    {
        var path = context.Request.Path;
        if (!IsOpen(path) && !(platform.IdentityEnabled && !path.StartsWithSegments("/Account/Setup")) && await state.NeedsSetupAsync(db))
        {
            context.Response.Redirect("/Account/Setup");
            return;
        }

        await next(context);
    }

    private static bool IsOpen(PathString path)
        => path.StartsWithSegments("/Account/Setup") || path.StartsWithSegments("/health") || path.StartsWithSegments("/Error") || path.StartsWithSegments("/api")
           || path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/signout-callback-oidc") || path.StartsWithSegments("/signout-backchannel")
           || StaticFiles.IsStatic(path);
}

/// <summary>A person whose password was set by an administrator chooses their own before anything else.</summary>
public sealed class ForcePasswordChange(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser current)
    {
        var path = context.Request.Path;
        if (current.Snapshot is { MustChangePassword: true }
            && !path.StartsWithSegments("/Account") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/api") && !StaticFiles.IsStatic(path))
        {
            context.Response.Redirect("/Account/Security?force=1");
            return;
        }

        await next(context);
    }
}

/// <summary>
/// Headers every response carries: no inline script or style (the toolkit needs none), nothing from another site, no guessing of types.
/// <c>MAT_FRAME_ANCESTORS</c> (MatPlatform) replaces <c>frame-ancestors</c> when Mattiverse shows MatCloud in its window.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    private const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; " +
        "frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = Policy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), clipboard-write=(self)";
        return next(context);
    }
}

/// <summary>The files of wwwroot (the toolkit, styles, scripts, icons, the PWA files) and of the modules (<c>/_content</c>).</summary>
public static class StaticFiles
{
    public static bool IsStatic(PathString path)
        => path.StartsWithSegments("/matui") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/img")
           || path.StartsWithSegments("/_content")
           || path == "/favicon.ico" || path == "/manifest.webmanifest" || path == "/sw.js" || path == "/offline.html" || path == "/robots.txt" || path == "/llms.txt";
}

/// <summary>
/// The housekeeping that runs by itself every hour: ended sessions and old sign-ins of devices go, old tombstones of the general sync go, and every module
/// does its own (the retention of the clips removes what the account no longer keeps, as tombstones, so the devices remove it too).
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The housekeeping failed; it tries again later.");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (sessions, links, tombstones, modules) = await SystemService.CleanUpAsync(db, ct);
        if (sessions + links + tombstones + modules > 0)
        {
            logger.LogInformation("Housekeeping: {Sessions} sessions, {Links} device sign-ins, {Tombstones} old tombstones and {Modules} expired entries removed.",
                sessions, links, tombstones, modules);
        }
    }
}

/// <summary>A text that is posted empty is an empty string, not <c>null</c>: the properties of the page models are not nullable.</summary>
public sealed class EmptyStringBinderProvider : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinderProvider
{
    public Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder? GetBinder(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBinderProviderContext context)
        => context.Metadata.ModelType == typeof(string) ? new EmptyStringBinder() : null;

    private sealed class EmptyStringBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (result == Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
            bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(result.FirstValue ?? string.Empty);
            return Task.CompletedTask;
        }
    }
}
