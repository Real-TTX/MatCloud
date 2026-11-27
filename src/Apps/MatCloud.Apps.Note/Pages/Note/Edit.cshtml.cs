using MatCloud.Apps.Note.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Note.Pages.Note;

/// <summary>
/// A new note or a change of one: title (the file name), folder, kind (only for a new note: Markdown or text) and the text. A protected note is only
/// renamed or moved here; its text is changed on its own page, in the browser.
/// </summary>
public sealed class EditModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Key { get; set; }

    [BindProperty]
    public string Title { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Folder { get; set; } = string.Empty;

    [BindProperty]
    public NoteKind Kind { get; set; } = NoteKind.Markdown;

    [BindProperty]
    public string Text { get; set; } = string.Empty;

    [BindProperty]
    public string BaseHash { get; set; } = string.Empty;

    public NoteItem? Note { get; private set; }

    public IReadOnlyList<string> Folders { get; private set; } = [];

    public bool IsNew => Note is null;

    /// <summary>A device changed the note while it was open here (the next save replaces that change).</summary>
    public bool IsConflict { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Note is { } note)
        {
            Title = note.Title;
            Folder = note.Folder;
            Kind = note.Kind;
            Text = note.Content ?? string.Empty;
            BaseHash = note.Hash;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await NoteService.SaveAsync(Db, Actor, new NoteInput(Key, Title, Folder, Kind, Text, BaseHash), HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            await LoadAsync();
            if (Note is { Kind: not NoteKind.Protected } current && outcome.Field == "Text" && !string.IsNullOrEmpty(BaseHash) && BaseHash != current.Hash)
            {
                // a device changed the note meanwhile: the text of the person stays on the page, and saving again replaces the device's change on purpose
                // (not when a device protected it: the text stays on the page to be copied, and saving it stays refused)
                BaseHash = current.Hash;
                ModelState.Remove(nameof(BaseHash));
                IsConflict = true;
            }

            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Gespeichert – auch auf deinen Geräten.");
        return Redirect("/Note/View?key=" + Uri.EscapeDataString(outcome.Value!));
    }

    private async Task<bool> LoadAsync()
    {
        Folders = await NoteService.FoldersAsync(Db, Actor, HttpContext.RequestAborted);
        if (string.IsNullOrEmpty(Key))
        {
            return true;
        }

        var found = await NoteService.GetAsync(Db, Actor, Key, HttpContext.RequestAborted);
        Note = found.Value;
        return found.Ok;
    }
}
