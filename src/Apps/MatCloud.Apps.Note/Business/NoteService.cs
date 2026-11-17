using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Note.Business;

/// <summary>What a note is: Markdown (.md), plain text (.txt) or protected with a password (.txts, encrypted by ThixNote).</summary>
public enum NoteKind
{
    Markdown,
    Text,
    Protected,
}

/// <summary>A note as the pages show it. <see cref="Content"/> is null for a protected note; <see cref="Bytes"/> (base64) is set only for it.</summary>
public sealed record NoteItem(
    string Key, string Path, string Title, string Folder, NoteKind Kind, string? Content, string? Bytes, string Hash, DateTime UpdatedDate, string? Source, int Size)
{
    /// <summary>One line of the text for the list (nothing of a protected note).</summary>
    public string Preview => Content is null ? string.Empty : NoteService.Preview(Content, Title);

    /// <summary>The first lines of the text for the card of the list, as plain text (nothing of a protected note).</summary>
    public IReadOnlyList<string> Excerpt => Content is null ? [] : NoteMarkdown.PlainLines(Content, Title, 5, 320);

    /// <summary>How many words the text has (0 for a protected note).</summary>
    public int Words => Content is null ? 0 : NoteService.CountWords(Content);

    public string KindName => NoteService.KindName(Kind);

    /// <summary>Where it was written last: "im Browser" or "auf ANNA-PC" (empty when unknown).</summary>
    public string Where => Source is null ? string.Empty : Source == "Web" ? "im Browser" : "auf " + Source;
}

/// <summary>The query of the list: a search in titles and texts, a folder (with its sub folders), the order.</summary>
public sealed record NoteQuery(string? Search, string? Folder, string Sort, bool Descending);

public sealed record NoteListResult(IReadOnlyList<NoteItem> Items, int Total, IReadOnlyList<string> Folders, long Latest);

/// <summary>What the edit page posts. <see cref="BaseHash"/> is the hash of the text the page was opened with (empty for a new note).</summary>
public sealed record NoteInput(string? Key, string Title, string Folder, NoteKind Kind, string Content, string? BaseHash);

/// <summary>The data of a note in the general sync, exactly as ThixNote writes it: the path and the text, or the encrypted bytes (base64).</summary>
internal sealed record NoteData(string Path, string? Content = null, string? Bytes = null);

/// <summary>
/// The notes of ThixNote in the browser. ThixNote keeps every note as an item of the general sync (app <c>note</c>, collection <c>notes</c>, key = a GUID,
/// data = <see cref="NoteData"/>, hash = SHA-256 hex of the UTF-8 text or of the encrypted bytes) and every folder as an item of <c>folders</c> (key = its
/// path). These functions read and write the same items through <see cref="SyncService"/>, so every device of ThixNote sees a change of the browser at
/// once, and the rules are the same (the last change wins; a device that changed the note too keeps a conflict copy). Only the browser of the account
/// uses them; a protected note is never decrypted here: the browser sends its new encrypted bytes.
/// </summary>
public static class NoteService
{
    public const string App = CloudApps.Note;
    public const string NotesCollection = "notes";
    public const string FoldersCollection = "folders";
    public const int MaxTitleLength = 200;

    /// <summary>The longest text: what fits into one item of the sync with its path and JSON around it.</summary>
    public const int MaxContentLength = SyncContract.MaxDataLength - 20_000;

    /// <summary>The characters no file name on Windows or macOS may have (ThixNote refuses such a path).</summary>
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly byte[] Magic = "QNSEC1"u8.ToArray();

    /// <summary>magic (6) + iterations (4) + salt (16) + nonce (12) + tag (16): the head of an encrypted file of ThixNote.</summary>
    private const int EncryptedHeaderSize = 54;

    public static string Extension(NoteKind kind) => kind switch
    {
        NoteKind.Markdown => ".md",
        NoteKind.Text => ".txt",
        _ => ".txts",
    };

