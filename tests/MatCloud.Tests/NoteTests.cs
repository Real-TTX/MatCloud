using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCloud.Apps.Note.Business;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;

namespace MatCloud.Tests;

/// <summary>The notes of ThixNote in the browser (the module Note) on the items of the general sync, against a real PostgreSQL (MATCLOUD_TEST_DB).</summary>
public sealed class NoteTests
{
    private static Actor Browser(User user) => new(user.Id, user.IsAdmin);

    private static Actor Device(User user, string name = "ANNA-PC") => new(user.Id, user.IsAdmin, 7, name, CloudApps.Note);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A note as ThixNote pushes it.</summary>
    private static SyncItemDto DeviceNote(string path, string? content = null, byte[]? bytes = null, string? key = null) => new()
    {
        Collection = "notes",
        Key = key ?? Guid.NewGuid().ToString(),
        Data = JsonSerializer.Serialize(new { path, content, bytes = bytes is null ? null : Convert.ToBase64String(bytes) }, CloudContract.Json),
        ContentHash = bytes is null ? Hash(content!) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private static readonly byte[] Encrypted = [.. "QNSEC1"u8, 0x40, 0x27, 0x09, 0x00, .. new byte[44], .. "geheim"u8];

    private static readonly byte[] EncryptedLater = [.. "QNSEC1"u8, 0x40, 0x27, 0x09, 0x00, .. new byte[44], .. "anders"u8];

    /// <summary>A note as ThixNote pushes it at <paramref name="at"/>, with the marker of "Schutz aufheben" when <paramref name="unprotected"/>.</summary>
    private static SyncItemDto Note(string key, string path, string? content = null, byte[]? bytes = null, DateTimeOffset? at = null, bool unprotected = false) => new()
    {
        Collection = "notes",
        Key = key,
        Data = JsonSerializer.Serialize(
            new { path, content, bytes = bytes is null ? null : Convert.ToBase64String(bytes), unprotected = unprotected ? true : (bool?)null }, CloudContract.Json),
        ContentHash = bytes is null ? Hash(content!) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        UpdatedAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private static SyncItemDto Tombstone(string key, DateTimeOffset at, string collection = "notes")
        => new() { Collection = collection, Key = key, IsDeleted = true, UpdatedAt = at };

    [Fact]
    public void A_protected_note_becomes_plain_text_only_with_the_marker_of_Schutz_aufheben()
    {
        var module = MatCloud.Core.Modules.CloudModules.Find(CloudApps.Note)!;
        var protectedNote = Note("k", "Privat/Titel.txts", bytes: Encrypted);
        var plainNote = Note("k", "Privat/Titel.md", "alter Text");

        // an old plain copy (or a plain text at the protected path, or bytes under a plain name, or no note at all) does not replace it
        Assert.False(module.AcceptsSyncChange(protectedNote, plainNote));
        Assert.False(module.AcceptsSyncChange(protectedNote, Note("k", "Privat/Titel.txts", "Klartext")));
        Assert.False(module.AcceptsSyncChange(protectedNote, Note("k", "Privat/Titel.md", bytes: Encrypted)));
        Assert.False(module.AcceptsSyncChange(protectedNote, plainNote with { Data = "kein JSON" }));
        Assert.False(module.AcceptsSyncChange(protectedNote, plainNote with { Data = "{\"path\":\"Titel.md\",\"content\":\"x\",\"unprotected\":false}" }));

        // "Schutz aufheben" on a device: the marker lets it through; the JSON is ThixNote's (camelCase)
        var unprotected = Note("k", "Privat/Titel.md", "alter Text", unprotected: true);
        Assert.Contains("\"unprotected\":true", unprotected.Data);
        Assert.True(module.AcceptsSyncChange(protectedNote, unprotected));

        // a protected note changed or renamed, a delete, protecting a plain note, plain changes, a deleted note written again: the last writer wins
        Assert.True(module.AcceptsSyncChange(protectedNote, Note("k", "Privat/Titel.txts", bytes: EncryptedLater)));
        Assert.True(module.AcceptsSyncChange(protectedNote, Note("k", "Arbeit/Neu.TXTS", bytes: Encrypted)));
        Assert.True(module.AcceptsSyncChange(protectedNote, Tombstone("k", DateTimeOffset.UtcNow)));
        Assert.True(module.AcceptsSyncChange(plainNote, protectedNote));
        Assert.True(module.AcceptsSyncChange(plainNote, Note("k", "Privat/Titel.txt", "neuer Text")));
        Assert.True(module.AcceptsSyncChange(Tombstone("k", DateTimeOffset.UtcNow), plainNote));
        Assert.True(module.AcceptsSyncChange(plainNote with { Data = "{\"t\":1}" }, plainNote));

        // a push that deletes the protected note and writes it plain in one go never reaches the rule: one key twice is refused before
        Assert.Equal("items", SyncService.Validate(new SyncPushRequest([Tombstone("k", DateTimeOffset.UtcNow), plainNote]))!.Value.Field);

        // other collections of ThixNote, and the other modules, are not touched by the rule
        Assert.True(module.AcceptsSyncChange(protectedNote with { Collection = "folders" }, plainNote with { Collection = "folders" }));
        foreach (var other in MatCloud.Core.Modules.CloudModules.All.Where(m => m.AppId != CloudApps.Note))
        {
            Assert.True(other.AcceptsSyncChange(protectedNote, plainNote));
        }
    }

    [DbFact]
    public async Task An_old_plain_copy_of_a_device_cannot_turn_a_protected_note_back_into_plain_text()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna, "ANNA-PC");
        var laptop = Device(anna, "LAPTOP");
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var key = Guid.NewGuid().ToString();
        await SyncService.PushAsync(test.Db, pc, CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "Klartext", at: start)]));

        // the PC protects the note (same key), the laptop was offline and edits its old plain copy later by its own clock
        var protecting = await SyncService.PushAsync(test.NewContext(), pc, CloudApps.Note, new SyncPushRequest([Note(key, "Titel.txts", bytes: Encrypted, at: start.AddSeconds(10))]));
        Assert.True(Assert.Single(protecting.Results).Applied);
        var stale = await SyncService.PushAsync(test.NewContext(), laptop, CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "offline geändert", at: start.AddSeconds(12))]));

        // not applied: the laptop gets the protected note, exactly as when a newer change is there
        var result = Assert.Single(stale.Results);
        Assert.False(result.Applied);
        Assert.Equal(protecting.Results[0].Revision, result.Revision);
        using (var current = JsonDocument.Parse(result.Current!.Data!))
        {
            Assert.Equal("Titel.txts", current.RootElement.GetProperty("path").GetString());
            Assert.Equal(Convert.ToBase64String(Encrypted), current.RootElement.GetProperty("bytes").GetString());
        }

        Assert.Equal(protecting.Latest, stale.Latest);
        Assert.Equal(NoteKind.Protected, (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!.Kind);

        // "Schutz aufheben" on the laptop: the marker makes it plain text on every device, and later plain changes need no marker
        var unprotecting = await SyncService.PushAsync(test.NewContext(), laptop, CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "wieder offen", at: start.AddSeconds(20), unprotected: true)]));
        Assert.True(Assert.Single(unprotecting.Results).Applied);
        var plain = await SyncService.PushAsync(test.NewContext(), pc, CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "weiter", at: start.AddSeconds(30))]));
        Assert.True(Assert.Single(plain.Results).Applied);
        var note = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;
        Assert.Equal(NoteKind.Markdown, note.Kind);
        Assert.Equal("weiter", note.Content);
    }

    [DbFact]
    public async Task A_protected_note_is_changed_renamed_and_deleted_as_before_and_a_plain_note_is_protected()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var key = Guid.NewGuid().ToString();

        async Task<bool> Applied(SyncItemDto item)
            => Assert.Single((await SyncService.PushAsync(test.NewContext(), pc, CloudApps.Note, new SyncPushRequest([item]))).Results).Applied;

        Assert.True(await Applied(Note(key, "Titel.md", "eins", at: start)));
        Assert.True(await Applied(Note(key, "Titel.md", "zwei", at: start.AddSeconds(1))));
        Assert.True(await Applied(Note(key, "Titel.txts", bytes: Encrypted, at: start.AddSeconds(2))));
        Assert.True(await Applied(Note(key, "Titel.txts", bytes: EncryptedLater, at: start.AddSeconds(3))));
        Assert.True(await Applied(Note(key, "Privat/Zugang.txts", bytes: EncryptedLater, at: start.AddSeconds(4))));
        Assert.True(await Applied(new SyncItemDto { Collection = "folders", Key = "Privat", Data = "{}", UpdatedAt = start.AddSeconds(5) }));

        // a delete is not held back (ThixNote moves the note into the trash of the computer)
        Assert.True(await Applied(Tombstone(key, start.AddSeconds(6))));
        Assert.True((await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).NotFound);

        // another app of the general sync with the same shape of data: no rule of ThixNote
        var tunnel = new Actor(anna.Id, anna.IsAdmin, 8, "ANNA-PC", CloudApps.Tunnel);
        await SyncService.PushAsync(test.NewContext(), tunnel, CloudApps.Tunnel, new SyncPushRequest([Note(key, "Titel.txts", bytes: Encrypted, at: start)]));
        var other = await SyncService.PushAsync(test.NewContext(), tunnel, CloudApps.Tunnel, new SyncPushRequest([Note(key, "Titel.md", "Klartext", at: start.AddSeconds(1))]));
        Assert.True(Assert.Single(other.Results).Applied);
    }

    [DbFact]
    public async Task The_pages_never_turn_a_protected_note_into_plain_text_and_never_drop_a_text_silently()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var key = Guid.NewGuid().ToString();
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "Klartext", at: start)]));
        var opened = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;

        // the page is open on the plain note while a device protects it
        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Note, new SyncPushRequest([Note(key, "Titel.txts", bytes: Encrypted, at: start.AddSeconds(10))]));
        var saved = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(key, "Titel", "", NoteKind.Markdown, "im Browser geschrieben", opened.Hash));
        Assert.False(saved.Ok);
        Assert.Equal("Text", saved.Field);
        var note = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;
        Assert.Equal(NoteKind.Protected, note.Kind);
        Assert.Equal(Convert.ToBase64String(Encrypted), note.Bytes);

        // renaming it on its page keeps it protected
        var renamed = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(key, "Zugang", "Privat", NoteKind.Protected, string.Empty, note.Hash));
        Assert.True(renamed.Ok, renamed.Error);
        Assert.Equal("Privat/Zugang.txts", (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!.Path);

        // the rule holds for the browser too: a plain text pushed in its name is not applied
        var pushed = await SyncService.PushAsync(test.NewContext(), Browser(anna), CloudApps.Note, new SyncPushRequest([Note(key, "Privat/Zugang.md", "Klartext", at: DateTimeOffset.UtcNow)]));
        Assert.False(Assert.Single(pushed.Results).Applied);
        Assert.Equal(NoteKind.Protected, (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!.Kind);
    }

    [DbFact]
    public async Task A_save_of_the_pages_that_the_cloud_does_not_take_is_never_reported_as_saved()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var key = Guid.NewGuid().ToString();

        // a device whose clock is ahead wrote the note: the page opens on that text, and its save is older by the clocks, so the cloud keeps the device's
        // (the same answer as for a device that protects the note between the checks of the page and its push)
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([Note(key, "Titel.md", "vom Gerät", at: DateTimeOffset.UtcNow.AddHours(1))]));
        var opened = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;
        var saved = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(key, "Titel", "", NoteKind.Markdown, "im Browser geschrieben", opened.Hash));
        Assert.False(saved.Ok);
        Assert.Equal("Text", saved.Field);
        Assert.Equal("vom Gerät", (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!.Content);

        // the same for the encrypted text of a protected note and for renaming it
        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Note, new SyncPushRequest([Note(key, "Titel.txts", bytes: Encrypted, at: DateTimeOffset.UtcNow.AddHours(2))]));
        var locked = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;
        var encrypted = await NoteService.SaveProtectedAsync(test.NewContext(), Browser(anna), key, Convert.ToBase64String(EncryptedLater), locked.Hash);
        Assert.False(encrypted.Ok);
        Assert.Equal("Bytes", encrypted.Field);
        var renamed = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(key, "Zugang", "", NoteKind.Protected, string.Empty, locked.Hash));
        Assert.False(renamed.Ok);
        Assert.Equal("Title", renamed.Field);
        var note = (await NoteService.GetAsync(test.NewContext(), Browser(anna), key)).Value!;
        Assert.Equal("Titel.txts", note.Path);
        Assert.Equal(Convert.ToBase64String(Encrypted), note.Bytes);
    }

    [DbFact]
    public async Task A_note_written_in_the_browser_reaches_ThixNote_in_its_own_format()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");

        var saved = await NoteService.SaveAsync(test.Db, Browser(anna), new NoteInput(null, "Plan", "Arbeit / Projekte", NoteKind.Markdown, "# Plan\r\n\r\n- Termine", null));
        Assert.True(saved.Ok, saved.Error);

        var changes = await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Note, 0, 100);
        var note = Assert.Single(changes.Items, i => i.Collection == "notes");
        Assert.Equal(saved.Value, note.Key);
        Assert.Equal("Web", note.Source);
        using var data = JsonDocument.Parse(note.Data!);
        Assert.Equal("Arbeit/Projekte/Plan.md", data.RootElement.GetProperty("path").GetString());
        Assert.Equal("# Plan\n\n- Termine", data.RootElement.GetProperty("content").GetString());
        Assert.False(data.RootElement.TryGetProperty("bytes", out _));
        Assert.Equal(Hash("# Plan\n\n- Termine"), note.ContentHash);
        Assert.Equal(["Arbeit", "Arbeit/Projekte"], changes.Items.Where(i => i.Collection == "folders").Select(i => i.Key).Order());
    }

    [DbFact]
    public async Task The_notes_of_the_devices_are_listed_searched_and_filtered()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([
            DeviceNote("Einkauf.md", "Milch, Brot"),
            DeviceNote("Arbeit/Meeting.txt", "Agenda für Dienstag"),
            DeviceNote("Privat/Zugang.txts", bytes: [.. "QNSEC1"u8, 1, 0, 0, 0, .. new byte[44], 9, 9]),
            new SyncItemDto { Collection = "folders", Key = "Leer", Data = "{}", UpdatedAt = DateTimeOffset.UtcNow },
        ]));

        var all = await NoteService.ListAsync(test.NewContext(), Browser(anna), new NoteQuery(null, null, "title", false));
        Assert.Equal(["Einkauf", "Meeting", "Zugang"], all.Items.Select(n => n.Title));
        Assert.Equal(["Arbeit", "Leer", "Privat"], all.Folders);
        Assert.Equal(NoteKind.Protected, all.Items.Single(n => n.Title == "Zugang").Kind);
        Assert.Null(all.Items.Single(n => n.Title == "Zugang").Content);
        Assert.Equal("ANNA-PC", all.Items[0].Source);

        Assert.Equal(["Meeting"], (await NoteService.ListAsync(test.NewContext(), Browser(anna), new NoteQuery("dienstag", null, "title", false))).Items.Select(n => n.Title));
        Assert.Equal(["Meeting"], (await NoteService.ListAsync(test.NewContext(), Browser(anna), new NoteQuery(null, "arbeit", "title", false))).Items.Select(n => n.Title));
    }

    [DbFact]
    public async Task A_change_keeps_the_key_refuses_a_stale_text_and_a_taken_name_and_a_delete_is_a_tombstone()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var first = DeviceNote("Plan.md", "eins\r\nzwei");
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([first, DeviceNote("Andere.md", "x")]));
        var note = (await NoteService.GetAsync(test.NewContext(), Browser(anna), first.Key)).Value!;

        // renamed and moved, the text changed: the line ends the note had (CRLF) stay
        var saved = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(note.Key, "Projektplan", "Arbeit", NoteKind.Text, "eins\nzwei\ndrei", note.Hash));
        Assert.Equal(note.Key, saved.Value);
        var changed = (await NoteService.GetAsync(test.NewContext(), Browser(anna), note.Key)).Value!;
        Assert.Equal("Arbeit/Projektplan.md", changed.Path); // the kind of an existing note stays
        Assert.Equal("eins\r\nzwei\r\ndrei", changed.Content);

        // a text the page loaded before a device changed it is not saved over the device's change
        var stale = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(note.Key, "Projektplan", "Arbeit", NoteKind.Markdown, "alt", note.Hash));
        Assert.Equal("Text", stale.Field);

        // a name another note has in the same folder
        var taken = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(null, "andere", "", NoteKind.Markdown, "y", null));
        Assert.Equal("Title", taken.Field);
        Assert.Equal("Title", (await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(null, "a:b", "", NoteKind.Markdown, "y", null))).Field);
        Assert.Equal("Folder", (await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(null, "ok", "a/b?", NoteKind.Markdown, "y", null))).Field);

        Assert.True((await NoteService.DeleteAsync(test.NewContext(), Browser(anna), note.Key)).Ok);
        var tombstone = (await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Note, 0, 100)).Items.Single(i => i.Key == note.Key);
        Assert.True(tombstone.IsDeleted);
        Assert.True((await NoteService.GetAsync(test.NewContext(), Browser(anna), note.Key)).NotFound);
    }

    [DbFact]
    public async Task A_protected_note_takes_only_encrypted_bytes_and_keeps_them_when_it_is_renamed()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        byte[] encrypted = [.. "QNSEC1"u8, 0x40, 0x27, 0x09, 0x00, .. RandomNumberGenerator.GetBytes(44), .. "geheim"u8];
        var item = DeviceNote("Zugang.txts", bytes: encrypted);
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([item]));
        var note = (await NoteService.GetAsync(test.NewContext(), Browser(anna), item.Key)).Value!;

        Assert.Equal("Bytes", (await NoteService.SaveProtectedAsync(test.NewContext(), Browser(anna), note.Key, Convert.ToBase64String("klartext"u8.ToArray()), note.Hash)).Field);
        Assert.Equal("Bytes", (await NoteService.SaveProtectedAsync(test.NewContext(), Browser(anna), note.Key, "kein base64!", note.Hash)).Field);

        byte[] newer = [.. "QNSEC1"u8, 0x40, 0x27, 0x09, 0x00, .. RandomNumberGenerator.GetBytes(44), .. "anders"u8];
        Assert.True((await NoteService.SaveProtectedAsync(test.NewContext(), Browser(anna), note.Key, Convert.ToBase64String(newer), note.Hash)).Ok);
        Assert.Equal("Bytes", (await NoteService.SaveProtectedAsync(test.NewContext(), Browser(anna), note.Key, Convert.ToBase64String(newer), note.Hash)).Field);

        var renamed = await NoteService.SaveAsync(test.NewContext(), Browser(anna), new NoteInput(note.Key, "WLAN", "Privat", NoteKind.Markdown, "ignored", null));
        Assert.True(renamed.Ok);
        var after = (await NoteService.GetAsync(test.NewContext(), Browser(anna), note.Key)).Value!;
        Assert.Equal("Privat/WLAN.txts", after.Path);
        Assert.Equal(Convert.ToBase64String(newer), after.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(newer)), after.Hash);
    }

    [DbFact]
    public async Task Only_the_browser_of_the_account_uses_the_pages_and_the_devices_keep_the_general_sync()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await Assert.ThrowsAsync<NotAllowedException>(() => NoteService.ListAsync(test.Db, Device(anna), new NoteQuery(null, null, "changed", true)));

        // ThixNote has pages now, and its devices still sync through /api/v1/sync
        Assert.True(MatCloud.Core.Modules.CloudModules.HasModule(CloudApps.Note));
        Assert.True(MatCloud.Core.Modules.CloudModules.UsesGeneralSync(CloudApps.Note));
        Assert.False(MatCloud.Core.Modules.CloudModules.UsesGeneralSync(CloudApps.Clip));
        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Note, new SyncPushRequest([DeviceNote("A.md", "a")]));
        Assert.Equal(1, (await AppService.UsageAsync(test.NewContext(), anna.Id, CloudApps.Note)).Items);
    }

    [Fact]
    public void Titles_and_folders_are_file_names_on_every_computer()
    {
        Assert.Null(NoteService.NameProblem("Einkauf 2026"));
        Assert.NotNull(NoteService.NameProblem(""));
        Assert.NotNull(NoteService.NameProblem("a/b"));
        Assert.NotNull(NoteService.NameProblem(".versteckt"));
        Assert.NotNull(NoteService.NameProblem("Ende."));
        Assert.Equal("Arbeit/Projekte", NoteService.CleanFolder(" Arbeit / Projekte/ "));
        Assert.Equal(string.Empty, NoteService.CleanFolder("  "));
        Assert.Null(NoteService.CleanFolder("Arbeit/Pro*jekte"));
        Assert.Equal("Termine morgen", NoteService.Preview("\n# Termine morgen\nmehr"));
    }

    [Fact]
    public void The_card_of_a_note_shows_its_first_lines_as_plain_text()
    {
        const string text = "# Plan\n\nDie **wichtigsten** Dinge für _diese_ Woche:\n\n- [x] [MatCloud](https://cloud.rltx.de) ausrollen\n1. `Code` lesen\n\n| Tag | Was |\n| --- | --- |\n| Montag | Planung |\n\n---\n```\ndocker compose up\n```";
        Assert.Equal(
            ["Die wichtigsten Dinge für diese Woche:", "MatCloud ausrollen", "Code lesen", "Tag · Was", "Montag · Planung"],
            NoteMarkdown.PlainLines(text, "Plan", 5, 500));
        Assert.Equal(["Die wichtigsten…"], NoteMarkdown.PlainLines(text, "Plan", 5, 20));
        Assert.Equal("Dunkles Logo", NoteService.Preview("Dunkles Logo\nzweite Zeile"));
        Assert.Equal(3, NoteService.CountWords("Milch, Brot –\n# Kaffee"));
    }

    [Fact]
    public void Markdown_is_drawn_without_html_of_its_own_and_without_the_heading_that_repeats_the_title()
    {
        var html = NoteMarkdown.ToHtml("# Plan\n\n<script>alert(1)</script>\n\n- [ ] offen\n\n[Link](https://example.com)", "Plan");
        Assert.DoesNotContain("<h1", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains("<h1", NoteMarkdown.ToHtml("# Anderes\n\nText", "Plan"));
    }

    [Fact]
    public void The_preview_of_the_editor_is_only_for_the_browser_of_the_account()
    {
        var browser = new Actor(1, false);
        Assert.Contains("<strong>fett</strong>", NoteService.Render(browser, "**fett**", "Titel").Value);
        Assert.Equal("Text", NoteService.Render(browser, new string('x', NoteService.MaxContentLength + 1), null).Field);
        Assert.Throws<NotAllowedException>(() => NoteService.Render(new Actor(1, false, 7, "ANNA-PC", CloudApps.Note), "x", null));
    }
}
