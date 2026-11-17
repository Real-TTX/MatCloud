using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using MatCloud.Core.Business;
using MatCloud.Core.Data;

namespace MatCloud.Core.Web;

/// <summary>
/// The cookie of the browser: it carries only the token of a session (<see cref="SessionService"/>), signed and encrypted with the keys in the volume.
/// Every request checks the session (cached for a few seconds, forgotten when a session or an account changes).
/// </summary>
public static class CookieSignIn
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string CookieName = "matcloud.session";
    private const string SessionClaim = "matcloud_session";

    /// <summary>
    /// Starts a session and gives the browser its cookie. After a sign-in through MatIdentity the session keeps MatIdentity's ID token (for the sign-out
    /// there) and its <c>sid</c> (for the back-channel logout).
    /// </summary>
    public static async Task SignInAsync(HttpContext http, AppDbContext db, User user, bool remember, string? identityToken = null, string? identitySessionId = null)
    {
        var session = await SessionService.CreateAsync(
            db, user.Id, remember, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), identityToken, identitySessionId);
        var identity = new ClaimsIdentity([new Claim(SessionClaim, session.Token.ToString())], Scheme);
        await http.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = remember,
            ExpiresUtc = session.ExpiresDate,
            AllowRefresh = true,
        });
    }

    /// <summary>Ends the session of the request; answers MatIdentity's ID token when the session began there (the caller signs out there too).</summary>
    public static async Task<string?> SignOutAsync(HttpContext http, AppDbContext db)
    {
        string? identityToken = null;
        if (Guid.TryParse(http.User.FindFirstValue(SessionClaim), out var token))
        {
            identityToken = await SessionService.IdentityTokenAsync(db, token);
            await SessionService.RevokeAsync(db, token);
        }

        await http.SignOutAsync(Scheme);
        return identityToken;
    }

    /// <summary>Runs for every request with a cookie: accepts it only while its session is valid and the account active.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var found = Guid.TryParse(context.Principal?.FindFirstValue(SessionClaim), out var token) ? await LoadAsync(db, cache, token) : null;
        if (found is null)
        {
            context.RejectPrincipal();
            await http.SignOutAsync(Scheme);
            return;
        }

        http.Items[CurrentUser.ItemKey] = found.User;
        http.Items[CurrentUser.SessionItemKey] = found.SessionId;
        http.Items[AppDbContext.ActorItem] = found.User.Id;

        var claims = new List<Claim>
        {
            new(SessionClaim, token.ToString()),
            new(ClaimTypes.NameIdentifier, found.User.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, found.User.UserName),
        };
        if (found.User.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)));
    }

    private static async Task<SessionInfo?> LoadAsync(AppDbContext db, IMemoryCache cache, Guid token)
    {
        var key = $"session:{SessionService.Generation}:{token}";
        if (!cache.TryGetValue(key, out SessionInfo? session))
        {
            session = await SessionService.LoadAsync(db, token);
            if (session is null)
            {
                return null;
            }

            cache.Set(key, session, TimeSpan.FromSeconds(20));
        }

        if (session is null || session.ExpiresDate < DateTime.UtcNow)
        {
            return null;
        }

        var touched = await SessionService.TouchAsync(db, session);
        if (!ReferenceEquals(touched, session))
        {
            cache.Set(key, touched, TimeSpan.FromSeconds(20));
        }

        return touched;
    }
}
