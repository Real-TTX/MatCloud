using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>
/// "Meine Ablage": a folder (none: the top) with its folders and files, or – with a search – the items of the whole Drive whose name fits. Toolbar
/// above (search, type, sort, view), the rows, the pager, the actions below (upload, new folder, download, rename, move, trash).
/// </summary>
public sealed class IndexModel : DriveListPageModel
{
    public DriveItem? Folder { get; private set; }

    public IReadOnlyList<DriveCrumb> Ancestors { get; private set; } = [];

    public bool Searching { get; private set; }

    public long? FolderId => Folder?.Id;

    public async Task<IActionResult> OnGetAsync(long? folder)
    {
        ReadTools();
        var ct = HttpContext.RequestAborted;
        Revision = await DriveItems.LatestAsync(Db, Actor.UserId, ct);
        var keep = new Dictionary<string, string?> { ["folder"] = folder?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (Search is not null)
        {
            Searching = true;
            var found = await DriveItems.SearchAsync(Db, Actor, Search, Query, ct);
            Toolbar = BuildToolbar("/Drive", "Ablage", found.Total, found.Total, keep);
            List = new DriveListView(found.Items, Tiles, ShowLocation: true, Trash: false, Here);
            Pager = BuildPager(found.Total);
        }
        else
        {
            var listing = await DriveItems.ListAsync(Db, Actor, folder, Query, ct);
            if (!listing.Ok)
            {
                return NotFound();
            }

            Folder = listing.Value!.Folder;
            Ancestors = listing.Value.Ancestors;
            var total = Category is null ? listing.Value.Total : (await DriveItems.ListAsync(Db, Actor, folder, Query with { Category = null, Limit = 1 }, ct)).Value!.Total;
            Toolbar = BuildToolbar("/Drive", Folder?.Name ?? "Meine Ablage", total, listing.Value.Total, keep);
            List = new DriveListView(listing.Value.Items, Tiles, ShowLocation: false, Trash: false, Here);
            Pager = BuildPager(listing.Value.Total);
        }

        await LoadFoldersAsync();
        return Page();
    }

    /// <summary>"Neuer Ordner" (the dialog below the list): in the folder of the page; then into it.</summary>
    public async Task<IActionResult> OnPostFolderAsync(long? parent, string? name, string? returnUrl)
    {
        var outcome = await DriveItems.CreateFolderAsync(Db, Actor, parent, name, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
            return Back(returnUrl, DriveUi.FolderUrl(parent));
        }

        Flash($"Ordner „{outcome.Value!.Name}“ angelegt.");
        return LocalRedirect(DriveUi.FolderUrl(outcome.Value.Id));
    }
}
