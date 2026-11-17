using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>What the pages need to know about the signed-in person, read once per request from the session.</summary>
public sealed record UserSnapshot(long Id, string UserName, string DisplayName, bool IsAdmin, string Theme, bool MustChangePassword, bool IsExternal, bool HasPassword);

/// <summary>A valid session of the browser.</summary>
public sealed record SessionInfo(long SessionId, UserSnapshot User, DateTime ExpiresDate, DateTime LastSeenDate, bool Remember);

/// <summary>
/// The sessions of the browser. A session is a row in <c>UserSession</c>; the cookie carries only its token, so a session survives a restart and
/// can be ended (sign-out, a new password, a deactivated account, a back-channel logout of MatIdentity).
/// </summary>
public static class SessionService
{
    public static readonly TimeSpan ShortSession = TimeSpan.FromHours(12);
    public static readonly TimeSpan LongSession = TimeSpan.FromDays(60);
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private static int _generation;

    /// <summary>Changes whenever a session or an account changed: caches of sessions keyed by it are stale.</summary>
    public static int Generation => _generation;

    public static void Invalidate() => Interlocked.Increment(ref _generation);

    public static UserSnapshot Snapshot(User user) => new(
        user.Id, user.UserName, user.DisplayName, user.IsAdmin, user.Theme, user.MustChangePassword, user.ExternalSubject is not null, !string.IsNullOrEmpty(user.PasswordHash));

    public static async Task<UserSession> CreateAsync(
        AppDbContext db, long userId, bool remember, string? userAgent, string? ipAddress, string? identityToken = null, string? identitySessionId = null)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            UserId = userId,
            Token = Guid.NewGuid(),
            LastSeenDate = now,
            ExpiresDate = now + (remember ? LongSession : ShortSession),
            CreateUserId = userId,
            UserAgent = Trim(userAgent, 300),
            IpAddress = Trim(ipAddress, 64),
            IdentityToken = identityToken,
            IdentitySessionId = Trim(identitySessionId, 200),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>The session of a token while it is valid and its account active; null otherwise.</summary>
    public static async Task<SessionInfo?> LoadAsync(AppDbContext db, Guid token)
    {
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.Token == token)
            .Select(s => new { s.Id, s.IsRevoked, s.ExpiresDate, s.LastSeenDate, s.User })
            .FirstOrDefaultAsync();
        if (row is null || row.IsRevoked || row.User is null || !row.User.IsActive || row.ExpiresDate < DateTime.UtcNow)
        {
            return null;
        }

        var remember = row.ExpiresDate - row.LastSeenDate > ShortSession + TimeSpan.FromMinutes(1);
        return new SessionInfo(row.Id, Snapshot(row.User), row.ExpiresDate, row.LastSeenDate, remember);
    }

    /// <summary>The ID token of MatIdentity of a session (for the sign-out there), or null after a local sign-in.</summary>
    public static async Task<string?> IdentityTokenAsync(AppDbContext db, Guid token)
        => await db.Sessions.AsNoTracking().Where(s => s.Token == token).Select(s => s.IdentityToken).FirstOrDefaultAsync();

    /// <summary>Keeps a session alive while it is used (at most every <see cref="TouchEvery"/>).</summary>
    public static async Task<SessionInfo> TouchAsync(AppDbContext db, SessionInfo session)
    {
        var now = DateTime.UtcNow;
        if (now - session.LastSeenDate < TouchEvery)
        {
            return session;
        }

        var expires = now + (session.Remember ? LongSession : ShortSession);
        await db.Sessions.Where(s => s.Id == session.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenDate, now).SetProperty(x => x.ExpiresDate, expires));
        return session with { LastSeenDate = now, ExpiresDate = expires };
    }

    public static async Task RevokeAsync(AppDbContext db, Guid token)
    {
        await db.Sessions.Where(s => s.Token == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true));
        Invalidate();
    }

    /// <summary>Ends every session of an account, except one (the current one, after a change of the password).</summary>
    public static async Task RevokeAllAsync(AppDbContext db, long userId, long? exceptSessionId = null)
    {
        await db.Sessions.Where(s => s.UserId == userId && s.Id != exceptSessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true));
        Invalidate();
    }

    /// <summary>
    /// The back-channel logout of MatIdentity: with <paramref name="sessionId"/> the sessions that started with that session of MatIdentity, with the
    /// subject alone every session of the person. An unknown subject or session is no error. Answers how many sessions ended.
    /// </summary>
    public static async Task<int> EndIdentitySessionsAsync(AppDbContext db, string? subject, string? sessionId, CancellationToken ct = default)
    {
        var sessions = db.Sessions.Where(s => !s.IsRevoked);
        if (!string.IsNullOrEmpty(sessionId))
        {
            sessions = sessions.Where(s => s.IdentitySessionId == sessionId);
        }
        else if (!string.IsNullOrEmpty(subject))
        {
            sessions = sessions.Where(s => s.User!.ExternalSubject == subject);
        }
        else
        {
            return 0;
        }

        var ended = await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);
        Invalidate();
        return ended;
    }

    /// <summary>Removes ended and expired sessions (the housekeeping).</summary>
    public static async Task<int> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        return await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < limit).ExecuteDeleteAsync(ct);
    }

    private static string? Trim(string? text, int max) => string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
}
