using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>
/// "Kontakt": a new contact or one to change, in the tabs Allgemein (name, work, e-mail, phone), Details (addresses, web, dates, categories, groups) and
/// Notizen; the picture above them. Several e-mail addresses, numbers, addresses and web addresses, each with its label. Speichern · Zurück · gap · Löschen.
/// </summary>
public sealed class ContactModel : ContactsPageModel
{
    public ContactEntry? Existing { get; private set; }

    public IReadOnlyList<GroupEntry> AllGroups { get; private set; } = [];

    public IReadOnlyList<string> AllCategories { get; private set; } = [];

    /// <summary>The messages of the rows (<c>Emails[1]</c> → message).</summary>
    public Dictionary<string, string> RowErrors { get; } = new(StringComparer.Ordinal);

    public bool IsNew => Existing is null;

    [BindProperty] public string? Key { get; set; }

    [BindProperty] public long? Revision { get; set; }

    [BindProperty] public string Prefix { get; set; } = string.Empty;

    [BindProperty] public string GivenName { get; set; } = string.Empty;

    [BindProperty] public string MiddleName { get; set; } = string.Empty;

    [BindProperty] public string FamilyName { get; set; } = string.Empty;

    [BindProperty] public string Suffix { get; set; } = string.Empty;

    [BindProperty] public string DisplayName { get; set; } = string.Empty;

    [BindProperty] public string Nickname { get; set; } = string.Empty;

    [BindProperty] public string Company { get; set; } = string.Empty;

    [BindProperty] public string Department { get; set; } = string.Empty;

    [BindProperty] public string JobTitle { get; set; } = string.Empty;

    [BindProperty] public List<string> EmailLabels { get; set; } = [];

    [BindProperty] public List<string> EmailValues { get; set; } = [];

    [BindProperty] public List<string> PhoneLabels { get; set; } = [];

    [BindProperty] public List<string> PhoneValues { get; set; } = [];

    [BindProperty] public List<string> UrlLabels { get; set; } = [];

    [BindProperty] public List<string> UrlValues { get; set; } = [];

    [BindProperty] public List<string> AddressLabels { get; set; } = [];

    [BindProperty] public List<string> AddressStreets { get; set; } = [];

    [BindProperty] public List<string> AddressPostals { get; set; } = [];

    [BindProperty] public List<string> AddressCities { get; set; } = [];

    [BindProperty] public List<string> AddressRegions { get; set; } = [];

    [BindProperty] public List<string> AddressCountries { get; set; } = [];

    [BindProperty] public string Birthday { get; set; } = string.Empty;

    [BindProperty] public string Anniversary { get; set; } = string.Empty;

    [BindProperty] public string Notes { get; set; } = string.Empty;

    /// <summary>The categories, separated by commas (the tags field).</summary>
    [BindProperty] public string Categories { get; set; } = string.Empty;

    [BindProperty] public List<string> Groups { get; set; } = [];

    [BindProperty] public bool IsFavorite { get; set; }

    [BindProperty] public string PhotoId { get; set; } = string.Empty;

    [BindProperty] public bool RemovePhoto { get; set; }

    [BindProperty] public IFormFile? Photo { get; set; }

    public async Task<IActionResult> OnGetAsync(string? key, string? group)
    {
        if (!await LoadAsync(key))
        {
            return NotFound();
        }

        if (Existing is null)
        {
            if (AllGroups.Any(g => g.Key == group))
            {
                Groups = [group!];
            }

            return Page();
        }

        var c = Existing.Data;
        Revision = Existing.Revision;
        (Prefix, GivenName, MiddleName, FamilyName, Suffix, DisplayName, Nickname) = (c.Prefix, c.GivenName, c.MiddleName, c.FamilyName, c.Suffix, c.DisplayName, c.Nickname);
        (Company, Department, JobTitle) = (c.Company, c.Department, c.JobTitle);
        EmailLabels = c.Emails.Select(e => e.Label).ToList();
        EmailValues = c.Emails.Select(e => e.Value).ToList();
        PhoneLabels = c.Phones.Select(e => e.Label).ToList();
        PhoneValues = c.Phones.Select(e => e.Value).ToList();
        UrlLabels = c.Urls.Select(e => e.Label).ToList();
        UrlValues = c.Urls.Select(e => e.Value).ToList();
        AddressLabels = c.Addresses.Select(a => a.Label).ToList();
        AddressStreets = c.Addresses.Select(a => a.Street).ToList();
        AddressPostals = c.Addresses.Select(a => a.PostalCode).ToList();
        AddressCities = c.Addresses.Select(a => a.City).ToList();
        AddressRegions = c.Addresses.Select(a => a.Region).ToList();
        AddressCountries = c.Addresses.Select(a => a.Country).ToList();
        Birthday = ContactsUi.DateInput(c.Birthday);
        Anniversary = ContactsUi.DateInput(c.Anniversary);
        Notes = c.Notes;
        Categories = string.Join(", ", c.Categories);
        Groups = AllGroups.Where(g => g.Members.Contains(Existing.Key)).Select(g => g.Key).ToList();
        IsFavorite = c.IsFavorite;
        PhotoId = c.PhotoId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync(Key))
        {
            return NotFound();
        }

