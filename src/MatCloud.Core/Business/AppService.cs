using MatCloud.Contract;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>An app as the pages show it: on or off, with a module (pages and an API of its own) or in the general sync.</summary>
public sealed record AppView(string Id, string Name, string Description, bool IsEnabled, bool HasModule, string Icon, string? Home)
{
    /// <summary>"Web-App und Sync" or "Sync".</summary>
    public string KindText => HasModule ? "Web-App und Sync" : "Sync";
}

/// <summary>An app of the signed-in person: their devices of it and what it keeps.</summary>
public sealed record MyAppView(AppView App, int Devices, AppUsage Usage);

/// <summary>An app in the administration: on or off, and what all accounts keep in it.</summary>
public sealed record AdminAppView(AppView App, int Devices, int Accounts, AppUsage Usage);

/// <summary>
/// The apps of this installation (<see cref="CloudApps"/>): which are switched on (an administrator decides; on by default), what an account keeps in
/// each and the removal of an app's data. An app with a module asks the module; every other app is the general sync (<see cref="SyncService"/>).
/// </summary>
public static class AppService
{
    private static readonly Lock Gate = new();
    // per database (its connection string): one in the app, one per test in the tests that run side by side
    private static readonly Dictionary<string, Dictionary<string, bool>> Enabled = [];
    private static int _generation;

    public static string SwitchedOffText(string app) => $"{CloudApps.NameOf(app)} ist in dieser MatCloud ausgeschaltet. Bitte an die Administration wenden.";

    public static AppView View(CloudApp app, bool enabled)
    {
        var module = CloudModules.Find(app.Id);
        return new AppView(app.Id, app.Name, app.Description, enabled, module is not null, module?.Icon ?? "refresh", module?.Home);
    }

    /// <summary>Whether an app is switched on (unknown apps never are). Read once and kept until an administrator changes it.</summary>
    public static async Task<bool> IsEnabledAsync(AppDbContext db, string app, CancellationToken ct = default)
        => CloudApps.IsKnown(app) && (await EnabledAsync(db, ct)).GetValueOrDefault(app, true);

    /// <summary>The apps that are switched on, in the order of the contract.</summary>
    public static async Task<IReadOnlyList<AppView>> EnabledAppsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var enabled = await EnabledAsync(db, ct);
        return CloudApps.All.Where(a => enabled.GetValueOrDefault(a.Id, true)).Select(a => View(a, true)).ToList();
    }

    /// <summary>The apps of the signed-in person: every app that is on, with their devices and what it keeps for them.</summary>
    public static async Task<IReadOnlyList<MyAppView>> MineAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.UserId == actor.UserId)
            .GroupBy(d => d.App).Select(g => new { App = g.Key, Count = g.Count() }).ToListAsync(ct);
        var result = new List<MyAppView>();
        foreach (var app in await EnabledAppsAsync(db, ct))
        {
            result.Add(new MyAppView(app, devices.FirstOrDefault(d => d.App == app.Id)?.Count ?? 0, await UsageAsync(db, actor.UserId, app.Id, ct)));
        }

        return result;
    }

    /// <summary>One app of the signed-in person (missing for an unknown or switched-off app).</summary>
    public static async Task<Outcome<MyAppView>> MineAsync(AppDbContext db, Actor actor, string app, CancellationToken ct = default)
    {
        var found = (await MineAsync(db, actor, ct)).FirstOrDefault(a => a.App.Id == app);
        return found is null ? Outcome<MyAppView>.Missing() : Outcome<MyAppView>.Success(found);
    }

    /// <summary>What an account keeps in an app.</summary>
    public static async Task<AppUsage> UsageAsync(AppDbContext db, long userId, string app, CancellationToken ct = default)
    {
        if (CloudModules.Find(app) is { } module)
        {
            return await module.UsageAsync(db, userId, ct);
        }

        return await SyncService.UsageAsync(db, userId, app, ct);
    }

    /// <summary>
    /// The person deletes what an app keeps in the cloud for them: the data goes for good, and the devices of the app are signed out (they would
    /// otherwise send it again). The data on the devices stays.
    /// </summary>
    public static async Task<Outcome> DeleteMyDataAsync(AppDbContext db, Actor actor, string app, CancellationToken ct = default)
    {
        if (actor.DeviceId is not null || !CloudApps.IsKnown(app))
        {
            return Outcome.Missing();
        }

        await DeviceService.RemoveAllAsync(db, actor.UserId, app);
        await DeleteDataAsync(db, actor.UserId, app, ct);
        return Outcome.Success();
    }

    /// <summary>Removes what an account keeps in an app, for good (no tombstones: nobody is left to tell).</summary>
    internal static async Task DeleteDataAsync(AppDbContext db, long userId, string app, CancellationToken ct)
    {
        if (CloudModules.Find(app) is { } module)
        {
            await module.DeleteDataAsync(db, userId, ct);
        }
        else
        {
            await db.SyncItems.Where(i => i.UserId == userId && i.App == app).ExecuteDeleteAsync(ct);
        }

        RevisionSignals.Notify(Revisions.Stream(app, userId));
    }

    // ---- the administration ----

    /// <summary>Every app with its state and what all accounts keep in it.</summary>
    public static async Task<IReadOnlyList<AdminAppView>> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var enabled = await EnabledAsync(db, ct);
        var devices = await db.Devices.AsNoTracking()
            .GroupBy(d => d.App).Select(g => new { App = g.Key, Count = g.Count(), Accounts = g.Select(d => d.UserId).Distinct().Count() }).ToListAsync(ct);
        var result = new List<AdminAppView>();
        foreach (var app in CloudApps.All)
        {
            var usage = CloudModules.Find(app.Id) is { } module ? await module.TotalUsageAsync(db, ct) : await SyncService.TotalUsageAsync(db, app.Id, ct);
            var d = devices.FirstOrDefault(x => x.App == app.Id);
            result.Add(new AdminAppView(View(app, enabled.GetValueOrDefault(app.Id, true)), d?.Count ?? 0, d?.Accounts ?? 0, usage));
        }

        return result;
    }

    /// <summary>
    /// Switches an app on or off. Off: its devices get 403 at their next request and cannot sign in, its pages and its menu are gone; its data stays and
    /// comes back when it is switched on again.
    /// </summary>
    public static async Task<Outcome> SetEnabledAsync(AppDbContext db, Actor actor, string app, bool enabled, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        if (!CloudApps.IsKnown(app))
        {
            return Outcome.Missing();
        }

        var row = await db.AppSettings.FirstOrDefaultAsync(a => a.App == app, ct);
        if (row is null)
        {
            row = new AppSetting { App = app };
            db.AppSettings.Add(row);
        }

        row.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        Forget();
        DeviceService.Invalidate();
        return Outcome.Success();
    }

    /// <summary>Forgets the cached states (after a change, and in tests with a fresh database).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Enabled.Clear();
            _generation++;
        }
    }

    private static async Task<Dictionary<string, bool>> EnabledAsync(AppDbContext db, CancellationToken ct)
    {
        var database = db.Database.GetConnectionString() ?? string.Empty;
        int generation;
        lock (Gate)
        {
            if (Enabled.TryGetValue(database, out var known))
            {
                return known;
            }

            generation = _generation;
        }

        var rows = await db.AppSettings.AsNoTracking().ToDictionaryAsync(a => a.App, a => a.IsEnabled, ct);
        lock (Gate)
        {
            // a switch that changed while it was read is read again next time
            if (generation == _generation)
            {
                Enabled[database] = rows;
            }
        }

        return rows;
    }
}
