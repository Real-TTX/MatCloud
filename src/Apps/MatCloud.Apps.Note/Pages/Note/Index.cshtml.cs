using MatCloud.Apps.Note.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Note.Pages.Note;

/// <summary>"Notizen": the notes of ThixNote, newest change first; search (titles and texts), folder and order above, the actions below.</summary>
public sealed class IndexModel : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public NoteListResult Result { get; private set; } = null!;

    public string? Folder { get; private set; }

    [BindProperty]
    public string NewFolder { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var folder = ListQuery.Text(Request, "folder");
        var sort = ListQuery.Choice(Request, "sort", "changed", "title", "folder") ?? "changed";
        var descending = ListQuery.Descending(Request, byDefault: sort == "changed");

        Result = await NoteService.ListAsync(Db, Actor, new NoteQuery(search, folder, sort, descending), HttpContext.RequestAborted);
        Folder = folder is not null && Result.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) ? folder : null;
        Toolbar = new ToolbarModel
        {
            Id = "notes",
            Action = "/Note",
            Label = "Notizen",
            Search = search,
            SearchPlaceholder = "Notizen durchsuchen",
            Filters = [new FilterField("folder", "Ordner", [.. Result.Folders.Select(f => new FilterOption(f, f))], Folder)],
            SortOptions = [new SortOption("changed", "Zuletzt geändert"), new SortOption("title", "Titel"), new SortOption("folder", "Ordner")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "changed",
            DefaultDescending = true,
            Total = Result.Total,
            Matching = Result.Items.Count,
            Noun = "Notizen",
            NounOne = "Notiz",
        };
    }

    public async Task<IActionResult> OnPostFolderAsync()
    {
        var outcome = await NoteService.CreateFolderAsync(Db, Actor, NewFolder, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            FlashError(outcome.Error ?? "Den Ordner gibt es nicht.");
            return RedirectToPage();
        }

        Flash("Der Ordner ist angelegt – auch auf deinen Geräten.");
        return Redirect("/Note?folder=" + Uri.EscapeDataString(outcome.Value!));
    }
}
