using System.Collections.Concurrent;
using MatCloud.Contract;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>A device as the API knows it after its token was checked.</summary>
public sealed record DeviceIdentity(long DeviceId, long UserId, bool IsAdmin, string Name, string App, DateTime LastSeenDate)
{
    public Actor Actor => new(UserId, IsAdmin, DeviceId, Name, App);
}

/// <summary>A device in the pages of the account.</summary>
public sealed record DeviceView(long Id, string App, string Name, string Platform, string AppVersion, DateTime CreateDate, DateTime LastSeenDate, string? IpAddress)
{
    public string AppName => CloudApps.NameOf(App);
}

/// <summary>What a device sends about itself when it signs in.</summary>
public sealed record DeviceFacts(string App, string DeviceName, string Platform, string AppVersion);

/// <summary>
/// The devices of an account: an app on a computer. A device signs in with the user name and password of a local account
/// (<see cref="LoginAsync"/>) or in the browser (<see cref="DeviceLinkService"/>) and gets a token (shown once); with it the device syncs the data
/// of its app, and only of its app. Removing a device in the browser (or signing out on the device) ends its access at once: the next request answers 401.
/// </summary>
public static class DeviceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<Guid, (DeviceIdentity Identity, DateTime Cached, int Generation)> Cache = new();
    private static int _generation;

    /// <summary>What is wrong with the facts a device sends, or null.</summary>
    public static (string Message, string Field)? Problem(DeviceFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.App))
        {
            // a version from before MatCloud (it sends no app): the app is too old, not the cloud
            return ("Diese Version der App kennt die MatCloud noch nicht. Bitte die App aktualisieren.", "app");
        }

        if (!CloudApps.IsKnown(facts.App))
        {
            return ("Diese App kennt die MatCloud nicht. Bitte die MatCloud aktualisieren.", "app");
        }

        var name = (facts.DeviceName ?? string.Empty).Trim();
        return name.Length == 0 || name.Length > CloudContract.MaxDeviceNameLength
            ? ("Der Gerätename fehlt oder ist zu lang (höchstens 100 Zeichen).", "deviceName")
            : null;
    }

    /// <summary>Signs a device in with the user name and password of a local account.</summary>
    public static async Task<Outcome<DeviceLoginResponse>> LoginAsync(AppDbContext db, DeviceLoginRequest request, string? ipAddress, string serverVersion, CancellationToken ct = default)
    {
        var facts = new DeviceFacts(request.App ?? string.Empty, request.DeviceName ?? string.Empty, request.Platform ?? string.Empty, request.AppVersion ?? string.Empty);
        if (Problem(facts) is { } problem)
        {
            return Outcome<DeviceLoginResponse>.Fail(problem.Message, problem.Field);
        }

        if (!await AppService.IsEnabledAsync(db, facts.App, ct))
        {
            return Outcome<DeviceLoginResponse>.Fail(AppService.SwitchedOffText(facts.App), "app");
        }

        var signIn = await UserService.SignInAsync(db, request.UserName ?? string.Empty, request.Password ?? string.Empty, ct);
        if (!signIn.Ok)
        {
            return Outcome<DeviceLoginResponse>.Fail(signIn.Error!, "password");
        }

        var user = signIn.Value!;
        var device = await CreateAsync(db, user, facts, ipAddress, ct);
        return Outcome<DeviceLoginResponse>.Success(Response(user, device, serverVersion));
    }

    /// <summary>
    /// Makes the device of an account (after a sign-in with the password, or when a person confirmed it in the browser). The same computer signing in
    /// again with the same app replaces its old entry (a fresh token), so the list of devices does not fill up.
    /// </summary>
    internal static async Task<Device> CreateAsync(AppDbContext db, User user, DeviceFacts facts, string? ipAddress, CancellationToken ct)
    {
        var name = facts.DeviceName.Trim();
        var platform = Clip(facts.Platform.Trim().ToLowerInvariant(), 40);
        var previous = await db.Devices.Where(d => d.UserId == user.Id && d.App == facts.App && d.Name == name && d.Platform == platform).ToListAsync(ct);
        db.Devices.RemoveRange(previous);

        var now = DateTime.UtcNow;
        var device = new Device
        {
            UserId = user.Id,
            Token = Guid.NewGuid(),
            App = facts.App,
            Name = name,
            Platform = platform,
            AppVersion = Clip(facts.AppVersion.Trim(), 60),
            LastSeenDate = now,
            IpAddress = Clip(ipAddress ?? string.Empty, 64),
            CreateUserId = user.Id,
        };
        db.Devices.Add(device);
        db.ActingUserId = user.Id;
        await db.SaveChangesAsync(ct);
        Invalidate();
        return device;
    }

    internal static DeviceLoginResponse Response(User user, Device device, string serverVersion)
        => new(device.Token.ToString("D"), device.Id, new AccountInfo(user.UserName, user.DisplayName, device.Name, device.App, serverVersion));

    /// <summary>The device of a bearer token, or null when the token is unknown, the device removed or the account inactive. Cached for 20 seconds.</summary>
    public static async Task<DeviceIdentity?> AuthenticateAsync(AppDbContext db, string? bearer, CancellationToken ct = default)
    {
        if (!Guid.TryParse(bearer, out var token) || token == Guid.Empty)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(token, out var hit) && hit.Generation == _generation && now - hit.Cached < CacheFor)
        {
            return hit.Identity;
        }

        var row = await db.Devices.AsNoTracking()
            .Where(d => d.Token == token)
            .Select(d => new { d.Id, d.UserId, d.Name, d.App, d.LastSeenDate, d.User!.IsAdmin, d.User.IsActive })
            .FirstOrDefaultAsync(ct);
        if (row is null || !row.IsActive)
        {
            Cache.TryRemove(token, out _);
            return null;
        }

        var identity = new DeviceIdentity(row.Id, row.UserId, row.IsAdmin, row.Name, row.App, row.LastSeenDate);
        Cache[token] = (identity, now, _generation);
        return identity;
    }

    /// <summary>Notes that the device was seen (at most every two minutes), with its address and the version of the app.</summary>
    public static async Task TouchAsync(AppDbContext db, DeviceIdentity device, string? ipAddress, string? appVersion, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now - device.LastSeenDate < TouchEvery)
        {
            return;
        }

        var ip = Clip(ipAddress ?? string.Empty, 64);
        var version = Clip((appVersion ?? string.Empty).Trim(), 60);
        if (version.Length > 0)
        {
            await db.Devices.Where(d => d.Id == device.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip).SetProperty(d => d.AppVersion, version), ct);
        }
        else
        {
            await db.Devices.Where(d => d.Id == device.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip), ct);
        }

        foreach (var (token, entry) in Cache.Where(e => e.Value.Identity.DeviceId == device.DeviceId).ToList())
        {
            Cache[token] = (entry.Identity with { LastSeenDate = now }, entry.Cached, entry.Generation);
        }
    }

    /// <summary>The device signs itself out: its token ends.</summary>
    public static async Task LogoutAsync(AppDbContext db, Actor actor)
    {
        if (actor.DeviceId is not { } id)
        {
            return;
        }

        await RemoveAsync(db, actor with { DeviceId = null }, id);
    }

    /// <summary>
    /// The devices of the actor's account (search in name, app, platform and version; one app or all; sort by last contact, name, app or sign-in) and how
    /// many there are in all.
    /// </summary>
    public static async Task<(IReadOnlyList<DeviceView> Items, int Total)> ListAsync(AppDbContext db, Actor actor, string? search, string? app, string sort, bool descending)
    {
        var all = db.Devices.AsNoTracking().Where(d => d.UserId == actor.UserId);
        var total = await all.CountAsync();
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + UserService.EscapeLike(search.Trim()) + "%";
            var apps = CloudApps.All.Where(a => a.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).Select(a => a.Id).ToList();
            filtered = filtered.Where(d => EF.Functions.ILike(d.Name, pattern, "\\") || EF.Functions.ILike(d.Platform, pattern, "\\")
                                           || EF.Functions.ILike(d.AppVersion, pattern, "\\") || apps.Contains(d.App));
        }

        if (!string.IsNullOrEmpty(app))
        {
            filtered = filtered.Where(d => d.App == app);
        }

        filtered = (sort, descending) switch
        {
            ("name", false) => filtered.OrderBy(d => d.Name),
            ("name", true) => filtered.OrderByDescending(d => d.Name),
            ("app", false) => filtered.OrderBy(d => d.App).ThenBy(d => d.Name),
            ("app", true) => filtered.OrderByDescending(d => d.App).ThenBy(d => d.Name),
            ("created", false) => filtered.OrderBy(d => d.CreateDate),
            ("created", true) => filtered.OrderByDescending(d => d.CreateDate),
            (_, false) => filtered.OrderBy(d => d.LastSeenDate),
            _ => filtered.OrderByDescending(d => d.LastSeenDate),
        };
        var items = await filtered
            .Select(d => new DeviceView(d.Id, d.App, d.Name, d.Platform, d.AppVersion, d.CreateDate, d.LastSeenDate, d.IpAddress))
            .ToListAsync();
        return (items, total);
    }

    /// <summary>Gives a device of the actor's account another name (shown in the list and as the source of what it writes from now on).</summary>
    public static async Task<Outcome> RenameAsync(AppDbContext db, Actor actor, long id, string name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > CloudContract.MaxDeviceNameLength)
        {
            return Outcome.Fail("Der Name hat 1 bis 100 Zeichen.", "Name");
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == actor.UserId);
        if (device is null)
        {
            return Outcome.Missing();
        }

        device.Name = clean;
        await db.SaveChangesAsync();
        Invalidate();
        return Outcome.Success();
    }

    public static async Task<Outcome<DeviceView>> GetAsync(AppDbContext db, Actor actor, long id)
    {
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == id && d.UserId == actor.UserId)
            .Select(d => new DeviceView(d.Id, d.App, d.Name, d.Platform, d.AppVersion, d.CreateDate, d.LastSeenDate, d.IpAddress))
            .FirstOrDefaultAsync();
        return device is null ? Outcome<DeviceView>.Missing() : Outcome<DeviceView>.Success(device);
    }

    /// <summary>Removes a device of the actor's own account (only the browser of the account, or the device itself): its token answers 401 from now on.</summary>
    public static async Task<Outcome> RemoveAsync(AppDbContext db, Actor actor, long id)
    {
        if (actor.DeviceId is { } own && own != id)
        {
            return Outcome.Missing();
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == actor.UserId);
        if (device is null)
        {
            return Outcome.Missing();
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        Invalidate();
        return Outcome.Success();
    }

    /// <summary>Every device of an account ends (the account was deactivated or deleted), or every device of one app of it.</summary>
    public static async Task RemoveAllAsync(AppDbContext db, long userId, string? app = null)
    {
        var devices = db.Devices.Where(d => d.UserId == userId);
        if (app is not null)
        {
            devices = devices.Where(d => d.App == app);
        }

        await devices.ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdateState, SoftEntity.Deleted).SetProperty(d => d.UpdateDate, DateTime.UtcNow));
        Invalidate();
    }

    /// <summary>Forgets every cached token (an app was switched off: its devices must be asked again).</summary>
    public static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Cache.Clear();
    }

    public static string PlatformName(string platform) => platform switch
    {
        "windows" => "Windows",
        "macos" or "osx" => "macOS",
        "linux" => "Linux",
        "" => "Unbekannt",
        _ => platform,
    };

    public static string PlatformIcon(string platform) => platform switch
    {
        "windows" or "macos" or "osx" or "linux" => "monitor",
        _ => "smartphone",
    };

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