    public static string KindName(NoteKind kind) => kind switch
    {
        NoteKind.Markdown => "Markdown",
        NoteKind.Text => "Text",
        _ => "Geschützt",
    };

    public static string KindIcon(NoteKind kind) => kind switch
    {
        NoteKind.Protected => "lock",
        NoteKind.Markdown => "file-text",
        _ => "file",
    };

    // ---- reading ----

    public static async Task<NoteListResult> ListAsync(AppDbContext db, Actor actor, NoteQuery query, CancellationToken ct = default)
    {
        Require(actor);
        var notes = await LoadAsync(db, actor.UserId, ct);
        var folders = await FoldersAsync(db, actor, notes, ct);

        IEnumerable<NoteItem> filtered = notes;
        if (!string.IsNullOrWhiteSpace(query.Folder))
        {
            var folder = query.Folder.Trim().Trim('/');
            filtered = filtered.Where(n => SamePath(n.Folder, folder) || n.Folder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var words = query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(n => words.All(w =>
                n.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || (n.Content?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
        }

        filtered = (query.Sort, query.Descending) switch
        {
            ("title", false) => filtered.OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase),
            ("title", true) => filtered.OrderByDescending(n => n.Title, StringComparer.CurrentCultureIgnoreCase),
            ("folder", false) => filtered.OrderBy(n => n.Folder, StringComparer.CurrentCultureIgnoreCase).ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase),
            ("folder", true) => filtered.OrderByDescending(n => n.Folder, StringComparer.CurrentCultureIgnoreCase).ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase),
            (_, false) => filtered.OrderBy(n => n.UpdatedDate),
            _ => filtered.OrderByDescending(n => n.UpdatedDate),
        };

        return new NoteListResult(filtered.ToList(), notes.Count, folders, await SyncService.LatestAsync(db, actor.UserId, App, ct));
    }

    public static async Task<Outcome<NoteItem>> GetAsync(AppDbContext db, Actor actor, string? key, CancellationToken ct = default)
    {
        Require(actor);
        var row = string.IsNullOrEmpty(key) ? null : await Live(db, actor.UserId).Where(i => i.Collection == NotesCollection && i.Key == key).FirstOrDefaultAsync(ct);
        return row is not null && ToItem(row) is { } item ? Outcome<NoteItem>.Success(item) : Outcome<NoteItem>.Missing();
    }

    /// <summary>Every folder of the account: those ThixNote keeps (also empty ones) and those the notes lie in, with every folder above them.</summary>
    public static async Task<IReadOnlyList<string>> FoldersAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        Require(actor);
        return await FoldersAsync(db, actor, await LoadAsync(db, actor.UserId, ct), ct);
    }

    /// <summary>The latest revision of the notes (the live list waits for a higher one).</summary>
    public static Task<long> WaitAsync(AppDbContext db, Actor actor, long since, TimeSpan timeout, CancellationToken ct)
    {
        Require(actor);
        return SyncService.WaitAsync(db, actor, App, since, timeout, ct);
    }

    // ---- writing ----

