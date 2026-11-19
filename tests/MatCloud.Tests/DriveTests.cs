using System.Security.Cryptography;
using MatCloud.Apps.Drive.Business;
using MatCloud.Apps.Drive.Data;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Tests;

/// <summary>The business layer of the Drive against a real PostgreSQL (MATCLOUD_TEST_DB), with a store in a temporary folder.</summary>
public sealed class DriveTests
{
    private const long Max = 64L * 1024 * 1024;

    private static Actor Browser(User user) => new(user.Id, user.IsAdmin);

    private static Actor Device(User user, string app, string name = "ANNA-PC") => new(user.Id, user.IsAdmin, 7, name, app);

    private static DriveStore NewStore() => new(Directory.CreateTempSubdirectory("matcloud-drive-").FullName);

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<DriveItem> UploadAsync(AppDbContext db, DriveStore store, Actor actor, long? parent, string name, string text,
        DriveNameConflict onConflict = DriveNameConflict.Fail)
    {
        var outcome = await DriveFiles.CreateAsync(db, store, actor, parent, name, onConflict, Bytes(text), text.Length, Max);
        Assert.True(outcome.Ok, outcome.Error);
        return outcome.Value!;
    }

    private static async Task<string> ReadAsync(AppDbContext db, DriveStore store, Actor actor, long id, long? version = null)
    {
        var content = await DriveFiles.OpenAsync(db, store, actor, id, version);
        Assert.True(content.Ok, content.Error);
        return await File.ReadAllTextAsync(content.Value!.PhysicalPath);
    }

    [DbFact]
    public async Task Folders_and_files_are_listed_folders_first_and_found_by_name()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var me = Browser(anna);

        var projects = (await DriveItems.CreateFolderAsync(test.Db, me, null, "Projekte")).Value!;
        var offer = await UploadAsync(test.Db, store, me, projects.Id, "Angebot.docx", "Angebot 2026");
        await UploadAsync(test.Db, store, me, projects.Id, "Zahlen.xlsx", "1;2;3");
        await DriveItems.CreateFolderAsync(test.Db, me, projects.Id, "Archiv");

        var listing = (await DriveItems.ListAsync(test.NewContext(), me, projects.Id, new DriveQuery())).Value!;
        Assert.Equal(["Archiv", "Angebot.docx", "Zahlen.xlsx"], listing.Items.Select(i => i.Name));
        Assert.Equal("Projekte", Assert.Single(listing.Ancestors).Name);
        Assert.Equal("/Projekte", listing.Items[1].Location);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", listing.Items[1].ContentType);
        Assert.Equal(1, listing.Items[1].Version);
        Assert.StartsWith("\"", listing.Items[1].ETag);

        var bySize = (await DriveItems.ListAsync(test.NewContext(), me, projects.Id, new DriveQuery(DriveSort.Size, Descending: true))).Value!;
        Assert.Equal(["Archiv", "Angebot.docx", "Zahlen.xlsx"], bySize.Items.Select(i => i.Name));
        var documents = (await DriveItems.ListAsync(test.NewContext(), me, projects.Id, new DriveQuery(Category: DriveTypes.Document))).Value!;
        Assert.Equal("Angebot.docx", Assert.Single(documents.Items).Name);

