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
