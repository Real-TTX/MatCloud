using Microsoft.AspNetCore.Mvc;
using MatCloud.Core.Business;

using MatCloud.Core.Web;

namespace MatCloud.Pages.Account;

/// <summary>The tab "Sicherheit": a new password. After a password set by an administrator the person comes here first (force=1).</summary>
public sealed class SecurityModel : AppPageModel
{
    [BindProperty]
    public string Current { get; set; } = string.Empty;

    [BindProperty]
    public string New { get; set; } = string.Empty;

    [BindProperty]
    public string Repeat { get; set; } = string.Empty;

    public bool MustChange => Me.Snapshot?.MustChangePassword == true;

    /// <summary>False for an account of MatIdentity that has no password here yet: it sets its first one (for the devices, and when MatIdentity is down).</summary>
    public bool HasPassword => Me.Snapshot?.HasPassword != false;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await AccountService.ChangePasswordAsync(Db, Actor, Current, New, Repeat, Me.SessionId);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Das Passwort wurde geändert. Andere Browser sind abgemeldet; die Geräte bleiben angemeldet.");
        return RedirectToPage();
    }
}
