using MatCloud.Apps.Note.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Note.Pages.Note;

/// <summary>
/// One note: Markdown drawn as a page (no HTML of its own: Markdig with HTML switched off), plain text as it is, a protected note locked until its password
/// is typed – it is decrypted in the browser (note-crypt.js), edited there and sent back encrypted (<see cref="OnPostProtectedAsync"/>).
/// </summary>
public sealed class ViewModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Key { get; set; } = string.Empty;

    [BindProperty]
    public string Bytes { get; set; } = string.Empty;

    [BindProperty]
    public string BaseHash { get; set; } = string.Empty;

    public NoteItem Note { get; private set; } = null!;

    /// <summary>The Markdown as HTML (only for a Markdown note).</summary>
    public string? Html { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await NoteService.GetAsync(Db, Actor, Key, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Note = outcome.Value!;
        if (Note.Kind == NoteKind.Markdown)
        {
            Html = NoteMarkdown.ToHtml(Note.Content ?? string.Empty, Note.Title);
        }

        return Page();
    }

    /// <summary>The new text of a protected note, encrypted in the browser.</summary>
    public async Task<IActionResult> OnPostProtectedAsync()
    {
        var outcome = await NoteService.SaveProtectedAsync(Db, Actor, Key, Bytes, BaseHash, HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
        }
        else
        {
            Flash("Gespeichert – verschlüsselt, auch auf deinen Geräten.");
        }

        return RedirectToPage(new { key = Key });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await NoteService.DeleteAsync(Db, Actor, Key, HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Gelöscht. ThixNote legt die Notiz auf deinen Geräten in den Papierkorb.");
        return Redirect("/Note");
    }
}