        var birthday = ContactsUi.ParseDateInput(Birthday);
        var anniversary = ContactsUi.ParseDateInput(Anniversary);
        if (birthday is null)
        {
            AddError(nameof(Birthday), "Bitte ein Datum wie 31.12.1980 eingeben (ohne Jahr: 31.12.).");
        }

        if (anniversary is null)
        {
            AddError(nameof(Anniversary), "Bitte ein Datum wie 31.12.1980 eingeben (ohne Jahr: 31.12.).");
        }

        var photoId = RemovePhoto ? string.Empty : PhotoId;
        if (Photo is { Length: > 0 })
        {
            using var stream = new MemoryStream();
            await Photo.CopyToAsync(stream, Ct);
            var uploaded = await ContactPhotos.UploadAsync(Db, Actor, Photos, stream.ToArray(), Ct);
            if (!uploaded.Ok)
            {
                AddError(nameof(Photo), uploaded.Error);
            }
            else
            {
                photoId = uploaded.Value!.Id;
                PhotoId = photoId;
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var data = (Existing?.Data ?? new ContactData()) with
        {
            Prefix = Prefix,
            GivenName = GivenName,
            MiddleName = MiddleName,
            FamilyName = FamilyName,
            Suffix = Suffix,
            DisplayName = DisplayName,
            Nickname = Nickname,
            Company = Company,
            Department = Department,
            JobTitle = JobTitle,
            Emails = Values(EmailLabels, EmailValues),
            Phones = Values(PhoneLabels, PhoneValues),
            Urls = Values(UrlLabels, UrlValues),
            Addresses = Enumerable.Range(0, AddressLabels.Count).Select(i => new ContactAddress
            {
                Label = AddressLabels[i],
                Street = At(AddressStreets, i),
                PostalCode = At(AddressPostals, i),
                City = At(AddressCities, i),
                Region = At(AddressRegions, i),
                Country = At(AddressCountries, i),
            }).ToList(),
            Birthday = birthday!,
            Anniversary = anniversary!,
            Notes = Notes,
            Categories = Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsFavorite = IsFavorite,
            PhotoId = photoId,
        };

        var outcome = await ContactBook.SaveAsync(Db, Actor, Photos, Existing?.Key, data, Revision, Ct);
        if (!outcome.Ok)
        {
            if (outcome.NotFound)
            {
                return NotFound();
            }

            ShowProblem(outcome.Field, outcome.Error);
            if (outcome.Field == "Revision")
            {
                Revision = Existing?.Revision;
            }

            return Page();
        }

        // the groups: in the chosen ones, out of the others
        var key = outcome.Value!;
        foreach (var group in AllGroups)
        {
            var member = group.Members.Contains(key);
            var wanted = Groups.Contains(group.Key);
            if (member != wanted)
            {
                await ContactBook.SetMembersAsync(Db, Actor, group.Key, [key], wanted, Ct);
            }
        }

        Flash(IsNew ? "Der Kontakt ist angelegt – auch in ThixContacts." : "Der Kontakt ist gespeichert – auch in ThixContacts.");
        return Redirect("/Contacts/Contact?key=" + Uri.EscapeDataString(key));
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync(Key) || Existing is null)
        {
            return NotFound();
        }

        await ContactBook.DeleteAsync(Db, Actor, [Existing.Key], Ct);
        Flash($"„{Existing.Name}“ ist gelöscht – auch in ThixContacts.");
        return Redirect("/Contacts");
    }

    /// <summary>The rows of a kind with something in them (a label without a value is an empty row).</summary>
    private static List<ContactValue> Values(List<string> labels, List<string> values)
        => Enumerable.Range(0, Math.Min(labels.Count, values.Count)).Select(i => new ContactValue(labels[i], values[i] ?? string.Empty)).ToList();

    private static string At(List<string> list, int index) => index < list.Count ? list[index] ?? string.Empty : string.Empty;

    /// <summary>A message of the business layer at its field: a row ("Emails[1]") counts the rows with something in them, as the business layer did.</summary>
    private void ShowProblem(string? field, string? message)
    {
        if (field is not null && field.Contains('['))
        {
            var kind = field[..field.IndexOf('[')];
            var index = int.Parse(field[(field.IndexOf('[') + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
            var values = kind switch
            {
                "Emails" => EmailValues,
                "Phones" => PhoneValues,
                "Urls" => UrlValues,
                _ => AddressStreets,
            };

            // the business layer counts only the rows that are not empty
            var row = -1;
            for (int i = 0, filled = -1; i < values.Count; i++)
            {
                if (kind == "Addresses" || !string.IsNullOrWhiteSpace(values[i]))
                {
                    filled++;
                }

                if (filled == index)
                {
                    row = i;
                    break;
                }
            }

            RowErrors[$"{kind}[{Math.Max(row, 0)}]"] = message ?? "Bitte prüfen.";
            AddError(null, message);
            return;
        }

        AddError(field, message);
    }

    private async Task<bool> LoadAsync(string? key)
    {
        AllGroups = await ContactBook.GroupsAsync(Db, Actor, Ct);
        AllCategories = await ContactBook.CategoriesAsync(Db, Actor, Ct);
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        var found = await ContactBook.GetAsync(Db, Actor, key, Ct);
        Existing = found.Value;
        Key = key;
        return found.Ok;
    }
}