        var found = await DriveItems.SearchAsync(test.NewContext(), me, "ANGEBOT", new DriveQuery());
        Assert.Equal(offer.Id, Assert.Single(found.Items).Id);
        Assert.Equal("Angebot 2026", await ReadAsync(test.NewContext(), store, me, offer.Id));
    }

    [DbFact]
    public async Task An_item_of_another_account_does_not_exist_not_even_for_an_administrator()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var ben = await test.AddUserAsync("ben", admin: true);
        var store = NewStore();
        var folder = (await DriveItems.CreateFolderAsync(test.Db, Browser(anna), null, "Privat")).Value!;
        var file = await UploadAsync(test.Db, store, Browser(anna), folder.Id, "Tagebuch.odt", "geheim");
        var other = Browser(ben);

        Assert.Equal(DriveFailure.NotFound, (await DriveItems.GetAsync(test.Db, other, file.Id)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.ListAsync(test.Db, other, folder.Id, new DriveQuery())).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.OpenAsync(test.Db, store, other, file.Id)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.RenameAsync(test.Db, other, file.Id, "x.odt")).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.TrashAsync(test.Db, other, file.Id)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.SaveVersionAsync(test.Db, store, other, file.Id, "*", Bytes("x"), 1, Max)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.VersionsAsync(test.Db, other, file.Id)).Failure);

        // into a folder of another account: the folder does not exist; a move of one's own item there neither
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.CreateAsync(test.Db, store, other, folder.Id, "Mein.txt", DriveNameConflict.Fail, Bytes("x"), 1, Max)).Failure);
        var bens = await UploadAsync(test.Db, store, other, null, "Ben.txt", "ben");
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.MoveAsync(test.Db, other, bens.Id, folder.Id)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.CopyAsync(test.Db, other, file.Id, null, null)).Failure);

        Assert.Empty((await DriveItems.SearchAsync(test.Db, other, "Tagebuch", new DriveQuery())).Items);
        Assert.Equal(["Ben.txt"], (await DriveItems.ListAsync(test.Db, other, null, new DriveQuery())).Value!.Items.Select(i => i.Name));
    }

    [DbFact]
    public async Task The_office_apps_reach_the_Drive_other_apps_do_not_and_switching_it_off_stops_them_all()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var admin = await test.AddUserAsync("admin", admin: true);
        var store = NewStore();

        foreach (var app in new[] { CloudApps.Drive, CloudApps.Write, CloudApps.Calc, CloudApps.Show })
        {
            var item = await UploadAsync(test.NewContext(), store, Device(anna, app), null, $"Von {app}.txt", app);
            var version = (await DriveFiles.VersionsAsync(test.NewContext(), Browser(anna), item.Id)).Value!.Versions.Single();
            Assert.Equal(app, version.App);
            Assert.Equal("ANNA-PC", version.Source);
        }

        foreach (var app in new[] { CloudApps.Clip, CloudApps.Note, CloudApps.Calendar, CloudApps.Launcher, CloudApps.Fred, CloudApps.Tunnel })
        {
            await Assert.ThrowsAsync<NotAllowedException>(() => DriveItems.ListAsync(test.NewContext(), Device(anna, app), null, new DriveQuery()));
            await Assert.ThrowsAsync<NotAllowedException>(() => DriveFiles.CreateAsync(test.NewContext(), store, Device(anna, app), null, "x.txt", DriveNameConflict.Fail, Bytes("x"), 1, Max));
        }

        Assert.True((await AppService.SetEnabledAsync(test.NewContext(), Browser(admin), CloudApps.Drive, false)).Ok);
        await Assert.ThrowsAsync<NotAllowedException>(() => DriveItems.ListAsync(test.NewContext(), Device(anna, CloudApps.Write), null, new DriveQuery()));
        await Assert.ThrowsAsync<NotAllowedException>(() => DriveItems.RecentAsync(test.NewContext(), Browser(anna), 10));
        Assert.True((await AppService.SetEnabledAsync(test.NewContext(), Browser(admin), CloudApps.Drive, true)).Ok);
        Assert.Equal(4, (await DriveItems.ListAsync(test.NewContext(), Device(anna, CloudApps.Calc), null, new DriveQuery())).Value!.Total);
    }

    [DbFact]
    public async Task A_save_on_a_stale_version_is_refused_with_the_current_one_and_overwriting_is_explicit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var pc = Device(anna, CloudApps.Write, "ANNA-PC");
        var mac = Device(anna, CloudApps.Write, "ANNA-MAC");
        var opened = await UploadAsync(test.Db, store, pc, null, "Brief.docx", "Version 1");

        // the Mac saves first
        var macSave = await DriveFiles.SaveVersionAsync(test.NewContext(), store, mac, opened.Id, opened.ETag, Bytes("Version 2 vom Mac"), null, Max);
        Assert.True(macSave.Ok, macSave.Error);
        Assert.Equal(2, macSave.Value!.Version);
        Assert.NotEqual(opened.ETag, macSave.Value.ETag);

        // the PC saves on what it opened: 412 with the file as it is now
        var stale = await DriveFiles.SaveVersionAsync(test.NewContext(), store, pc, opened.Id, opened.ETag, Bytes("Version 2 vom PC"), null, Max);
        Assert.Equal(DriveFailure.Changed, stale.Failure);
        Assert.Equal(macSave.Value.ETag, stale.Current!.ETag);
        Assert.Equal("ANNA-MAC", stale.Current.ModifiedBy);

        // without If-Match: 428; "Überschreiben" with the current ETag works; "*" too
        Assert.Equal(DriveFailure.IfMatchMissing, (await DriveFiles.SaveVersionAsync(test.NewContext(), store, pc, opened.Id, null, Bytes("x"), null, Max)).Failure);
        var overwritten = await DriveFiles.SaveVersionAsync(test.NewContext(), store, pc, opened.Id, $"W/{stale.Current.ETag}", Bytes("Version 3 vom PC"), null, Max);
        Assert.True(overwritten.Ok, overwritten.Error);
        Assert.True((await DriveFiles.SaveVersionAsync(test.NewContext(), store, pc, opened.Id, "*", Bytes("Version 4"), null, Max)).Ok);

        // "Als Kopie speichern": the same name with Rename gets a number
        var copy = await UploadAsync(test.NewContext(), store, pc, null, "Brief.docx", "Version 2 vom PC", DriveNameConflict.Rename);
        Assert.Equal("Brief (2).docx", copy.Name);
        var taken = await DriveFiles.CreateAsync(test.NewContext(), store, pc, null, "BRIEF.docx", DriveNameConflict.Fail, Bytes("x"), 1, Max);
        Assert.Equal(DriveFailure.NameTaken, taken.Failure);
        Assert.Equal(opened.Id, taken.Current!.Id);

        // every version is kept and can be read and restored (as a new version)
        var versions = (await DriveFiles.VersionsAsync(test.NewContext(), pc, opened.Id)).Value!.Versions;
        Assert.Equal([4, 3, 2, 1], versions.Select(v => v.Number));
        Assert.Equal("Version 2 vom Mac", await ReadAsync(test.NewContext(), store, pc, opened.Id, versions[2].Id));
        var restored = await DriveFiles.RestoreVersionAsync(test.NewContext(), pc, opened.Id, versions[3].Id);
        Assert.Equal(5, restored.Value!.Version);
        Assert.Equal("Version 1", await ReadAsync(test.NewContext(), store, pc, opened.Id));
    }

    [DbFact]
    public async Task An_upload_in_pieces_resumes_after_a_break_and_checks_the_version_at_the_end()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var me = Device(anna, CloudApps.Show);
        var bytes = RandomNumberGenerator.GetBytes(300_000);

        var started = await DriveFiles.StartUploadAsync(test.Db, me, new DriveUploadRequest(bytes.Length, Name: "Folien.pptx"));
        Assert.True(started.Ok, started.Error);
        var token = started.Value!.Id;
        Assert.True((await DriveFiles.AppendAsync(test.NewContext(), store, me, token, 0, new MemoryStream(bytes, 0, 100_000), 100_000)).Ok);

        // the connection broke during the second piece: the device asks how much arrived and sends from there (a part again)
        Assert.Equal(100_000, (await DriveFiles.UploadStateAsync(test.NewContext(), me, token)).Value!.Received);
        Assert.Equal(DriveFailure.Invalid, (await DriveFiles.AppendAsync(test.NewContext(), store, me, token, 150_000, new MemoryStream(bytes, 150_000, 10), 10)).Failure);
        Assert.True((await DriveFiles.AppendAsync(test.NewContext(), store, me, token, 50_000, new MemoryStream(bytes, 50_000, 200_000), 200_000)).Ok);
        Assert.Equal(DriveFailure.Invalid, (await DriveFiles.FinishUploadAsync(test.NewContext(), store, me, token)).Failure);
        Assert.True((await DriveFiles.AppendAsync(test.NewContext(), store, me, token, 250_000, new MemoryStream(bytes, 250_000, 50_000), 50_000)).Ok);
        Assert.Equal(DriveFailure.TooLarge, (await DriveFiles.AppendAsync(test.NewContext(), store, me, token, 300_000, new MemoryStream([1, 2, 3]), 3)).Failure);

        var file = await DriveFiles.FinishUploadAsync(test.NewContext(), store, me, token);
        Assert.True(file.Ok, file.Error);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Value!.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync((await DriveFiles.OpenAsync(test.NewContext(), store, me, file.Value.Id)).Value!.PhysicalPath));
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.UploadStateAsync(test.NewContext(), me, token)).Failure);

        // a new version in pieces checks If-Match when it starts and again when it ends
        var stale = file.Value.ETag;
        var next = (await DriveFiles.StartUploadAsync(test.NewContext(), me, new DriveUploadRequest(3, ItemId: file.Value.Id, IfMatch: stale))).Value!;
        Assert.True((await DriveFiles.SaveVersionAsync(test.NewContext(), store, me, file.Value.Id, stale, Bytes("zwischendurch"), null, Max)).Ok);
        Assert.True((await DriveFiles.AppendAsync(test.NewContext(), store, me, next.Id, 0, new MemoryStream([1, 2, 3]), 3)).Ok);
        Assert.Equal(DriveFailure.Changed, (await DriveFiles.FinishUploadAsync(test.NewContext(), store, me, next.Id)).Failure);
        Assert.Equal(DriveFailure.Changed, (await DriveFiles.StartUploadAsync(test.NewContext(), me, new DriveUploadRequest(3, ItemId: file.Value.Id, IfMatch: stale))).Failure);

        // the upload of another account is not there
        var ben = await test.AddUserAsync("ben");
        var bens = (await DriveFiles.StartUploadAsync(test.NewContext(), Browser(ben), new DriveUploadRequest(3, Name: "b.txt"))).Value!;
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.AppendAsync(test.NewContext(), store, me, bens.Id, 0, new MemoryStream([1]), 1)).Failure);
    }

    [DbFact]
    public async Task The_quota_counts_every_version_and_the_trash_and_refuses_what_does_not_fit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var me = Browser(anna);
        var file = await UploadAsync(test.Db, store, me, null, "a.txt", new string('a', 400));
        await test.Db.DriveSpaces.Where(d => d.OwnerUserId == anna.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.QuotaBytes, 1000L));

        Assert.True((await DriveFiles.SaveVersionAsync(test.NewContext(), store, me, file.Id, "*", Bytes(new string('b', 400)), null, Max)).Ok);
        var quota = await DriveFiles.QuotaAsync(test.NewContext(), me);
        Assert.Equal(800, quota.UsedBytes);
        Assert.Equal(1000, quota.QuotaBytes);
        Assert.Equal(400, quota.OlderVersionsBytes);

        // refused before the first byte (declared size) and after the bytes (no size declared)
        Assert.Equal(DriveFailure.QuotaFull, (await DriveFiles.CreateAsync(test.NewContext(), store, me, null, "b.txt", DriveNameConflict.Fail, Bytes(new string('c', 300)), 300, Max)).Failure);
        Assert.Equal(DriveFailure.QuotaFull, (await DriveFiles.CreateAsync(test.NewContext(), store, me, null, "b.txt", DriveNameConflict.Fail, Bytes(new string('c', 300)), null, Max)).Failure);
        Assert.Equal(DriveFailure.QuotaFull, (await DriveFiles.StartUploadAsync(test.NewContext(), me, new DriveUploadRequest(300, Name: "b.txt"))).Failure);
        Assert.Equal(DriveFailure.QuotaFull, (await DriveItems.CopyAsync(test.NewContext(), me, file.Id, null, null)).Failure);
        Assert.Equal(DriveFailure.TooLarge, (await DriveFiles.CreateAsync(test.NewContext(), store, me, null, "c.txt", DriveNameConflict.Fail, Bytes("12345"), null, 4)).Failure);

        // the trash still counts; emptied, the space is free again
        await DriveItems.TrashAsync(test.NewContext(), me, file.Id);
        Assert.Equal(800, (await DriveFiles.QuotaAsync(test.NewContext(), me)).TrashBytes);
        Assert.Equal(1, await DriveItems.EmptyTrashAsync(test.NewContext(), me));
        Assert.Equal(0, (await DriveFiles.QuotaAsync(test.NewContext(), me)).UsedBytes);
        Assert.True((await DriveFiles.CreateAsync(test.NewContext(), store, me, null, "b.txt", DriveNameConflict.Fail, Bytes(new string('c', 300)), 300, Max)).Ok);
    }

    [DbFact]
    public async Task The_trash_hides_a_folder_with_everything_in_it_restores_it_and_deletes_for_good_only_from_there()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var me = Browser(anna);
        var folder = (await DriveItems.CreateFolderAsync(test.Db, me, null, "Reise")).Value!;
        var inner = (await DriveItems.CreateFolderAsync(test.Db, me, folder.Id, "Belege")).Value!;
        var file = await UploadAsync(test.Db, store, me, inner.Id, "Hotel.pdf", "pdf");
        await DriveItems.StarAsync(test.Db, me, file.Id, true);

        Assert.Equal(DriveFailure.Conflict, (await DriveItems.DeleteAsync(test.NewContext(), me, file.Id)).Failure);
        Assert.True((await DriveItems.TrashAsync(test.NewContext(), me, folder.Id)).Ok);
        Assert.Empty((await DriveItems.ListAsync(test.NewContext(), me, null, new DriveQuery())).Value!.Items);
        Assert.Empty((await DriveItems.SearchAsync(test.NewContext(), me, "Hotel", new DriveQuery())).Items);
        Assert.Empty((await DriveItems.StarredAsync(test.NewContext(), me, new DriveQuery())).Items);
        Assert.Equal(DriveFailure.NotFound, (await DriveFiles.OpenAsync(test.NewContext(), store, me, file.Id)).Failure);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.CreateFolderAsync(test.NewContext(), me, inner.Id, "Neu")).Failure);
        Assert.Equal("Reise", Assert.Single((await DriveItems.TrashAsync(test.NewContext(), me, new DriveQuery())).Items).Name);

        // the name is free again while it is in the trash; restored, the old folder gets a number
        await DriveItems.CreateFolderAsync(test.NewContext(), me, null, "Reise");
        var back = await DriveItems.RestoreAsync(test.NewContext(), me, folder.Id);
        Assert.Equal("Reise (2)", back.Value!.Name);
        Assert.Equal("Hotel.pdf", Assert.Single((await DriveItems.StarredAsync(test.NewContext(), me, new DriveQuery())).Items).Name);

        // an item whose folder is in the trash comes back at the top
        await DriveItems.TrashAsync(test.NewContext(), me, file.Id);
        await DriveItems.TrashAsync(test.NewContext(), me, folder.Id);
        var alone = await DriveItems.RestoreAsync(test.NewContext(), me, file.Id);
        Assert.Null(alone.Value!.ParentId);
        Assert.Equal("/", alone.Value.Location);

        // for good: the folder, its folder inside, their versions; the stars go with them
        Assert.True((await DriveItems.DeleteAsync(test.NewContext(), me, folder.Id)).Ok);
        Assert.Equal(DriveFailure.NotFound, (await DriveItems.GetAsync(test.NewContext(), me, inner.Id)).Failure);
        Assert.Empty((await DriveItems.TrashAsync(test.NewContext(), me, new DriveQuery())).Items);
        var tombstones = await test.NewContext().DriveNodes.IgnoreQueryFilters().CountAsync(n => n.UpdateState == SoftEntity.Deleted);
        Assert.Equal(2, tombstones);
    }

    [DbFact]
    public async Task Names_that_leave_their_folder_or_break_a_file_system_are_refused_and_folders_never_go_into_themselves()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var me = Browser(anna);
        foreach (var bad in new[] { "../geheim.txt", "a/b.txt", "a\\b.txt", "..", ".", "", "   ", "CON", "nul.txt", "Datei.", "Datei ", "x\u0001y", new string('a', 256), "C:x" })
        {
            var outcome = await DriveFiles.CreateAsync(test.NewContext(), store, me, null, bad, DriveNameConflict.Fail, Bytes("x"), 1, Max);
            Assert.Equal(DriveFailure.Invalid, outcome.Failure);
            Assert.Equal("name", outcome.Field);
            Assert.Equal(DriveFailure.Invalid, (await DriveItems.CreateFolderAsync(test.NewContext(), me, null, bad)).Failure);
        }

        Assert.Empty(Directory.EnumerateFiles(store.Temp));
        var outer = (await DriveItems.CreateFolderAsync(test.NewContext(), me, null, "Außen")).Value!;
        var inner = (await DriveItems.CreateFolderAsync(test.NewContext(), me, outer.Id, "Innen")).Value!;
        Assert.Equal(DriveFailure.Conflict, (await DriveItems.MoveAsync(test.NewContext(), me, outer.Id, inner.Id)).Failure);
        Assert.Equal(DriveFailure.Conflict, (await DriveItems.MoveAsync(test.NewContext(), me, outer.Id, outer.Id)).Failure);
        Assert.Equal(DriveFailure.Conflict, (await DriveItems.CopyAsync(test.NewContext(), me, outer.Id, inner.Id, null)).Failure);
        Assert.Equal("INNEN", (await DriveItems.RenameAsync(test.NewContext(), me, inner.Id, "INNEN")).Value!.Name);
        Assert.Equal(DriveFailure.NameTaken, (await DriveItems.CreateFolderAsync(test.NewContext(), me, outer.Id, "innen")).Failure);
        Assert.True((await DriveItems.RenameAsync(test.NewContext(), me, inner.Id, "Innen")).Ok);

        // a move to the top and back, the paths of everything below follow
        var file = await UploadAsync(test.NewContext(), store, me, inner.Id, "Plan.md", "# Plan");
        Assert.True((await DriveItems.MoveAsync(test.NewContext(), me, inner.Id, null)).Ok);
        Assert.Equal("/Innen", (await DriveItems.GetAsync(test.NewContext(), me, file.Id)).Value!.Location);
        Assert.True((await DriveItems.MoveAsync(test.NewContext(), me, inner.Id, outer.Id)).Ok);
        var moved = (await DriveItems.GetAsync(test.NewContext(), me, file.Id)).Value!;
        Assert.Equal("/Außen/Innen", moved.Location);
        Assert.Equal(["Außen", "Innen"], moved.Ancestors!.Select(a => a.Name));

        // a copy of a folder copies what is in it and shares the bytes
        var copy = (await DriveItems.CopyAsync(test.NewContext(), me, outer.Id, null, null)).Value!;
        Assert.Equal("Kopie von Außen", copy.Name);
        var copied = (await DriveItems.SearchAsync(test.NewContext(), me, "Plan.md", new DriveQuery())).Items;
        Assert.Equal(2, copied.Count);
        Assert.Single(copied.Select(c => c.Sha256).Distinct());
        Assert.Single(await test.NewContext().DriveBlobs.ToListAsync());
    }

    [DbFact]
    public async Task Recent_lists_what_was_opened_or_saved_last_in_any_app()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = NewStore();
        var write = Device(anna, CloudApps.Write);
        var a = await UploadAsync(test.Db, store, write, null, "a.docx", "a");
        var b = await UploadAsync(test.Db, store, Browser(anna), null, "b.xlsx", "b");
        await DriveFiles.OpenAsync(test.NewContext(), store, write, a.Id);

        var recent = await DriveItems.RecentAsync(test.NewContext(), Device(anna, CloudApps.Calc), 10);
        Assert.Equal([a.Id, b.Id], recent.Items.Select(i => i.Id));
        await DriveItems.TrashAsync(test.NewContext(), write, a.Id);
        Assert.Equal([b.Id], (await DriveItems.RecentAsync(test.NewContext(), write, 10)).Items.Select(i => i.Id));
    }

    [DbFact]
    public async Task The_housekeeping_removes_blobs_nobody_names_and_deleting_the_data_of_the_app_empties_the_Drive()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var ben = await test.AddUserAsync("ben");
        var store = NewStore();
        var shared = await UploadAsync(test.Db, store, Browser(anna), null, "gleich.txt", "derselbe Inhalt");
        await UploadAsync(test.Db, store, Browser(ben), null, "gleich.txt", "derselbe Inhalt");
        var gone = await UploadAsync(test.Db, store, Browser(anna), null, "weg.txt", "nur hier");
        Assert.Equal(2, await test.NewContext().DriveBlobs.CountAsync());

        await DriveItems.TrashAsync(test.NewContext(), Browser(anna), gone.Id);
        await DriveItems.EmptyTrashAsync(test.NewContext(), Browser(anna));
        await test.NewContext().DriveBlobs.ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdateDate, DateTime.UtcNow.AddHours(-2)));
        Assert.Equal(1, await DriveHousekeeping.SweepAsync(test.NewContext(), store, CancellationToken.None));
        Assert.False(store.Exists(Convert.ToHexStringLower(SHA256.HashData("nur hier"u8.ToArray()))));
        Assert.True(store.Exists(shared.Sha256!));

        // anna deletes her Drive: ben's file with the same content stays
        await AppService.DeleteMyDataAsync(test.NewContext(), Browser(anna), CloudApps.Drive);
        Assert.Equal(0, (await AppService.UsageAsync(test.NewContext(), anna.Id, CloudApps.Drive)).Items);
        Assert.Equal(1, (await AppService.UsageAsync(test.NewContext(), ben.Id, CloudApps.Drive)).Items);
        Assert.Equal(0, await DriveHousekeeping.SweepAsync(test.NewContext(), store, CancellationToken.None));
        Assert.True(store.Exists(shared.Sha256!));
    }
}
