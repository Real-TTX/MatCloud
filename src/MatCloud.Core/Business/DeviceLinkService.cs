using System.Security.Cryptography;
using MatCloud.Contract;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>A sign-in in the browser as the page Connect shows it.</summary>
public sealed record DeviceLinkView(string Code, string App, string DeviceName, string Platform, string AppVersion, string? IpAddress, DateTime ExpiresDate)
{
    public string AppName => CloudApps.NameOf(App);

    /// <summary>The code as people read it: ABCD-EFGH.</summary>
    public string DisplayCode => DeviceLinkService.Format(Code);
}

/// <summary>
/// The sign-in of a device in the browser (like the device flow of OAuth): the device asks for a code (<see cref="StartAsync"/>), shows it and opens
/// the page Connect; the person signs in there (with a password or through MatIdentity), compares the code and confirms it (<see cref="ApproveAsync"/>);
/// the device asks every few seconds (<see cref="PollAsync"/>) and gets its token once. A code lives ten minutes and works once.
/// </summary>
public static class DeviceLinkService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int PollSeconds = 2;

    /// <summary>The letters of a code: no 0/O, 1/I/L, 5/S, 8/B, 2/Z that look alike.</summary>
    private const string Alphabet = "ACDEFGHJKMNPQRTUVWXY34679";

    public static async Task<Outcome<DeviceLinkStart>> StartAsync(AppDbContext db, DeviceLinkRequest request, string? ipAddress, string baseUrl, CancellationToken ct = default)
    {
        var facts = new DeviceFacts(request.App ?? string.Empty, request.DeviceName ?? string.Empty, request.Platform ?? string.Empty, request.AppVersion ?? string.Empty);
        if (DeviceService.Problem(facts) is { } problem)
        {
            return Outcome<DeviceLinkStart>.Fail(problem.Message, problem.Field);
        }

        if (!await AppService.IsEnabledAsync(db, facts.App, ct))
        {
            return Outcome<DeviceLinkStart>.Fail(AppService.SwitchedOffText(facts.App), "app");
        }

        var now = DateTime.UtcNow;
        string code;
        do
        {
            code = NewCode();
        }
        while (await db.DeviceLinks.AnyAsync(l => l.Code == code && l.ExpiresDate > now, ct));

        var link = new DeviceLink
        {
            Code = code,
            App = facts.App,
            DeviceName = facts.DeviceName.Trim(),
            Platform = Clip(facts.Platform.Trim().ToLowerInvariant(), 40),
            AppVersion = Clip(facts.AppVersion.Trim(), 60),
            IpAddress = ipAddress is null ? null : Clip(ipAddress, 64),
            ExpiresDate = now + Lifetime,
        };
        db.DeviceLinks.Add(link);
        await db.SaveChangesAsync(ct);

        var url = baseUrl.TrimEnd('/') + CloudContract.ConnectPage + "?code=" + Format(code);
        return Outcome<DeviceLinkStart>.Success(new DeviceLinkStart(Format(code), url, link.Token.ToString("D"), (int)Lifetime.TotalSeconds, PollSeconds));
    }

    /// <summary>
    /// Where a sign-in stands. Once confirmed, the answer carries the token of the new device, only this one time (then the sign-in is used up).
    /// </summary>
    public static async Task<DeviceLinkPollResponse> PollAsync(AppDbContext db, string? pollToken, string serverVersion, CancellationToken ct = default)
    {
        if (!Guid.TryParse(pollToken, out var token))
        {
            return new DeviceLinkPollResponse(DeviceLinkState.Expired);
        }

        var link = await db.DeviceLinks.FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || link.State == DeviceLink.Fetched || (link.State == DeviceLink.Pending && link.ExpiresDate < DateTime.UtcNow))
        {
            return new DeviceLinkPollResponse(DeviceLinkState.Expired);
        }

        switch (link.State)
        {
            case DeviceLink.Denied:
                return new DeviceLinkPollResponse(DeviceLinkState.Denied);
            case DeviceLink.Pending:
                return new DeviceLinkPollResponse(DeviceLinkState.Pending);
        }

        var device = await db.Devices.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == link.DeviceId, ct);
        link.State = DeviceLink.Fetched;
        await db.SaveChangesAsync(ct);
        if (device?.User is not { IsActive: true } user)
        {
            return new DeviceLinkPollResponse(DeviceLinkState.Expired);
        }

        return new DeviceLinkPollResponse(DeviceLinkState.Approved, DeviceService.Response(user, device, serverVersion));
    }

    /// <summary>The sign-in of a code as the page shows it to the signed-in person; missing when the code is unknown, used or expired.</summary>
    public static async Task<Outcome<DeviceLinkView>> FindAsync(AppDbContext db, string? code, CancellationToken ct = default)
    {
        var clean = Normalize(code);
        if (clean is null)
        {
            return Outcome<DeviceLinkView>.Missing();
        }

        var now = DateTime.UtcNow;
        var link = await db.DeviceLinks.AsNoTracking()
            .Where(l => l.Code == clean && l.State == DeviceLink.Pending && l.ExpiresDate > now)
            .Select(l => new DeviceLinkView(l.Code, l.App, l.DeviceName, l.Platform, l.AppVersion, l.IpAddress, l.ExpiresDate))
            .FirstOrDefaultAsync(ct);
        return link is null ? Outcome<DeviceLinkView>.Missing() : Outcome<DeviceLinkView>.Success(link);
    }

    /// <summary>The signed-in person confirms a code: the device of their account is made, and the device fetches its token with its next poll.</summary>
    public static async Task<Outcome<DeviceView>> ApproveAsync(AppDbContext db, Actor actor, string? code, CancellationToken ct = default)
    {
        if (actor.DeviceId is not null)
        {
            return Outcome<DeviceView>.Missing();
        }

        var link = await OpenAsync(db, code, ct);
        if (link is null)
        {
            return Outcome<DeviceView>.Fail("Diesen Code gibt es nicht, er ist abgelaufen oder schon benutzt. Bitte in der App noch einmal anmelden.", "Code");
        }

        if (!await AppService.IsEnabledAsync(db, link.App, ct))
        {
            return Outcome<DeviceView>.Fail(AppService.SwitchedOffText(link.App), "Code");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId && u.IsActive, ct);
        if (user is null)
        {
            return Outcome<DeviceView>.Missing();
        }

        var device = await DeviceService.CreateAsync(db, user, new DeviceFacts(link.App, link.DeviceName, link.Platform, link.AppVersion), link.IpAddress, ct);
        link.State = DeviceLink.Approved;
        link.UserId = user.Id;
        link.DeviceId = device.Id;
        await db.SaveChangesAsync(ct);
        return Outcome<DeviceView>.Success(new DeviceView(device.Id, device.App, device.Name, device.Platform, device.AppVersion, device.CreateDate, device.LastSeenDate, device.IpAddress));
    }

    /// <summary>The signed-in person refuses a code: the device is told so and signs in nowhere.</summary>
    public static async Task<Outcome> DenyAsync(AppDbContext db, Actor actor, string? code, CancellationToken ct = default)
    {
        var link = actor.DeviceId is null ? await OpenAsync(db, code, ct) : null;
        if (link is null)
        {
            return Outcome.Missing();
        }

        link.State = DeviceLink.Denied;
        link.UserId = actor.UserId;
        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    /// <summary>Removes sign-ins that ended more than a day ago (the housekeeping).</summary>
    public static async Task<int> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        return await db.DeviceLinks.Where(l => l.ExpiresDate < limit).ExecuteDeleteAsync(ct);
    }

    /// <summary>ABCDEFGH as ABCD-EFGH.</summary>
    public static string Format(string code) => code.Length == 8 ? code[..4] + "-" + code[4..] : code;

    /// <summary>A code as a person may type it (small letters, a dash, spaces) in its stored form, or null when it cannot be one.</summary>
    public static string? Normalize(string? code)
    {
        var clean = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 8 && clean.All(Alphabet.Contains) ? clean : null;
    }

    private static async Task<DeviceLink?> OpenAsync(AppDbContext db, string? code, CancellationToken ct)
    {
        var clean = Normalize(code);
        if (clean is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        return await db.DeviceLinks.FirstOrDefaultAsync(l => l.Code == clean && l.State == DeviceLink.Pending && l.ExpiresDate > now, ct);
    }

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
