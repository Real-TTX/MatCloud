using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>"Papierkorb": what was deleted, latest first; restore it, delete it for good, or empty the trash. It goes by itself after 30 days.</summary>
public sealed class TrashModel : DriveListPageModel
{
    public async Task OnGetAsync()
    {
        ReadTools(defaultSort: "modified", defaultDescending: true);
        var ct = HttpContext.RequestAborted;
        Revision = await DriveItems.LatestAsync(Db, Actor.UserId, ct);
        var trash = await DriveItems.TrashAsync(Db, Actor, Query with { Category = null, Offset = 0, Limit = DriveContract.MaxPageSize }, ct);
        var items = Narrow(trash.Items);
        Toolbar = BuildToolbar("/Drive/Trash", "Papierkorb", trash.Total, items.Count, placeholder: "Papierkorb durchsuchen", sortable: false);
        List = new DriveListView(items.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList(), Tiles, ShowLocation: true, Trash: true, Here);
        Pager = BuildPager(items.Count);
    }

    public async Task<IActionResult> OnPostRestoreAsync(long[] ids)
    {
        var restored = 0;
        foreach (var id in ids)
        {
            restored += (await DriveItems.RestoreAsync(Db, Actor, id, HttpContext.RequestAborted)).Ok ? 1 : 0;
        }

        Flash(restored == 1 ? "1 Element wiederhergestellt." : $"{restored} Elemente wiederhergestellt.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long[] ids)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            deleted += (await DriveItems.DeleteAsync(Db, Actor, id, HttpContext.RequestAborted)).Ok ? 1 : 0;
        }

        Flash(deleted == 1 ? "1 Element endgültig gelöscht." : $"{deleted} Elemente endgültig gelöscht.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEmptyAsync()
    {
        var deleted = await DriveItems.EmptyTrashAsync(Db, Actor, HttpContext.RequestAborted);
        Flash(deleted == 0 ? "Der Papierkorb war leer." : "Der Papierkorb ist leer.");
        return RedirectToPage();
    }
}
