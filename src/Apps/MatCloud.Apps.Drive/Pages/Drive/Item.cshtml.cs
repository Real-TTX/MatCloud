using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>An item of the Drive on its own page, tab "Allgemein": what it is, where it lies, who changed it; download, rename, move, copy, star, trash.</summary>
public sealed class ItemModel : DrivePageModel
{
    public DriveItem Item { get; private set; } = null!;

    public IReadOnlyList<DriveOpener> Openers { get; private set; } = [];

    public IReadOnlyList<(long Id, string Path)> Folders { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var outcome = await DriveItems.GetAsync(Db, Actor, id, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Item = outcome.Value!;
        Openers = Item.Kind == DriveItemKind.File && !Item.IsTrashed ? DriveOpeners.For(Item.Id, Item.Name) : [];
        Folders = (await DriveItems.FoldersAsync(Db, Actor, HttpContext.RequestAborted)).Where(f => f.Id != Item.Id && !IsBelow(f.Path)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(long id, string? name)
    {
        Report(await DriveItems.RenameAsync(Db, Actor, id, name, HttpContext.RequestAborted), "Umbenannt.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMoveAsync(long id, string? target)
    {
        Report(await DriveItems.MoveAsync(Db, Actor, id, long.TryParse(target, out var to) ? to : null, HttpContext.RequestAborted), "Verschoben.");
        return RedirectToPage(new { id });
    }

    /// <summary>"Kopie anlegen": a copy in the same folder ("Kopie von …"), then its page.</summary>
    public async Task<IActionResult> OnPostCopyAsync(long id)
    {
        var source = await DriveItems.GetAsync(Db, Actor, id, HttpContext.RequestAborted);
        if (!source.Ok)
        {
            return NotFound();
        }

        var outcome = await DriveItems.CopyAsync(Db, Actor, id, source.Value!.ParentId, null, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
            return RedirectToPage(new { id });
        }

        Flash($"Kopie „{outcome.Value!.Name}“ angelegt.");
        return RedirectToPage(new { id = outcome.Value.Id });
    }

    public async Task<IActionResult> OnPostStarAsync(long id, bool on)
    {
        await DriveItems.StarAsync(Db, Actor, id, on, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTrashAsync(long id)
    {
        var outcome = await DriveItems.TrashAsync(Db, Actor, id, HttpContext.RequestAborted);
        Report(outcome, "Liegt im Papierkorb.");
        return outcome.Ok ? LocalRedirect(DriveUi.FolderUrl(outcome.Value!.ParentId)) : RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id)
    {
        Report(await DriveItems.RestoreAsync(Db, Actor, id, HttpContext.RequestAborted), "Wiederhergestellt.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var outcome = await DriveItems.DeleteAsync(Db, Actor, id, HttpContext.RequestAborted);
        Report(outcome, "Endgültig gelöscht.");
        return outcome.Ok ? LocalRedirect("/Drive/Trash") : RedirectToPage(new { id });
    }

    /// <summary>A folder below the item itself (a folder never goes into itself).</summary>
    private bool IsBelow(string path)
    {
        if (Item.Kind != DriveItemKind.Folder)
        {
            return false;
        }

        var own = (Item.Location == "/" ? "/" : Item.Location + "/") + Item.Name;
        return path.StartsWith(own + "/", StringComparison.Ordinal);
    }
}
