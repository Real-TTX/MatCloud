using System.Security.Cryptography;
using System.Text;
using MatCloud.Contract;
using MatCloud.Contract.Contacts;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Contacts.Business;

/// <summary>A contact as the pages show it: its key, its data, the revision it has (a change made elsewhere since the page opened is noticed) and who wrote it last.</summary>
public sealed record ContactEntry(string Key, ContactData Data, long Revision, DateTime UpdatedDate, string? Source)
{
    public string Name => Data.NameToShow();

    /// <summary>"im Browser" or "auf ANNA-PC" (empty when unknown).</summary>
    public string Where => Source is null ? string.Empty : Source == Actor.WebSource ? "im Browser" : "auf " + Source;
}

/// <summary>A group with the members that exist (keys of contacts that are gone do not count).</summary>
public sealed record GroupEntry(string Key, ContactGroupData Data, long Revision, IReadOnlyList<string> Members)
{
    public string Name => Data.Name;
}

/// <summary>The query of the list: search, one group or category or the favourites, the order, a page.</summary>
public sealed record ContactQuery(string? Search = null, string? Group = null, string? Category = null, bool FavoritesOnly = false,
    ContactSort Sort = ContactSort.GivenName, bool Descending = false, int Offset = 0, int Limit = 50);

/// <summary>A page of the list: the contacts of the page, how many match, how many there are, the groups and categories (the filters) and the revision.</summary>
public sealed record ContactPage(IReadOnlyList<ContactEntry> Items, int Matching, int Total, IReadOnlyList<GroupEntry> Groups, IReadOnlyList<string> Categories, long Latest);

/// <summary>What an import did: how many contacts came, how many were left out as duplicates, how many rows were not contacts.</summary>
public sealed record ImportResult(int Imported, int Skipped);

/// <summary>
/// The contacts of ThixContacts in MatCloud – the business layer of the pages and of the module's API. ThixContacts keeps every contact as an item of the
/// general sync (app <c>contacts</c>, collection <see cref="ContactsContract.Contacts"/>, key = a GUID, data = <see cref="ContactData"/>) and every group in
/// <see cref="ContactsContract.Groups"/>; these functions read and write the same items through <see cref="SyncService"/>, so every device (and ThixMail)
/// sees a change of the browser at once and the rules are the same (the last change wins). Every function checks the actor first: the browser of the
/// account, or a device of an app the module lets in, and the app switched on.
/// </summary>
public static class ContactBook
{
    public const string App = CloudApps.Contacts;

    // ---- reading ----

    public static async Task<ContactPage> ListAsync(AppDbContext db, Actor actor, ContactQuery query, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        var contacts = await LoadAsync(db, actor.UserId, ct);
        var groups = Groups(await LoadGroupsAsync(db, actor.UserId, ct), contacts);
        IEnumerable<ContactEntry> filtered = contacts;
        if (query.FavoritesOnly)
        {
            filtered = filtered.Where(c => c.Data.IsFavorite);
        }

        if (!string.IsNullOrEmpty(query.Group))
        {
            var members = groups.FirstOrDefault(g => g.Key == query.Group)?.Members.ToHashSet() ?? [];
            filtered = filtered.Where(c => members.Contains(c.Key));
        }

        if (!string.IsNullOrEmpty(query.Category))
        {
            filtered = filtered.Where(c => c.Data.Categories.Contains(query.Category, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(c => ContactRules.Matches(c.Data, query.Search));
        }

        var sorted = query.Descending
            ? filtered.OrderByDescending(c => ContactRules.SortKey(c.Data, query.Sort), StringComparer.Ordinal)
            : filtered.OrderBy(c => ContactRules.SortKey(c.Data, query.Sort), StringComparer.Ordinal);
        var all = sorted.ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        var page = all.Skip(Math.Max(0, query.Offset)).Take(Math.Max(1, query.Limit)).ToList();
        return new ContactPage(page, all.Count, contacts.Count, groups, Categories(contacts), await SyncService.LatestAsync(db, actor.UserId, App, ct));
    }

    /// <summary>Every contact of the account (export, duplicates, the members of a group).</summary>
    public static async Task<IReadOnlyList<ContactEntry>> AllAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        return await LoadAsync(db, actor.UserId, ct);
    }

    public static async Task<Outcome<ContactEntry>> GetAsync(AppDbContext db, Actor actor, string? key, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        var row = string.IsNullOrEmpty(key) ? null : await Live(db, actor.UserId).Where(i => i.Collection == ContactsContract.Contacts && i.Key == key).FirstOrDefaultAsync(ct);
        return row is not null && ToEntry(row) is { } entry ? Outcome<ContactEntry>.Success(entry) : Outcome<ContactEntry>.Missing();
    }

    public static async Task<IReadOnlyList<GroupEntry>> GroupsAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        return Groups(await LoadGroupsAsync(db, actor.UserId, ct), await LoadAsync(db, actor.UserId, ct));
    }

