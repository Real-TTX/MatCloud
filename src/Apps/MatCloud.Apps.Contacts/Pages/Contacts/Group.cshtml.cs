using MatCloud.Apps.Contacts.Business;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>"Kontaktgruppe": its name (Speichern · Zurück · gap · Löschen), its members (take out the selected ones) and the field to add more.</summary>
public sealed class GroupModel : ContactsPageModel
{
    public GroupEntry Group { get; private set; } = null!;

    public IReadOnlyList<ContactEntry> Members { get; private set; } = [];

    [BindProperty] public string Name { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(string? key)
    {
        if (!await LoadAsync(key))
        {
            return NotFound();
        }

        Name = Group.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? key)
    {
        if (!await LoadAsync(key))
        {
            return NotFound();
        }

        var outcome = await ContactBook.SaveGroupAsync(Db, Actor, key, Name, Ct);
        if (!outcome.Ok)
        {
            AddError(nameof(Name), outcome.Error);
            return Page();
        }

        Flash("Die Gruppe ist gespeichert.");
        return RedirectToPage(new { key });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? key)
    {
        var outcome = await ContactBook.DeleteGroupAsync(Db, Actor, key, Ct);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Flash("Die Gruppe ist gelöscht. Ihre Kontakte bleiben.");
        return Redirect("/Contacts/Groups");
    }

    /// <summary>Adds the contacts chosen in the field (keys separated by commas).</summary>
    public async Task<IActionResult> OnPostAddAsync(string? key, string? add)
    {
        var keys = (add ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outcome = await ContactBook.SetMembersAsync(Db, Actor, key, keys, add: true, Ct);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash(outcome.Value == 1 ? "1 Mitglied hinzugefügt." : $"{outcome.Value} Mitglieder hinzugefügt.");
        }
        else
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
        }

        return RedirectToPage(new { key });
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? key, string[] keys)
    {
        var outcome = await ContactBook.SetMembersAsync(Db, Actor, key, keys, add: false, Ct);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash(outcome.Value == 1 ? "1 Mitglied aus der Gruppe genommen." : $"{outcome.Value} Mitglieder aus der Gruppe genommen.");
        return RedirectToPage(new { key });
    }

    private async Task<bool> LoadAsync(string? key)
    {
        var group = (await ContactBook.GroupsAsync(Db, Actor, Ct)).FirstOrDefault(g => g.Key == key);
        if (group is null)
        {
            return false;
        }

        Group = group;
        var members = group.Members.ToHashSet();
        Members = (await ContactBook.AllAsync(Db, Actor, Ct)).Where(c => members.Contains(c.Key))
            .OrderBy(c => ContactRules.SortKey(c.Data, ContactSort.GivenName), StringComparer.Ordinal).ToList();
        return true;
    }
}
