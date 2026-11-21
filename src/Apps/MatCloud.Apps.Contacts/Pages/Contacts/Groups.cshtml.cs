using MatCloud.Apps.Contacts.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>"Kontaktgruppen": the groups with their number of members; search and sort above, a new group below.</summary>
public sealed class GroupsModel : ContactsPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<GroupEntry> Groups { get; private set; } = [];

    [BindProperty] public string NewName { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        var all = await ContactBook.GroupsAsync(Db, Actor, Ct);
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Choice(Request, "sort", "name", "members") ?? "name";
        var descending = ListQuery.Descending(Request, sort == "members");
        IEnumerable<GroupEntry> list = all.Where(g => search is null || ContactRules.Fold(g.Name).Contains(ContactRules.Fold(search), StringComparison.Ordinal));
        list = (sort, descending) switch
        {
            ("members", true) => list.OrderByDescending(g => g.Members.Count),
            ("members", false) => list.OrderBy(g => g.Members.Count),
            (_, true) => list.OrderByDescending(g => ContactRules.Fold(g.Name), StringComparer.Ordinal),
            _ => list.OrderBy(g => ContactRules.Fold(g.Name), StringComparer.Ordinal),
        };
        Groups = list.ToList();
        Toolbar = new ToolbarModel
        {
            Id = "groups",
            Action = "/Contacts/Groups",
            Label = "Kontaktgruppen",
            Search = search,
            SearchPlaceholder = "Gruppen durchsuchen",
            SortOptions = [new SortOption("name", "Name"), new SortOption("members", "Mitglieder")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            AscendingLabel = "A–Z, wenige zuerst",
            DescendingLabel = "Z–A, viele zuerst",
            Total = all.Count,
            Matching = Groups.Count,
            Noun = "Gruppen",
            NounOne = "Gruppe",
        };
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var outcome = await ContactBook.SaveGroupAsync(Db, Actor, null, NewName, Ct);
        if (!outcome.Ok)
        {
            FlashError(outcome.Error ?? "Die Gruppe ließ sich nicht anlegen.");
            return RedirectToPage();
        }

        Flash("Die Gruppe ist angelegt – auch in ThixContacts.");
        return Redirect("/Contacts/Group?key=" + Uri.EscapeDataString(outcome.Value!));
    }
}