    public static async Task<IReadOnlyList<string>> CategoriesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        return Categories(await LoadAsync(db, actor.UserId, ct));
    }

    /// <summary>The latest revision of the contacts (the live list waits for a higher one).</summary>
    public static async Task<long> WaitAsync(AppDbContext db, Actor actor, long since, TimeSpan timeout, CancellationToken ct)
    {
        await RequireAsync(db, actor, write: false, ct);
        return await SyncService.WaitAsync(db, actor, App, since, timeout, ct);
    }

    /// <summary>The sets of duplicates of the account, each as its contacts.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<ContactEntry>>> DuplicatesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        var contacts = (await LoadAsync(db, actor.UserId, ct)).OrderBy(c => ContactRules.SortKey(c.Data, ContactSort.GivenName), StringComparer.Ordinal).ToList();
        var byKey = contacts.ToDictionary(c => c.Key);
        return ContactDuplicates.Find(contacts.Select(c => (c.Key, c.Data)).ToList())
            .Select(set => (IReadOnlyList<ContactEntry>)set.Select(k => byKey[k]).ToList()).ToList();
    }

    // ---- writing ----

    /// <summary>
    /// Creates a contact (<paramref name="key"/> null) or changes one. The data is cleaned and checked (<see cref="ContactRules"/>); a picture must have been
    /// kept before (<see cref="ContactPhotos"/>). <paramref name="baseRevision"/>: the revision the page opened; when another device changed the contact since,
    /// nothing is saved.
    /// </summary>
    public static async Task<Outcome<string>> SaveAsync(AppDbContext db, Actor actor, ContactPhotoStore photos, string? key, ContactData data, long? baseRevision = null,
        CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var contact = ContactRules.Normalize(data);
        if (ContactRules.Validate(contact).FirstOrDefault() is { } problem)
        {
            return Outcome<string>.Fail(problem.Message, problem.Field);
        }

        if (contact.PhotoId.Length > 0 && !photos.Exists(actor.UserId, contact.PhotoId))
        {
            return Outcome<string>.Fail("Das Bild fehlt in der Cloud. Bitte noch einmal hochladen.", nameof(ContactData.PhotoId));
        }

        if (!string.IsNullOrEmpty(key))
        {
            var existing = await GetAsync(db, actor, key, ct);
            if (!existing.Ok)
            {
                return Outcome<string>.Missing();
            }

            if (baseRevision is { } revision && revision != existing.Value!.Revision)
            {
                return Outcome<string>.Fail("Der Kontakt wurde inzwischen auf einem anderen Gerät geändert. Deine Eingaben sind noch da: Speichern ersetzt die Änderung.", "Revision");
            }

            // what a newer app wrote stays
            contact = contact with { Extra = existing.Value!.Data.Extra };
        }

        key = string.IsNullOrEmpty(key) ? Guid.NewGuid().ToString() : key;
        await PushAsync(db, actor, [ContactItem(key, contact)], ct);
        return Outcome<string>.Success(key);
    }

    /// <summary>Deletes contacts on every device (tombstones); answers how many there were.</summary>
    public static async Task<int> DeleteAsync(AppDbContext db, Actor actor, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var existing = (await LoadAsync(db, actor.UserId, ct)).Select(c => c.Key).ToHashSet();
        var items = keys.Distinct().Where(existing.Contains)
            .Select(k => new SyncItemDto { Collection = ContactsContract.Contacts, Key = k, IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow }).ToList();
        await PushAsync(db, actor, items, ct);
        return items.Count;
    }

    public static async Task<Outcome> SetFavoriteAsync(AppDbContext db, Actor actor, string? key, bool favorite, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var found = await GetAsync(db, actor, key, ct);
        if (!found.Ok)
        {
            return Outcome.Missing();
        }

        await PushAsync(db, actor, [ContactItem(found.Value!.Key, found.Value.Data with { IsFavorite = favorite })], ct);
        return Outcome.Success();
    }

    /// <summary>"Kategorisieren": adds a category to contacts (<paramref name="add"/>) or takes it away from them.</summary>
    public static async Task<Outcome<int>> CategorizeAsync(AppDbContext db, Actor actor, IReadOnlyCollection<string> keys, string? category, bool add, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var name = (category ?? string.Empty).Trim();
        if (ContactRules.CategoryProblem(name) is { } problem)
        {
            return Outcome<int>.Fail(problem, "Category");
        }

        var items = new List<SyncItemDto>();
        foreach (var contact in (await LoadAsync(db, actor.UserId, ct)).Where(c => keys.Contains(c.Key)))
        {
            var has = contact.Data.Categories.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (has == add)
            {
                continue;
            }

            var categories = add ? contact.Data.Categories.Append(name).ToList() : contact.Data.Categories.Where(c => !c.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Add(ContactItem(contact.Key, contact.Data with { Categories = categories }));
        }

        await PushAsync(db, actor, items, ct);
        return Outcome<int>.Success(items.Count);
    }

    // ---- groups ----

    /// <summary>A new group or a new name; the name must not be taken by another group.</summary>
    public static async Task<Outcome<string>> SaveGroupAsync(AppDbContext db, Actor actor, string? key, string? name, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var clean = (name ?? string.Empty).Trim();
        if (ContactRules.CategoryProblem(clean) is { } problem)
        {
            return Outcome<string>.Fail(problem, "Name");
        }

        var groups = await LoadGroupsAsync(db, actor.UserId, ct);
        if (groups.Any(g => g.Key != key && g.Data.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)))
        {
            return Outcome<string>.Fail("Eine Gruppe mit diesem Namen gibt es schon.", "Name");
        }

        GroupEntry? existing = null;
        if (!string.IsNullOrEmpty(key))
        {
            existing = groups.FirstOrDefault(g => g.Key == key);
            if (existing is null)
            {
                return Outcome<string>.Missing();
            }
        }

        key = existing?.Key ?? Guid.NewGuid().ToString();
        await PushAsync(db, actor, [GroupItem(key, (existing?.Data ?? new ContactGroupData()) with { Name = clean })], ct);
        return Outcome<string>.Success(key);
    }

    public static async Task<Outcome> DeleteGroupAsync(AppDbContext db, Actor actor, string? key, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        if ((await LoadGroupsAsync(db, actor.UserId, ct)).All(g => g.Key != key))
        {
            return Outcome.Missing();
        }

        await PushAsync(db, actor, [new SyncItemDto { Collection = ContactsContract.Groups, Key = key!, IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow }], ct);
        return Outcome.Success();
    }

    /// <summary>Adds contacts to a group (<paramref name="add"/>) or takes them out; answers how many changed.</summary>
    public static async Task<Outcome<int>> SetMembersAsync(AppDbContext db, Actor actor, string? groupKey, IReadOnlyCollection<string> contactKeys, bool add, CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var group = (await LoadGroupsAsync(db, actor.UserId, ct)).FirstOrDefault(g => g.Key == groupKey);
        if (group is null)
        {
            return Outcome<int>.Missing();
        }

        var existing = (await LoadAsync(db, actor.UserId, ct)).Select(c => c.Key).ToHashSet();
        var members = group.Data.Members.ToList();
        var changed = 0;
        foreach (var key in contactKeys.Distinct())
        {
            if (add && existing.Contains(key) && !members.Contains(key))
            {
                members.Add(key);
                changed++;
            }
            else if (!add && members.Remove(key))
            {
                changed++;
            }
        }

        if (members.Count > ContactsContract.MaxGroupMembers)
        {
            return Outcome<int>.Fail($"Eine Gruppe hat höchstens {ContactsContract.MaxGroupMembers} Mitglieder.", "Members");
        }

        if (changed > 0)
        {
            await PushAsync(db, actor, [GroupItem(group.Key, group.Data with { Members = members })], ct);
        }

        return Outcome<int>.Success(changed);
    }

    // ---- duplicates ----

    /// <summary>
    /// Merges contacts into the first of <paramref name="keys"/> (<see cref="ContactDuplicates.Merge"/>; <paramref name="choices"/> names per field the key whose
    /// value is taken): the merged contact keeps the first key, the others are deleted, and the groups name the first instead of them.
    /// </summary>
    public static async Task<Outcome<string>> MergeAsync(AppDbContext db, Actor actor, IReadOnlyList<string> keys, IReadOnlyDictionary<string, string>? choices = null,
        CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var all = (await LoadAsync(db, actor.UserId, ct)).ToDictionary(c => c.Key);
        var contacts = keys.Distinct().Where(all.ContainsKey).Select(k => all[k]).ToList();
        if (contacts.Count < 2)
        {
            return Outcome<string>.Fail("Bitte mindestens zwei Kontakte zum Zusammenführen wählen.", "Keys");
        }

        var byIndex = choices?.Where(c => contacts.Any(x => x.Key == c.Value))
            .ToDictionary(c => c.Key, c => contacts.FindIndex(x => x.Key == c.Value));
        var merged = ContactDuplicates.Merge(contacts.Select(c => c.Data).ToList(), byIndex);
        var primary = contacts[0].Key;
        var others = contacts.Skip(1).Select(c => c.Key).ToHashSet();
        var items = new List<SyncItemDto> { ContactItem(primary, merged) };
        items.AddRange(others.Select(k => new SyncItemDto { Collection = ContactsContract.Contacts, Key = k, IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow }));
        foreach (var group in await LoadGroupsAsync(db, actor.UserId, ct))
        {
            if (!group.Data.Members.Any(others.Contains))
            {
                continue;
            }

            var members = group.Data.Members.Select(m => others.Contains(m) ? primary : m).Distinct().ToList();
            items.Add(GroupItem(group.Key, group.Data with { Members = members }));
        }

        await PushAsync(db, actor, items, ct);
        return Outcome<string>.Success(primary);
    }

    // ---- import and export ----

    /// <summary>
    /// Keeps contacts read from a file (vCard, CSV) with their pictures; <paramref name="skipDuplicates"/> leaves out a contact that would be a duplicate of
    /// one the account has (or of one imported before it).
    /// </summary>
    public static async Task<ImportResult> ImportAsync(AppDbContext db, Actor actor, ContactPhotoStore photos, IReadOnlyList<ImportedContact> contacts, bool skipDuplicates,
        CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: true, ct);
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (skipDuplicates)
        {
            foreach (var key in (await LoadAsync(db, actor.UserId, ct)).SelectMany(c => ContactDuplicates.Keys(c.Data)))
            {
                known.Add(key);
            }
        }

        var items = new List<SyncItemDto>();
        var skipped = 0;
        foreach (var imported in contacts)
        {
            var contact = ContactRules.Normalize(imported.Data);
            if (ContactRules.Validate(contact).Count > 0)
            {
                // a value the rules refuse (an e-mail without @) goes, the contact stays
                contact = Repair(contact);
                if (ContactRules.Validate(contact).Count > 0)
                {
                    skipped++;
                    continue;
                }
            }

            var keys = ContactDuplicates.Keys(contact).ToList();
            if (skipDuplicates && keys.Any(known.Contains))
            {
                skipped++;
                continue;
            }

            foreach (var key in keys)
            {
                known.Add(key);
            }

            if (imported.Photo is { Length: > 0 and <= ContactsContract.MaxPhotoBytes } photo && VCard.ImageTypeOf(photo) is not null)
            {
                contact = contact with { PhotoId = await photos.KeepAsync(actor.UserId, photo, ct) };
            }
            else
            {
                contact = contact with { PhotoId = string.Empty };
            }

            items.Add(ContactItem(Guid.NewGuid().ToString(), contact));
        }

        foreach (var batch in items.Chunk(SyncContract.MaxBatchSize))
        {
            await PushAsync(db, actor, batch, ct);
        }

        return new ImportResult(items.Count, skipped);
    }

    /// <summary>The contacts to write into a file (with their pictures): the keys given, or all when none are given; in the order of the list.</summary>
    public static async Task<IReadOnlyList<ExportedContact>> ExportAsync(AppDbContext db, Actor actor, ContactPhotoStore photos, IReadOnlyCollection<string>? keys,
        CancellationToken ct = default)
    {
        await RequireAsync(db, actor, write: false, ct);
        var contacts = (await LoadAsync(db, actor.UserId, ct))
            .Where(c => keys is null || keys.Count == 0 || keys.Contains(c.Key))
            .OrderBy(c => ContactRules.SortKey(c.Data, ContactSort.FamilyName), StringComparer.Ordinal);
        var result = new List<ExportedContact>();
        foreach (var contact in contacts)
        {
            var photo = contact.Data.PhotoId.Length > 0 ? await photos.ReadAsync(actor.UserId, contact.Data.PhotoId, ct) : null;
            result.Add(new ExportedContact(contact.Key, contact.Data, photo));
        }

        return result;
    }

    // ---- helpers ----

    /// <summary>Leaves out the values the rules refuse (imports: a broken e-mail address should not cost the whole contact).</summary>
    internal static ContactData Repair(ContactData contact) => contact with
    {
        Emails = contact.Emails.Where(e => ContactRules.IsEmail(e.Value) && e.Value.Length <= ContactsContract.MaxValueLength).Take(ContactsContract.MaxValues).ToList(),
        Phones = contact.Phones.Where(p => ContactRules.IsPhone(p.Value) && p.Value.Length <= ContactsContract.MaxValueLength).Take(ContactsContract.MaxValues).ToList(),
        Urls = contact.Urls.Where(u => ContactRules.IsUrl(u.Value) && u.Value.Length <= ContactsContract.MaxValueLength).Take(ContactsContract.MaxValues).ToList(),
        Addresses = contact.Addresses.Take(ContactsContract.MaxValues).ToList(),
        Categories = contact.Categories.Where(c => ContactRules.CategoryProblem(c) is null).Take(ContactsContract.MaxValues).ToList(),
        Birthday = ContactsContract.IsValidDate(contact.Birthday) ? contact.Birthday : string.Empty,
        Anniversary = ContactsContract.IsValidDate(contact.Anniversary) ? contact.Anniversary : string.Empty,
        Notes = contact.Notes.Length > ContactsContract.MaxNotesLength ? contact.Notes[..ContactsContract.MaxNotesLength] : contact.Notes,
        Prefix = Cut(contact.Prefix),
        GivenName = Cut(contact.GivenName),
        MiddleName = Cut(contact.MiddleName),
        FamilyName = Cut(contact.FamilyName),
        Suffix = Cut(contact.Suffix),
        Nickname = Cut(contact.Nickname),
        DisplayName = Cut(contact.DisplayName),
        Company = Cut(contact.Company),
        Department = Cut(contact.Department),
        JobTitle = Cut(contact.JobTitle),
    };

    private static string Cut(string text) => text.Length > ContactsContract.MaxNameLength ? text[..ContactsContract.MaxNameLength] : text;

    /// <summary>The browser of the account, or a device of ThixContacts or of an app the module lets in (reading or writing); the app must be switched on.</summary>
    internal static async Task RequireAsync(AppDbContext db, Actor actor, bool write, CancellationToken ct)
    {
        if (!(write ? actor.MayUse(App) : actor.MayRead(App)))
        {
            throw new NotAllowedException(write ? "Dieses Gerät darf die Kontakte nicht ändern." : "Dieses Gerät darf die Kontakte nicht lesen.");
        }

        if (!await AppService.IsEnabledAsync(db, App, ct))
        {
            throw new NotAllowedException(AppService.SwitchedOffText(App));
        }
    }

    private static IQueryable<SyncItem> Live(AppDbContext db, long userId)
        => db.SyncItems.AsNoTracking().Where(i => i.UserId == userId && i.App == App && i.UpdateState != SoftEntity.Deleted);

    private static async Task<List<ContactEntry>> LoadAsync(AppDbContext db, long userId, CancellationToken ct)
        => (await Live(db, userId).Where(i => i.Collection == ContactsContract.Contacts).ToListAsync(ct)).Select(ToEntry).OfType<ContactEntry>().ToList();

    private static async Task<List<GroupEntry>> LoadGroupsAsync(AppDbContext db, long userId, CancellationToken ct)
        => (await Live(db, userId).Where(i => i.Collection == ContactsContract.Groups).ToListAsync(ct))
            .Select(r => ContactsContract.ReadGroup(r.Data) is { } g
                ? new GroupEntry(r.Key, g with { Name = g.Name ?? string.Empty, Members = g.Members ?? [] }, r.Revision, g.Members ?? [])
                : null)
            .OfType<GroupEntry>()
            .OrderBy(g => ContactRules.Fold(g.Data.Name), StringComparer.Ordinal)
            .ToList();

    /// <summary>The groups with the members that exist.</summary>
    private static IReadOnlyList<GroupEntry> Groups(List<GroupEntry> groups, IReadOnlyList<ContactEntry> contacts)
    {
        var keys = contacts.Select(c => c.Key).ToHashSet();
        return groups.Select(g => g with { Members = g.Data.Members.Where(keys.Contains).Distinct().ToList() }).ToList();
    }

    private static IReadOnlyList<string> Categories(IEnumerable<ContactEntry> contacts)
        => contacts.SelectMany(c => c.Data.Categories).GroupBy(c => c.ToLowerInvariant()).Select(g => g.First())
            .OrderBy(ContactRules.Fold, StringComparer.Ordinal).ToList();

    internal static ContactEntry? ToEntry(SyncItem row)
        => ContactsContract.ReadContact(row.Data) is { } data
            ? new ContactEntry(row.Key, ContactRules.Normalize(data), row.Revision, row.ClientUpdateDate, row.Source) // (a device may have sent null lists)
            : null;

    private static SyncItemDto ContactItem(string key, ContactData contact)
    {
        var data = ContactsContract.Serialize(contact);
        return new SyncItemDto
        {
            Collection = ContactsContract.Contacts,
            Key = key,
            Data = data,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data))),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static SyncItemDto GroupItem(string key, ContactGroupData group) => new()
    {
        Collection = ContactsContract.Groups,
        Key = key,
        Data = ContactsContract.Serialize(group),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task PushAsync(AppDbContext db, Actor actor, IReadOnlyList<SyncItemDto> items, CancellationToken ct)
    {
        if (items.Count > 0)
        {
            await SyncService.PushAsync(db, actor, App, new SyncPushRequest(items), ct);
        }
    }
}
