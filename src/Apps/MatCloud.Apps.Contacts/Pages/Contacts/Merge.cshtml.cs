using MatCloud.Apps.Contacts.Business;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>
/// "Zusammenführen": the contacts side by side, field by field; per field the person picks whose value the merged contact takes (by default the first that
/// has one). E-mail addresses, numbers, addresses, web addresses and categories are joined. One contact is kept (its groups too), the others go.
/// </summary>
public sealed class MergeModel : ContactsPageModel
{
    public IReadOnlyList<ContactEntry> Contacts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string[] keys)
    {
        await LoadAsync(keys);
        if (Contacts.Count < 2)
        {
            FlashError("Bitte mindestens zwei Kontakte zum Zusammenführen wählen.");
            return Redirect("/Contacts");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string[] keys, string? keep)
    {
        await LoadAsync(keys);
        var order = Contacts.Select(c => c.Key).ToList();
        if (keep is not null && order.Remove(keep))
        {
            order.Insert(0, keep);
        }

        var choices = ContactDuplicates.Fields
            .Select(f => (f.Id, Value: Request.Form["field-" + f.Id].ToString()))
            .Where(c => c.Value.Length > 0)
            .ToDictionary(c => c.Id, c => c.Value);
        var outcome = await ContactBook.MergeAsync(Db, Actor, order, choices, Ct);
        if (!outcome.Ok)
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
            return Redirect("/Contacts/Duplicates");
        }

        Flash($"{Contacts.Count} Kontakte zu einem zusammengeführt – auch in ThixContacts.");
        return Redirect("/Contacts/Contact?key=" + Uri.EscapeDataString(outcome.Value!));
    }

    private async Task LoadAsync(string[] keys)
    {
        var all = (await ContactBook.AllAsync(Db, Actor, Ct)).ToDictionary(c => c.Key);
        Contacts = keys.Distinct().Where(all.ContainsKey).Select(k => all[k]).ToList();
    }
}
