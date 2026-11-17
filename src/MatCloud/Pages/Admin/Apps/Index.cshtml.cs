using MatCloud.Core.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages.Admin.Apps;

/// <summary>
/// The apps of this installation (administrators only): which are switched on, which have a module (pages and an API of their own) and what all accounts
/// keep in each. Off: the devices of the app get 403 and cannot sign in, its pages and menu are gone; its data stays.
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public IReadOnlyList<AdminAppView> Apps { get; private set; } = [];

    public async Task OnGetAsync() => Apps = await AppService.ListAsync(Db, Actor, HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostToggleAsync(string app, bool enabled)
    {
        var outcome = await AppService.SetEnabledAsync(Db, Actor, app, enabled, HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash(enabled ? "Die App ist eingeschaltet." : "Die App ist ausgeschaltet. Ihre Geräte synchronisieren nicht mehr; die Daten bleiben.");
        return RedirectToPage();
    }
}
