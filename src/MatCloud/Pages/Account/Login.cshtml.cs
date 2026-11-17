using System.Globalization;
using MatCloud.Core.Business;
using MatCloud.Core.Web;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCloud.Pages.Account;

/// <summary>
/// The sign-in of the browser. With MatIdentity configured (<c>MATIDENTITY_AUTHORITY</c>) a visit goes straight on to MatIdentity; the local form stays
/// reachable with <c>?local=1</c> (a way in for the administrator when MatIdentity is down), and <c>?signedOut=1</c> / <c>?ssoError=1</c> show it too
/// instead of signing in again at once (<c>?denied=1</c>: MatIdentity signed the person in, MatCloud refused the account). After a sign-in through
/// MatIdentity whose user name matches a local account that is not linked yet, the page shows <c>?local=1&amp;link=1</c>: a successful sign-in of exactly
/// that account links it (<see cref="MatIdentityPendingLink"/>); Cancel deletes the pending link.
/// </summary>
[EnableRateLimiting("signin")]
public sealed class LoginModel(MatPlatformSettings platform, ILogger<LoginModel> logger) : AppPageModel
{
    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool Remember { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => platform.IdentityEnabled;

    public bool SignedOut { get; private set; }

    public bool SsoError { get; private set; }

    public bool Denied { get; private set; }

    /// <summary>The pending link to a local account, when MatIdentity sent the person here to prove it (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    public bool LinkExpired { get; private set; }

    /// <summary>The address that starts the sign-in at MatIdentity and comes back to <see cref="ReturnUrl"/>.</summary>
    public string IdentitySignInUrl => string.IsNullOrEmpty(ReturnUrl) ? "/Account/Login" : "/Account/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    private bool Local { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();
        if (User.Identity?.IsAuthenticated == true && PendingLink is null)
        {
            return Back(ReturnUrl, "/");
        }

        // Auto login: straight on to MatIdentity, no local form in the way (also for the very first person: MatIdentity makes the first account).
        if (IdentityEnabled && !Local && !SignedOut && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = Target() });
                return new EmptyResult();
            }
            catch (InvalidOperationException exception)
            {
                logger.LogWarning(exception, "MatIdentity is not reachable, showing the local sign-in");
                SsoError = true;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        var outcome = await UserService.SignInAsync(Db, UserName, Password, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(nameof(Password), outcome.Error);
            return Page();
        }

        var user = outcome.Value!;
        string? identityToken = null;
        string? identitySessionId = null;

        // A pending link of MatIdentity is only ever for the account that matched; it is deleted either way.
        if (MatIdentityPendingLink.TakeFor(HttpContext, user.Id.ToString(CultureInfo.InvariantCulture)) is { } pending)
        {
            var link = await ExternalSignInService.LinkAsync(
                Db, user.Id, new ExternalIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role), HttpContext.RequestAborted);
            if (link.User is { } linked)
            {
                user = linked;
                identityToken = pending.IdToken;
                identitySessionId = pending.SessionId;
                Flash("Dein Konto ist jetzt mit MatIdentity verbunden.");
            }
            else
            {
                logger.LogWarning("User {UserId} signed in, but was not linked with MatIdentity: {Error}", user.Id, link.Error);
            }
        }

        await CookieSignIn.SignInAsync(HttpContext, Db, user, Remember, identityToken, identitySessionId);
        return LocalRedirect(Target());
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return LocalRedirect("/Account/Login?local=1");
    }

    private void Prepare()
    {
        Local = Flag("local");
        SignedOut = Flag("signedOut");
        SsoError = Flag("ssoError");
        Denied = Flag("denied");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && Flag("link");
    }

    private bool Flag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    private string Target() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
