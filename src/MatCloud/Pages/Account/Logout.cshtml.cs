using MatCloud.Core.Web;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages.Account;

/// <summary>
/// Signs the browser out (only by a form, so another site cannot do it with a link). After a sign-in through MatIdentity the session there ends too (its
/// end-session endpoint, then back to the sign-in page with <c>?signedOut=1</c>); with MatIdentity configured a local session lands there as well, so the
/// sign-in page does not sign in again at once.
/// </summary>
public sealed class LogoutModel(MatPlatformSettings platform) : AppPageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        var identityToken = await CookieSignIn.SignOutAsync(HttpContext, Db);
        if (platform.IdentityEnabled && identityToken is not null)
        {
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, identityToken);
        }

        return Redirect("/Account/Login?signedOut=1");
    }
}
