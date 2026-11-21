using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Contract.Contacts;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MatCloud.Tests;

/// <summary>
/// ThixContacts in MatCloud against a real PostgreSQL (MATCLOUD_TEST_DB): the business layer of the pages on the items of the general sync, the grants
/// (ThixMail reads and writes contacts and calendar, ThixCalendar only reads the contacts, ThixWrite reaches neither), the pictures and the pages.
/// </summary>
public sealed class ContactsTests
{
    private static Actor Browser(User user) => new(user.Id, user.IsAdmin);

    private static Actor Device(User user, string app = CloudApps.Contacts) => new(user.Id, user.IsAdmin, 7, "ANNA-PC", app);

    private static ContactPhotoStore Store() => new(Directory.CreateTempSubdirectory("matcloud-photos-").FullName);

    [DbFact]
    public async Task A_contact_saved_in_the_browser_reaches_the_devices_and_a_stale_change_is_refused()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = Store();

        var refused = await ContactBook.SaveAsync(test.Db, Browser(anna), store, null, new ContactData { Notes = "nichts" });
        Assert.Equal("GivenName", refused.Field);
        Assert.Equal("Emails[0]", (await ContactBook.SaveAsync(test.Db, Browser(anna), store, null,
            new ContactData { GivenName = "A", Emails = [new ContactValue("work", "kaputt")] })).Field);

        var saved = await ContactBook.SaveAsync(test.Db, Browser(anna), store, null, new ContactData
        {
            GivenName = " Anna ", FamilyName = "Müller", Emails = [new ContactValue("Work", "anna@x.de"), new ContactValue("home", " ")],
        });
        Assert.True(saved.Ok, saved.Error);