    /// <summary>
    /// Creates a note or changes one (title, folder, text; the kind of an existing note stays). For a protected note only the title and the folder change:
    /// its text is changed with <see cref="SaveProtectedAsync"/>. When the text changed elsewhere since the page was opened, nothing is saved.
    /// </summary>
    public static async Task<Outcome<string>> SaveAsync(AppDbContext db, Actor actor, NoteInput input, CancellationToken ct = default)
    {
        Require(actor);
        var title = (input.Title ?? string.Empty).Trim();
        if (NameProblem(title) is { } titleProblem)
        {
            return Outcome<string>.Fail(titleProblem, "Title");
        }

        if (CleanFolder(input.Folder) is not { } folder)
        {
            return Outcome<string>.Fail("Ein Ordnername hat ein Zeichen, das Windows oder macOS nicht erlauben (\\ / : * ? \" < > |), oder ist zu lang.", "Folder");
        }

        NoteItem? existing = null;
        if (!string.IsNullOrEmpty(input.Key))
        {
            var found = await GetAsync(db, actor, input.Key, ct);
            if (!found.Ok)
            {
                return Outcome<string>.Missing();
            }

            existing = found.Value!;
        }

        var kind = existing?.Kind ?? (input.Kind == NoteKind.Text ? NoteKind.Text : NoteKind.Markdown);
        string? content = null;
        if (kind != NoteKind.Protected)
        {
            content = NormalizeLineEnds(input.Content ?? string.Empty, existing?.Content);
            if (content.Length > MaxContentLength)
            {
                return Outcome<string>.Fail($"Die Notiz ist zu lang (höchstens {MaxContentLength / 1000:N0} Tausend Zeichen).", "Text");
            }

            if (existing is not null && content != existing.Content && !string.IsNullOrEmpty(input.BaseHash) && input.BaseHash != existing.Hash)
            {
                return Outcome<string>.Fail("Die Notiz wurde inzwischen auf einem anderen Gerät geändert. Dein Text ist noch da: Speichern ersetzt die Änderung des Geräts durch ihn.", "Text");
            }
        }

        var path = (folder.Length == 0 ? string.Empty : folder + "/") + title + Extension(kind);
        if (await PathTakenAsync(db, actor.UserId, path, input.Key, ct))
        {
            return Outcome<string>.Fail("In diesem Ordner gibt es schon eine Notiz mit diesem Namen.", "Title");
        }

        var key = existing?.Key ?? Guid.NewGuid().ToString();
        var data = kind == NoteKind.Protected ? new NoteData(path, null, existing!.Bytes) : new NoteData(path, content);
        await PushAsync(db, actor, NoteDto(key, data), folder, ct);
        return Outcome<string>.Success(key);
    }

    /// <summary>
    /// The new text of a protected note, encrypted in the browser with the note's password (ThixNote's format: "QNSEC1", iterations, salt, nonce, tag,
    /// ciphertext). The server checks only the form, never the content: it cannot read it.
    /// </summary>
    public static async Task<Outcome> SaveProtectedAsync(AppDbContext db, Actor actor, string? key, string? bytesBase64, string? baseHash, CancellationToken ct = default)
    {
        Require(actor);
        var found = await GetAsync(db, actor, key, ct);
        if (!found.Ok || found.Value!.Kind != NoteKind.Protected)
        {
            return Outcome.Missing();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(bytesBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return Outcome.Fail("Die verschlüsselte Notiz ist nicht lesbar.", "Bytes");
        }

        if (bytes.Length < EncryptedHeaderSize || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic) || BitConverter.ToInt32(bytes, Magic.Length) <= 0)
        {
            return Outcome.Fail("Das ist keine verschlüsselte Notiz von ThixNote.", "Bytes");
        }

        var note = found.Value!;
        if (!string.IsNullOrEmpty(baseHash) && baseHash != note.Hash)
        {
            return Outcome.Fail("Die Notiz wurde inzwischen auf einem anderen Gerät geändert. Bitte neu laden.", "Bytes");
        }

        var data = new NoteData(note.Path, null, Convert.ToBase64String(bytes));
        if (JsonSerializer.Serialize(data, CloudContract.Json).Length > SyncContract.MaxDataLength)
        {
            return Outcome.Fail("Die Notiz ist zu lang für die Cloud.", "Bytes");
        }

        await PushAsync(db, actor, NoteDto(note.Key, data), note.Folder, ct);
        return Outcome.Success();
    }

