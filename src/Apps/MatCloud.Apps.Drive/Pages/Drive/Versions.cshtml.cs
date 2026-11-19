using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>The tab "Versionen" of a file: every saved state, newest first, from which device and app; download one, make it current again, remove an old one.</summary>
public sealed class VersionsModel : DrivePageModel
{
    public DriveItem Item { get; private set; } = null!;

    public IReadOnlyList<DriveVersion> Versions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var item = await DriveItems.GetAsync(Db, Actor, id, HttpContext.RequestAborted);
        var versions = await DriveFiles.VersionsAsync(Db, Actor, id, HttpContext.RequestAborted);
        if (!item.Ok || !versions.Ok)
        {
            return NotFound();
        }

        Item = item.Value!;
        Versions = versions.Value!.Versions;
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id, long version)
    {
        var outcome = await DriveFiles.RestoreVersionAsync(Db, Actor, id, version, HttpContext.RequestAborted);
        Report(outcome, outcome.Ok ? $"Wiederhergestellt als Version {outcome.Value!.Version}." : string.Empty);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, long version)
    {
        Report(await DriveFiles.DeleteVersionAsync(Db, Actor, id, version, HttpContext.RequestAborted), "Version gelöscht.");
        return RedirectToPage(new { id });
    }
}
