using MatCloud.Core.Business;
using MatCloud.Core.Modules;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages.Apps;

/// <summary>
/// One app of the person: its devices, what it keeps (for an app of the general sync: per collection) and, after a gap, the removal of its data in the
/// cloud (its devices are signed out; the data on them stays).
/// </summary>
public sealed class DetailModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public MyAppView App { get; private set; } = null!;

    public IReadOnlyList<DeviceView> Devices { get; private set; } = [];

    public IReadOnlyList<(string Collection, long Items, long Bytes, DateTime? LastChange)> Collections { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await AppService.MineAsync(Db, Actor, Id, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        App = outcome.Value!;
        Devices = (await DeviceService.ListAsync(Db, Actor, null, Id, "seen", true)).Items;
        if (!CloudModules.HasModule(Id))
        {
            Collections = await SyncService.CollectionsAsync(Db, Actor, Id, HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await AppService.DeleteMyDataAsync(Db, Actor, Id, HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Die Daten wurden aus der MatCloud gelöscht und die Geräte dieser App abgemeldet.");
        return RedirectToPage(new { id = Id });
    }
}