        var changes = await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Contacts, 0, 100);
        var item = Assert.Single(changes.Items);
        Assert.Equal((ContactsContract.Contacts, saved.Value, "Web"), (item.Collection, item.Key, item.Source));
        var data = ContactsContract.ReadContact(item.Data)!;
        Assert.Equal(("Anna", "work"), (data.GivenName, data.Emails.Single().Label));

        // a device changes it; the page that opened the old revision is refused
        var entry = (await ContactBook.GetAsync(test.NewContext(), Browser(anna), saved.Value)).Value!;
        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Contacts, new SyncPushRequest([
            new SyncItemDto { Collection = ContactsContract.Contacts, Key = saved.Value!, Data = ContactsContract.Serialize(data with { JobTitle = "Chefin" }), UpdatedAt = DateTimeOffset.UtcNow },
        ]));
        var stale = await ContactBook.SaveAsync(test.NewContext(), Browser(anna), store, saved.Value, data with { Nickname = "Anni" }, entry.Revision);
        Assert.Equal("Revision", stale.Field);

        // favourite, category, delete = a tombstone
        Assert.True((await ContactBook.SetFavoriteAsync(test.NewContext(), Browser(anna), saved.Value, true)).Ok);
        Assert.Equal(1, (await ContactBook.CategorizeAsync(test.NewContext(), Browser(anna), [saved.Value!], "Kunde", true)).Value);
        var listed = await ContactBook.ListAsync(test.NewContext(), Browser(anna), new ContactQuery(FavoritesOnly: true, Category: "kunde"));
        Assert.Equal("Chefin", listed.Items.Single().Data.JobTitle);
        Assert.Equal(["Kunde"], listed.Categories);
        Assert.Equal(1, await ContactBook.DeleteAsync(test.NewContext(), Browser(anna), [saved.Value!, "unknown"]));
        Assert.True((await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Contacts, 0, 100)).Items.Single().IsDeleted);
    }

    [DbFact]
    public async Task The_list_searches_filters_sorts_and_pages()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = Store();
        foreach (var (given, family, company) in new[] { ("Zoe", "Adler", "Zeta"), ("Ärne", "Berg", "Alpha"), ("Bert", "Zander", ""), ("Carl", "Müller", "Beta") })
        {
            Assert.True((await ContactBook.SaveAsync(test.NewContext(), Browser(anna), store, null, new ContactData { GivenName = given, FamilyName = family, Company = company })).Ok);
        }

        async Task<string[]> Names(ContactQuery query) => (await ContactBook.ListAsync(test.NewContext(), Browser(anna), query)).Items.Select(c => c.Data.GivenName).ToArray();
        Assert.Equal(["Ärne", "Bert", "Carl", "Zoe"], await Names(new ContactQuery()));
        Assert.Equal(["Zoe", "Ärne", "Carl", "Bert"], await Names(new ContactQuery(Sort: ContactSort.FamilyName)));
        Assert.Equal(["Ärne", "Bert", "Carl", "Zoe"], await Names(new ContactQuery(Sort: ContactSort.Company, Descending: false)));
        Assert.Equal(["Zoe", "Carl", "Bert", "Ärne"], await Names(new ContactQuery(Sort: ContactSort.Company, Descending: true)));
        Assert.Equal(["Carl"], await Names(new ContactQuery("muller")));
        Assert.Equal(["Bert", "Carl"], await Names(new ContactQuery(Offset: 1, Limit: 2)));

        var group = await ContactBook.SaveGroupAsync(test.NewContext(), Browser(anna), null, "Team");
        Assert.Equal("Name", (await ContactBook.SaveGroupAsync(test.NewContext(), Browser(anna), null, "team")).Field);
        var all = await ContactBook.AllAsync(test.NewContext(), Browser(anna));
        Assert.Equal(2, (await ContactBook.SetMembersAsync(test.NewContext(), Browser(anna), group.Value, all.Take(2).Select(c => c.Key).Append("gone").ToList(), true)).Value);
        Assert.Equal(2, (await ContactBook.ListAsync(test.NewContext(), Browser(anna), new ContactQuery(Group: group.Value))).Matching);
        Assert.Equal(2, (await ContactBook.GroupsAsync(test.NewContext(), Browser(anna))).Single().Members.Count);
    }

    [DbFact]
    public async Task Duplicates_are_merged_into_one_contact_that_keeps_its_groups()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = Store();
        var first = (await ContactBook.SaveAsync(test.Db, Browser(anna), store, null, new ContactData { GivenName = "Max", FamilyName = "Muster", Emails = [new ContactValue("work", "max@x.de")] })).Value!;
        var second = (await ContactBook.SaveAsync(test.NewContext(), Browser(anna), store, null, new ContactData { GivenName = "Maximilian", Emails = [new ContactValue("home", "MAX@x.de")], JobTitle = "Koch" })).Value!;
        await ContactBook.SaveAsync(test.NewContext(), Browser(anna), store, null, new ContactData { GivenName = "Erika" });
        var group = (await ContactBook.SaveGroupAsync(test.NewContext(), Browser(anna), null, "Küche")).Value!;
        await ContactBook.SetMembersAsync(test.NewContext(), Browser(anna), group, [second], true);

        var sets = await ContactBook.DuplicatesAsync(test.NewContext(), Browser(anna));
        Assert.Equal(new[] { first, second }.Order(), Assert.Single(sets).Select(c => c.Key).Order());

        var merged = await ContactBook.MergeAsync(test.NewContext(), Browser(anna), [first, second], new Dictionary<string, string> { ["name"] = second });
        Assert.Equal(first, merged.Value);
        var contact = (await ContactBook.GetAsync(test.NewContext(), Browser(anna), first)).Value!.Data;
        Assert.Equal(("Maximilian", "Koch", 1), (contact.GivenName, contact.JobTitle, contact.Emails.Count));
        Assert.True((await ContactBook.GetAsync(test.NewContext(), Browser(anna), second)).NotFound);
        Assert.Equal([first], (await ContactBook.GroupsAsync(test.NewContext(), Browser(anna))).Single().Members);
        Assert.Empty(await ContactBook.DuplicatesAsync(test.NewContext(), Browser(anna)));
    }

    [DbFact]
    public async Task A_vcard_with_pictures_is_imported_without_duplicates_and_exported_again()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = Store();
        var file = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contacts", "outlook-3.0.vcf"));
        var result = await ContactBook.ImportAsync(test.Db, Browser(anna), store, VCard.Read(file), skipDuplicates: true);
        Assert.Equal(new ImportResult(2, 0), result);
        Assert.Equal(new ImportResult(0, 2), await ContactBook.ImportAsync(test.NewContext(), Browser(anna), store, VCard.Read(file), skipDuplicates: true));

        var exported = await ContactBook.ExportAsync(test.NewContext(), Browser(anna), store, null);
        var anne = exported.Single(c => c.Data.GivenName == "Anna");
        Assert.NotNull(anne.Photo);
        Assert.True(store.Exists(anna.Id, anne.Data.PhotoId));
        var again = VCard.Read(VCard.Write(exported));
        Assert.Equal(anne.Data.PhotoId, again.Single(c => c.Data.GivenName == "Anna").Data.PhotoId);

        // a picture nobody names goes after a day; the ones that are named stay
        var unused = await store.KeepAsync(anna.Id, [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 1], CancellationToken.None);
        File.SetLastWriteTimeUtc(store.PathOf(anna.Id, unused), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(store.PathOf(anna.Id, anne.Data.PhotoId), DateTime.UtcNow.AddDays(-2));
        Assert.Equal(1, await ContactHousekeeping.MaintainAsync(test.NewContext(), store, CancellationToken.None));
        Assert.False(store.Exists(anna.Id, unused));
        Assert.True(store.Exists(anna.Id, anne.Data.PhotoId));
    }

    [DbFact]
    public async Task The_grants_mail_reads_and_writes_contacts_and_calendar_calendar_only_reads_contacts_others_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        var ct = CancellationToken.None;
        using var contacts = await ClientAsync(app, CloudApps.Contacts);
        using var mail = await ClientAsync(app, CloudApps.Mail);
        using var calendar = await ClientAsync(app, CloudApps.Calendar);
        using var write = await ClientAsync(app, CloudApps.Write);

        var item = new SyncItemDto { Collection = ContactsContract.Contacts, Key = "k1", Data = ContactsContract.Serialize(new ContactData { GivenName = "Anna" }), UpdatedAt = DateTimeOffset.UtcNow };
        await contacts.PushAsync(new SyncPushRequest([item]), ct);

        // ThixMail: the contacts and the calendar of its account, both ways
        Assert.Equal("k1", (await mail.GetChangesAsync(CloudApps.Contacts, 0, 100, ct)).Items.Single().Key);
        var pushed = await mail.PushAsync(CloudApps.Contacts, new SyncPushRequest([item with { Key = "k2", UpdatedAt = DateTimeOffset.UtcNow }]), ct);
        Assert.True(pushed.Results.Single().Applied);
        Assert.Equal(2, (await contacts.GetChangesAsync(0, 100, ct)).Items.Count);
        await mail.PushAsync(CloudApps.Calendar, new SyncPushRequest([new SyncItemDto { Collection = CalendarContract.Events, Key = "e1", Data = "{}", UpdatedAt = DateTimeOffset.UtcNow }]), ct);
        Assert.Equal("e1", (await calendar.GetChangesAsync(0, 100, ct)).Items.Single().Key);
        Assert.True((await mail.WaitAsync(CloudApps.Contacts, 0, 1, ct)).Latest > 0);
        Assert.Empty((await mail.GetChangesAsync(0, 100, ct)).Items); // its own general sync is still its own

        // ThixCalendar: reads the contacts, never writes them
        Assert.Equal(2, (await calendar.GetChangesAsync(CloudApps.Contacts, 0, 100, ct)).Items.Count);
        var readOnly = await Assert.ThrowsAsync<CloudException>(() => calendar.PushAsync(CloudApps.Contacts, new SyncPushRequest([item with { Key = "k3" }]), ct));
        Assert.True(readOnly.IsForbidden);

        // ThixWrite: neither contacts nor calendar; ThixContacts: not the calendar
        foreach (var (client, target) in new[] { (write, CloudApps.Contacts), (write, CloudApps.Calendar), (contacts, CloudApps.Calendar), (calendar, CloudApps.Mail) })
        {
            var refused = await Assert.ThrowsAsync<CloudException>(() => client.GetChangesAsync(target, 0, 100, ct));
            Assert.True(refused.IsForbidden, target);
        }

        Assert.True((await Assert.ThrowsAsync<CloudException>(() => write.UploadContactPhotoAsync(Samples.Picture(8, 8), "image/png", ct))).IsForbidden);
        Assert.True((await Assert.ThrowsAsync<CloudException>(() => write.DownloadContactPhotoAsync(new string('a', 64), ct))).IsForbidden);

        // pictures: ThixContacts and ThixMail upload, ThixCalendar downloads, nothing that is not a picture
        var photo = Samples.Picture(16, 16);
        var info = await contacts.UploadContactPhotoAsync(photo, "image/png", ct);
        Assert.Equal((64, "image/png", (long)photo.Length), (info.Id.Length, info.ContentType, info.Size));
        Assert.Equal(info.Id, (await mail.UploadContactPhotoAsync(photo, "image/png", ct)).Id);
        Assert.Equal(photo, await calendar.DownloadContactPhotoAsync(info.Id, ct));
        Assert.Null(await calendar.DownloadContactPhotoAsync(new string('b', 64), ct));
        Assert.True((await Assert.ThrowsAsync<CloudException>(() => calendar.UploadContactPhotoAsync(photo, "image/png", ct))).IsForbidden);
        Assert.Equal("photo", (await Assert.ThrowsAsync<CloudException>(() => contacts.UploadContactPhotoAsync("kein Bild"u8.ToArray(), "image/png", ct))).Field);

        // the app switched off: the guests lose it too
        var admin = await test.AddUserAsync("root", admin: true);
        await AppService.SetEnabledAsync(test.NewContext(), new Actor(admin.Id, true), CloudApps.Contacts, false);
        AppService.Forget();
        Assert.True((await Assert.ThrowsAsync<CloudException>(() => mail.GetChangesAsync(CloudApps.Contacts, 0, 100, ct))).IsForbidden);
        Assert.Equal("e1", (await mail.GetChangesAsync(CloudApps.Calendar, 0, 100, ct)).Items.Single().Key);
    }

    [DbFact]
    public async Task The_pages_list_edit_import_and_export_contacts()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        var browser = await SignInAsync(app);

        var empty = await browser.GetStringAsync("/Contacts");
        Assert.Contains("Noch keine Kontakte", empty);
        Assert.Contains("data-app=\"thixcontacts\"", empty);

        // a new contact through the form
        var form = await browser.GetStringAsync("/Contacts/Contact");
        var created = await browser.PostAsync("/Contacts/Contact", Form(form,
            ("GivenName", "Anna"), ("FamilyName", "Müller"), ("EmailLabels", "work"), ("EmailValues", "anna@x.de"), ("PhoneLabels", "mobile"),
            ("PhoneValues", "0171 1"), ("Birthday", "1.5.1980"), ("Categories", "Kunde, Wichtig"), ("Notes", "Notiz")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var key = Regex.Match(created.Headers.Location!.OriginalString, "key=([^&]+)").Groups[1].Value;

        var page = await browser.GetStringAsync("/Contacts/Contact?key=" + key);
        Assert.Contains("01.05.1980", page);
        Assert.Contains("anna@x.de", page);

        // a wrong e-mail address is shown at its row
        var wrong = await browser.PostAsync("/Contacts/Contact", Form(page, ("Key", key), ("GivenName", "Anna"), ("EmailLabels", "work"), ("EmailValues", "kaputt")));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("ist keine E-Mail-Adresse", await wrong.Content.ReadAsStringAsync());

        // the list, the groups, the duplicates, the import page
        var list = await browser.GetStringAsync("/Contacts?q=müller");
        Assert.Contains("Anna Müller", WebUtility.HtmlDecode(list));
        foreach (var path in new[] { "/Contacts/Groups", "/Contacts/Duplicates", "/Contacts/Import" })
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
        }

        // export of everything as vCard
        var export = await browser.PostAsync("/Contacts?handler=Export", Form(list, ("format", "vcf3")));
        var vcard = await export.Content.ReadAsStringAsync();
        Assert.Contains("FN:Anna Müller", vcard);
        Assert.Equal("text/vcard", export.Content.Headers.ContentType?.MediaType);

        // live: the long poll answers the revision
        var latest = await browser.GetFromJsonAsync<Dictionary<string, long>>("/live/contacts/revision?since=0&timeout=1");
        Assert.True(latest!["latest"] > 0);
    }

    private static async Task<MatCloudClient> ClientAsync(WebApplicationFactory<Program> app, string appId)
    {
        var (_, login) = await TestApp.DeviceAsync(app, appId, "ANNA-PC");
        return new MatCloudClient(app.Server.BaseAddress.ToString(), login.Token, $"Thix{appId}/1.0.0 (windows)", app.Server.CreateHandler());
    }

    /// <summary>A browser signed in as anna (the cookie stays in the client).</summary>
    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var login = await client.GetStringAsync("/Account/Login");
        var answer = await client.PostAsync("/Account/Login", Form(login, ("UserName", "anna"), ("Password", "anna-password")));
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return client;
    }

    /// <summary>The fields of a post with the anti-forgery token of the page it came from.</summary>
    private static FormUrlEncodedContent Form(string page, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        return new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).Append(new("__RequestVerificationToken", token)));
    }
}
