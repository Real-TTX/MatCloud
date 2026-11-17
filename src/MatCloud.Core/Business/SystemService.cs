using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>The numbers of the installation (the administration, the tiles of the platform contract).</summary>
public sealed record SystemSnapshot(int Users, int ActiveUsers, int Admins, int Devices, int DevicesSeenToday, int AppsOn, long SyncItems, long SyncBytes, DateTime? LastDeviceContact);

/// <summary>
/// The settings and numbers of the installation: the public address (the links of the sign-in in the browser), the numbers of the overview and the
/// housekeeping of the platform. Only administrators read and change them.
/// </summary>
public static class SystemService
{
    public const string PublicUrlKey = "PublicUrl";

    /// <summary>The public address an administrator set, or null (then the address of the request).</summary>
    public static async Task<string?> PublicUrlAsync(AppDbContext db, CancellationToken ct = default)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == PublicUrlKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Sets the public address (empty: none). Only http and https addresses.</summary>
    public static async Task<Outcome> SetPublicUrlAsync(AppDbContext db, Actor actor, string? url, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var clean = (url ?? string.Empty).Trim().TrimEnd('/');
        if (clean.Length > 0 && (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || clean.Length > 300))
        {
            return Outcome.Fail("Eine vollständige Adresse mit http:// oder https://.", "PublicUrl");
        }

        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == PublicUrlKey, ct);
        if (row is null)
        {
            row = new SystemSetting { Key = PublicUrlKey };
            db.SystemSettings.Add(row);
        }

        row.Value = clean;
        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    public static async Task<SystemSnapshot> SnapshotAsync(AppDbContext db, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.AddDays(-1);
        var users = db.Users.AsNoTracking();
        var devices = db.Devices.AsNoTracking();
        var items = db.SyncItems.AsNoTracking().Where(i => i.UpdateState != SoftEntity.Deleted);
        return new SystemSnapshot(
            await users.CountAsync(ct),
            await users.CountAsync(u => u.IsActive, ct),
            await users.CountAsync(u => u.IsAdmin && u.IsActive, ct),
            await devices.CountAsync(ct),
            await devices.CountAsync(d => d.LastSeenDate > today, ct),
            (await AppService.EnabledAppsAsync(db, ct)).Count,
            await items.LongCountAsync(ct),
            await items.SumAsync(i => (long)i.Size, ct),
            await devices.MaxAsync(d => (DateTime?)d.LastSeenDate, ct));
    }

    /// <summary>
    /// The housekeeping (every hour, and the action "Aufräumen"): ended sessions, old sign-ins of devices and old tombstones of the general sync go,
    /// and every module does its own (the retention of the clips). Answers what went, per kind.
    /// </summary>
    public static async Task<(int Sessions, int Links, int Tombstones, int Modules)> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var sessions = await SessionService.CleanUpAsync(db, ct);
        var links = await DeviceLinkService.CleanUpAsync(db, ct);
        var tombstones = await SyncService.CleanUpAsync(db, ct);
        var modules = 0;
        foreach (var module in CloudModules.All)
        {
            modules += await module.MaintainAsync(db, ct);
        }

        return (sessions, links, tombstones, modules);
    }
}