    /// <summary>Deletes a note on every device (ThixNote moves it into the trash of the computer).</summary>
    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, string? key, CancellationToken ct = default)
    {
        Require(actor);
        if (!(await GetAsync(db, actor, key, ct)).Ok)
        {
            return Outcome.Missing();
        }

        var item = new SyncItemDto { Collection = NotesCollection, Key = key!, IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow };
        await SyncService.PushAsync(db, actor, App, new SyncPushRequest([item]), ct);
        return Outcome.Success();
    }

    /// <summary>A new (empty) folder on every device.</summary>
    public static async Task<Outcome<string>> CreateFolderAsync(AppDbContext db, Actor actor, string? path, CancellationToken ct = default)
    {
        Require(actor);
        if (CleanFolder(path) is not { Length: > 0 } folder)
        {
            return Outcome<string>.Fail("Bitte einen Ordnernamen ohne \\ : * ? \" < > | eingeben.", "Folder");
        }

        await PushAsync(db, actor, null, folder, ct);
        return Outcome<string>.Success(folder);
    }

    // ---- helpers ----

    /// <summary>
    /// The first line of a text that says something, at most <paramref name="max"/> characters, without Markdown marks at its start; a heading that only
    /// repeats the title is skipped.
    /// </summary>
    public static string Preview(string text, string? title = null, int max = 140)
        => NoteMarkdown.PlainLines(text, title, 1, max).FirstOrDefault() ?? string.Empty;

    /// <summary>The words of a text (anything between white space).</summary>
    public static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));

    /// <summary>
    /// The preview of the editor: the Markdown the person is writing, drawn like the page of the note. Only for the browser of the account, and only as long
    /// as a note may be.
    /// </summary>
    public static Outcome<string> Render(Actor actor, string? markdown, string? title)
    {
        Require(actor);
        markdown ??= string.Empty;
        return markdown.Length > MaxContentLength
            ? Outcome<string>.Fail($"Die Notiz ist zu lang (höchstens {MaxContentLength / 1000:N0} Tausend Zeichen).", "Text")
            : Outcome<string>.Success(NoteMarkdown.ToHtml(markdown, title));
    }

    /// <summary>What is wrong with the title of a note (a file name on every computer), or null.</summary>
    public static string? NameProblem(string title)
    {
        if (title.Length == 0)
        {
            return "Bitte einen Titel eingeben.";
        }

        if (title.Length > MaxTitleLength)
        {
            return $"Der Titel hat höchstens {MaxTitleLength} Zeichen.";
        }

        if (title.IndexOfAny(InvalidNameChars) >= 0 || title.Any(char.IsControl))
        {
            return "Der Titel ist der Dateiname der Notiz: bitte ohne \\ / : * ? \" < > |.";
        }

        return title is "." or ".." || title.StartsWith('.') || title.EndsWith('.') || title.EndsWith(' ')
            ? "Der Titel darf nicht mit einem Punkt beginnen oder mit einem Punkt oder Leerzeichen enden."
            : null;
    }

    /// <summary>A folder as people type it ("Arbeit / Projekte") in its stored form ("Arbeit/Projekte"); empty for the top; null when a part is not allowed.</summary>
    public static string? CleanFolder(string? folder)
    {
        var parts = (folder ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => NameProblem(p) is not null))
        {
            return null;
        }

        var clean = string.Join('/', parts);
        return clean.Length > 500 ? null : clean;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static NoteItem? ToItem(SyncItem row)
    {
        if (row.Data is null)
        {
            return null;
        }

        NoteData? data;
        try
        {
            data = JsonSerializer.Deserialize<NoteData>(row.Data, CloudContract.Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (data is null || string.IsNullOrWhiteSpace(data.Path) || (data.Bytes is null) == (data.Content is null))
        {
            return null;
        }

        var path = data.Path.Replace('\\', '/').Trim('/');
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : path[..slash];
        var file = slash < 0 ? path : path[(slash + 1)..];
        var kind = file.EndsWith(".txts", StringComparison.OrdinalIgnoreCase) ? NoteKind.Protected
            : file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? NoteKind.Text
            : NoteKind.Markdown;
        if ((kind == NoteKind.Protected) != (data.Bytes is not null))
        {
            return null;
        }

        var dot = file.LastIndexOf('.');
        var title = dot > 0 ? file[..dot] : file;
        var hash = row.ContentHash ?? (data.Bytes is not null ? Hash(Convert.FromBase64String(data.Bytes)) : Hash(Encoding.UTF8.GetBytes(data.Content!)));
        return new NoteItem(row.Key, path, title, folder, kind, data.Content, data.Bytes, hash, row.ClientUpdateDate, row.Source, row.Size);
    }

    private static IQueryable<SyncItem> Live(AppDbContext db, long userId)
        => db.SyncItems.AsNoTracking().Where(i => i.UserId == userId && i.App == App && i.UpdateState != SoftEntity.Deleted);

    private static async Task<List<NoteItem>> LoadAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var rows = await Live(db, userId).Where(i => i.Collection == NotesCollection).ToListAsync(ct);
        return rows.Select(ToItem).OfType<NoteItem>().ToList();
    }

    private static async Task<IReadOnlyList<string>> FoldersAsync(AppDbContext db, Actor actor, IEnumerable<NoteItem> notes, CancellationToken ct)
    {
        var kept = await Live(db, actor.UserId).Where(i => i.Collection == FoldersCollection).Select(i => i.Key).ToListAsync(ct);
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in kept.Concat(notes.Select(n => n.Folder)).Where(f => f.Length > 0))
        {
            foreach (var ancestor in Ancestors(folder))
            {
                all.Add(ancestor);
            }
        }

        return all.ToList();
    }

    /// <summary>"A/B/C" → "A", "A/B", "A/B/C".</summary>
    private static IEnumerable<string> Ancestors(string folder)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            yield return string.Join('/', parts.Take(i));
        }
    }

    private static async Task<bool> PathTakenAsync(AppDbContext db, long userId, string path, string? key, CancellationToken ct)
        => (await LoadAsync(db, userId, ct)).Any(n => n.Key != key && SamePath(n.Path, path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A textarea sends CRLF: a note that had LF (or is new) keeps LF, one that had CRLF keeps CRLF, so the devices see no change that is none.</summary>
    private static string NormalizeLineEnds(string content, string? before)
    {
        var lf = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        return before is not null && before.Contains("\r\n", StringComparison.Ordinal) ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    private static SyncItemDto NoteDto(string key, NoteData data) => new()
    {
        Collection = NotesCollection,
        Key = key,
        Data = JsonSerializer.Serialize(data, CloudContract.Json),
        ContentHash = data.Bytes is not null ? Hash(Convert.FromBase64String(data.Bytes)) : Hash(Encoding.UTF8.GetBytes(data.Content ?? string.Empty)),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Pushes a note (or nothing) and every folder above it that the account does not keep yet, so empty folders show on every device too.</summary>
    private static async Task PushAsync(AppDbContext db, Actor actor, SyncItemDto? note, string folder, CancellationToken ct)
    {
        var items = new List<SyncItemDto>();
        if (folder.Length > 0)
        {
            var kept = (await Live(db, actor.UserId).Where(i => i.Collection == FoldersCollection).Select(i => i.Key).ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            items.AddRange(Ancestors(folder).Where(f => !kept.Contains(f))
                .Select(f => new SyncItemDto { Collection = FoldersCollection, Key = f, Data = "{}", UpdatedAt = DateTimeOffset.UtcNow }));
        }

        if (note is not null)
        {
            items.Add(note);
        }

        if (items.Count > 0)
        {
            await SyncService.PushAsync(db, actor, App, new SyncPushRequest(items), ct);
        }
    }

    /// <summary>The pages of the account only: a device of ThixNote syncs through the general sync, not through these functions.</summary>
    private static void Require(Actor actor)
    {
        if (actor.DeviceId is not null)
        {
            throw new NotAllowedException("Nur im Browser.");
        }
    }
}
