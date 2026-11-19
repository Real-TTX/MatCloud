using MatCloud.Core.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Modules;

/// <summary>What an app keeps in MatCloud for one account: how many items, how much, when it changed last.</summary>
public sealed record AppUsage(long Items, long Bytes, DateTime? LastChange)
{
    public static readonly AppUsage None = new(0, 0, null);
}

/// <summary>
/// An app module of MatCloud: an app of the contract (<c>MatCloud.Contract.CloudApps</c>) with its own tables, its own API for its devices and its own
/// pages in the browser (ThixClip: the clipboard history). A module is a project under <c>src/Apps</c>, referenced by the host and listed in its
/// <c>Modules.cs</c>; the host installs it once at start-up (<see cref="CloudModules.Install"/>). An app without a module keeps its data in the general
/// sync and needs no code here.
///
/// The rules of the platform hold for a module too: its business layer is static classes that take the <see cref="AppDbContext"/> and the actor and
/// check what the actor may do; its tables follow the relational rules; its device routes start with <c>/api/v1/&lt;app id&gt;</c> and are guarded by
/// <see cref="Api.DeviceEndpoints.ForApp"/> (only a device of this app, only while the app is on); its pages start with <see cref="Home"/>.
/// </summary>
public interface ICloudModule
{
    /// <summary>The app (<c>CloudApps.Clip</c>).</summary>
    string AppId { get; }

    /// <summary>The address of its pages in the browser (<c>/Clip</c>).</summary>
    string Home { get; }

    /// <summary>An icon of the sprite for its menu and its tile.</summary>
    string Icon { get; }

    /// <summary>The title of its menu ("Zwischenablage").</summary>
    string MenuTitle { get; }

    /// <summary>The partial (in the module's <c>Pages/Shared</c>) that draws the entries of its menu.</summary>
    string MenuPartial { get; }

    /// <summary>The largest body of a request the module takes (pictures), or null for the default.</summary>
    long? MaxRequestBodyBytes { get; }

    /// <summary>
    /// True for a module that is a web client of the general sync (ThixNote): its devices keep using <c>/api/v1/sync</c>, and its pages read and write the
    /// same items. False (the default) for a module with an API of its own (ThixClip): its app has no general sync.
    /// </summary>
    bool UsesGeneralSync => false;

    /// <summary>
    /// The grant of the module to other apps: the apps (ids of the contract) whose devices may call its device API too and reach its data of THEIR
    /// account (the Drive lets ThixWrite, ThixCalc and ThixShow open and save documents). Empty (the default): only a device of <see cref="AppId"/>.
    /// The grant follows the switch of the module: while its app is off, the guests get 403 as well. The business layer checks it with
    /// <see cref="Business.Actor.MayUse"/>, the routes with <see cref="Api.DeviceEndpoints.ForApp"/>.
    /// </summary>
    IReadOnlyList<string> GuestApps => [];

    /// <summary>Its services (most modules need none: the business layer is static).</summary>
    void AddServices(IServiceCollection services);

    /// <summary>Its tables and sequences in the one database (the host's migrations contain them).</summary>
    void ConfigureModel(ModelBuilder model);

    /// <summary>Its routes: the device API (<c>/api/v1/&lt;app&gt;/...</c>) and what its pages fetch (<c>/live/&lt;app&gt;/...</c>).</summary>
    void MapEndpoints(IEndpointRouteBuilder routes);

    /// <summary>The hourly housekeeping (retention ...): how many things it removed.</summary>
    Task<int> MaintainAsync(AppDbContext db, CancellationToken ct);

    /// <summary>Removes everything the account keeps in this app, for good (the account is deleted, or the person deletes the app's data).</summary>
    Task DeleteDataAsync(AppDbContext db, long userId, CancellationToken ct);

    /// <summary>What the account keeps in this app.</summary>
    Task<AppUsage> UsageAsync(AppDbContext db, long userId, CancellationToken ct);

    /// <summary>What all accounts keep in this app (the overview of the administration).</summary>
    Task<AppUsage> TotalUsageAsync(AppDbContext db, CancellationToken ct);
}

/// <summary>The modules of this installation. The host installs them once, before the database is used (also in the design-time factory and in tests).</summary>
public static class CloudModules
{
    private static IReadOnlyList<ICloudModule> _all = [];

    public static IReadOnlyList<ICloudModule> All => _all;

    /// <summary>Installs the modules (the same set again changes nothing; the model of the database is built from them).</summary>
    public static void Install(params ICloudModule[] modules)
    {
        if (_all.Count > 0)
        {
            if (_all.Select(m => m.AppId).SequenceEqual(modules.Select(m => m.AppId)))
            {
                return;
            }

            throw new InvalidOperationException("The modules of MatCloud are installed already, with another set.");
        }

        _all = modules;
    }

    public static ICloudModule? Find(string? appId) => _all.FirstOrDefault(m => m.AppId == appId);

    /// <summary>True when the app has a module (pages in the browser); false: it keeps its data in the general sync and has no pages.</summary>
    public static bool HasModule(string? appId) => Find(appId) is not null;

    /// <summary>True when the app's devices use the general sync: no module, or a module that is a web client of it (ThixNote).</summary>
    public static bool UsesGeneralSync(string? appId) => Find(appId) is not { UsesGeneralSync: false };

    /// <summary>True when the module of <paramref name="appId"/> lets the devices of <paramref name="guestApp"/> in (<see cref="ICloudModule.GuestApps"/>).</summary>
    public static bool IsGuest(string appId, string? guestApp)
        => guestApp is not null && Find(appId) is { } module && module.GuestApps.Contains(guestApp, StringComparer.Ordinal);
}
