using System.Globalization;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using MatPlatform;

namespace MatCloud.Platform;

/// <summary>
/// MatCloud's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person becomes an account of MatCloud through
/// <see cref="ExternalSignInService"/> (by <c>sub</c>, else a new account; the claim <c>role</c> when it is there) and is then signed in with MatCloud's own
/// cookie and session, keeping the ID token and the <c>sid</c> of MatIdentity with the session. A local account with the same user name is not entered by
/// the name: the answer is <see cref="MatIdentitySignInResult.LinkRequired"/>, and the sign-in page links it after its password. When MatIdentity signs
/// the person out (back-channel logout, <see cref="IMatIdentitySessions"/>), their sessions here end too.
/// </summary>
public sealed class MatCloudIdentityUsers(AppDbContext db, ILogger<MatCloudIdentityUsers> logger) : IMatIdentityUsers, IMatIdentitySessions
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = await ExternalSignInService.SignInAsync(db, new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role), ct);
        if (result.LinkUserId is { } linkUserId)
        {
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));
        }

        if (result.User is not { } user)
        {
            return MatIdentitySignInResult.Refused(result.Error ?? "refused");
        }

        await CookieSignIn.SignInAsync(http, db, user, remember: true, person.IdToken, person.SessionId);
        logger.LogInformation("User {UserId} signed in through MatIdentity", user.Id);
        return MatIdentitySignInResult.Success();
    }

    public async Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        var ended = await SessionService.EndIdentitySessionsAsync(db, subject, sessionId, ct);
        logger.LogInformation("MatIdentity signed out a person: {Count} session(s) ended", ended);
    }
}
