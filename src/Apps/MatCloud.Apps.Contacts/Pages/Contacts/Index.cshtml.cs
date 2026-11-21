using System.Text;
using MatCloud.Apps.Contacts.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>
/// "Kontakte": the list (Outlook's "Personen"): search, filter (Alle | Favoriten, a group, a category), sort (Vorname, Nachname, Firma) above, the letters
/// of the alphabet to jump, the rows with their selection, the pages below, then the actions: new, import, export, categorize, add to a group, merge, delete.
/// </summary>
public sealed class IndexModel : ContactsPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel? Pager { get; private set; }

    public ContactPage Result { get; private set; } = null!;

    public ContactSort Sort { get; private set; }

    /// <summary>The letters of the index with the page each starts on (only letters that have contacts).</summary>
    public IReadOnlyList<(string Letter, int Page)> Letters { get; private set; } = [];

    public string Here => Request.Path + Request.QueryString;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var view = ListQuery.Choice(Request, "show", "favorites");
        var group = ListQuery.Text(Request, "group");
        var category = ListQuery.Text(Request, "category");
        var sortValue = ListQuery.Choice(Request, "sort", "given", "family", "company") ?? "given";
        Sort = sortValue switch { "family" => ContactSort.FamilyName, "company" => ContactSort.Company, _ => ContactSort.GivenName };
        var descending = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);

        var query = new ContactQuery(search, group, category, view == "favorites", Sort, descending, (page - 1) * PageSize, PageSize);
        Result = await ContactBook.ListAsync(Db, Actor, query, Ct);
        if (Result.Items.Count == 0 && page > 1)
        {
            page = 1;
            Result = await ContactBook.ListAsync(Db, Actor, query with { Offset = 0 }, Ct);
        }

        Toolbar = new ToolbarModel
        {
            Id = "contacts",
            Action = "/Contacts",
            Label = "Kontakte",
            Search = search,
            SearchPlaceholder = "Name, E-Mail, Telefon, Firma",
            Filters =
            [
                new FilterField("show", "Anzeigen", [new FilterOption("favorites", "Favoriten")], view),
                new FilterField("group", "Gruppe", Result.Groups.Select(g => new FilterOption(g.Key, g.Name)).ToList(), Result.Groups.Any(g => g.Key == group) ? group : null),
                new FilterField("category", "Kategorie", Result.Categories.Select(c => new FilterOption(c, c)).ToList(), category),
            ],
            SortOptions = [new SortOption("given", "Vorname"), new SortOption("family", "Nachname"), new SortOption("company", "Firma")],
            Sort = sortValue,
            Descending = descending,
            DefaultSort = "given",
            DefaultDescending = false,
            AscendingLabel = "A–Z",
            DescendingLabel = "Z–A",
            Total = Result.Total,
            Matching = Result.Matching,
            Noun = "Kontakte",
            NounOne = "Kontakt",
        };
        Pager = Result.Matching <= PageSize ? null : new PagerModel { Page = page, PageSize = PageSize, Total = Result.Matching, Url = Toolbar.UrlWithPage };
        if (!descending && Result.Matching > PageSize)
        {
            // the letters of the whole filtered list, each with the page it starts on
            var all = await ContactBook.ListAsync(Db, Actor, query with { Offset = 0, Limit = int.MaxValue }, Ct);
            var letters = new List<(string, int)>();
            for (var i = 0; i < all.Items.Count; i++)
            {
                var letter = ContactRules.IndexLetter(all.Items[i].Data, Sort);
                if (letters.All(l => l.Item1 != letter))
                {
                    letters.Add((letter, i / PageSize + 1));
                }
            }

            Letters = letters;
        }
    }

    // ---- the actions on the selection ----

    public async Task<IActionResult> OnPostDeleteAsync(string[] keys, string? returnUrl)
    {
        var deleted = await ContactBook.DeleteAsync(Db, Actor, keys, Ct);
        Flash(deleted == 1 ? "1 Kontakt gelöscht – auch auf deinen Geräten." : $"{deleted} Kontakte gelöscht – auch auf deinen Geräten.");
        return Back(returnUrl, "/Contacts");
    }

    public async Task<IActionResult> OnPostFavoriteAsync(string key, bool on, string? returnUrl)
    {
        await ContactBook.SetFavoriteAsync(Db, Actor, key, on, Ct);
        return Back(returnUrl, "/Contacts");
    }

    public async Task<IActionResult> OnPostCategorizeAsync(string[] keys, string? category, bool remove, string? returnUrl)
    {
        var outcome = await ContactBook.CategorizeAsync(Db, Actor, keys, category, !remove, Ct);
        if (outcome.Ok)
        {
            Flash(remove ? $"Kategorie „{category?.Trim()}“ bei {outcome.Value} Kontakten entfernt." : $"Kategorie „{category?.Trim()}“ bei {outcome.Value} Kontakten gesetzt.");
        }
        else
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
        }

        return Back(returnUrl, "/Contacts");
    }

    public async Task<IActionResult> OnPostAddToGroupAsync(string[] keys, string? group, string? newGroup, string? returnUrl)
    {
        if (string.IsNullOrEmpty(group) && !string.IsNullOrWhiteSpace(newGroup))
        {
            var created = await ContactBook.SaveGroupAsync(Db, Actor, null, newGroup, Ct);
            if (!created.Ok)
            {
                FlashError(created.Error ?? "Die Gruppe ließ sich nicht anlegen.");
                return Back(returnUrl, "/Contacts");
            }

            group = created.Value;
        }

        var outcome = await ContactBook.SetMembersAsync(Db, Actor, group, keys, add: true, Ct);
        if (outcome.Ok)
        {
            Flash(outcome.Value == 1 ? "1 Kontakt zur Gruppe hinzugefügt." : $"{outcome.Value} Kontakte zur Gruppe hinzugefügt.");
        }
        else
        {
            FlashError(outcome.Error ?? "Bitte eine Gruppe wählen.");
        }

        return Back(returnUrl, "/Contacts");
    }

    /// <summary>Exports the selection (or, without one, every contact) as vCard 3.0, vCard 4.0 or CSV.</summary>
    public async Task<IActionResult> OnPostExportAsync(string[] keys, string? format)
    {
        var contacts = await ContactBook.ExportAsync(Db, Actor, Photos, keys, Ct);
        var stamp = Dates.Local(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return format switch
        {
            "csv" => File(ContactCsv.Write(contacts.Select(c => c.Data)), "text/csv; charset=utf-8", $"Kontakte {stamp}.csv"),
            "vcf4" => File(Encoding.UTF8.GetBytes(VCard.Write(contacts, 4)), "text/vcard; charset=utf-8", $"Kontakte {stamp}.vcf"),
            _ => File(Encoding.UTF8.GetBytes(VCard.Write(contacts, 3)), "text/vcard; charset=utf-8", $"Kontakte {stamp}.vcf"),
        };
    }
}
