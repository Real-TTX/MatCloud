using MatCloud.Core.Business;
using MatCloud.Core.Data;

namespace MatCloud.Apps.Contacts.Business;

/// <summary>What a file to import is: vCard (contacts at once) or CSV (its columns are matched first).</summary>
public enum ImportKind
{
    VCard,
    Csv,
}

/// <summary>
/// Importing a file in the browser. A vCard file is imported at once; a CSV file waits (in <c>{data}/contacts/imports/&lt;user&gt;</c>, at most a day) while
/// the person matches its columns to fields, then it is imported and removed.
/// </summary>
public static class ContactImports
{
    /// <summary>The largest file to import (bytes).</summary>
    public const int MaxFileBytes = 15 * 1024 * 1024;

    /// <summary>vCard by its name or its first line, else CSV.</summary>
    public static ImportKind KindOf(string? fileName, byte[] bytes)
    {
        var name = (fileName ?? string.Empty).ToLowerInvariant();
        if (name.EndsWith(".vcf", StringComparison.Ordinal) || name.EndsWith(".vcard", StringComparison.Ordinal))
        {
            return ImportKind.VCard;
        }

        var head = VCard.TextOf(bytes.Length > 4096 ? bytes[..4096] : bytes).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) ? ImportKind.VCard : ImportKind.Csv;
    }

    /// <summary>Keeps a CSV file until its columns are matched; answers its token.</summary>
    public static async Task<Outcome<string>> KeepAsync(AppDbContext db, Actor actor, ContactPhotoStore store, byte[] bytes, CancellationToken ct = default)
    {
        await ContactBook.RequireAsync(db, actor, write: true, ct);
        if (bytes.Length > MaxFileBytes)
        {
            return Outcome<string>.Fail($"Eine Datei hat höchstens {MaxFileBytes / 1024 / 1024} MB.", "File");
        }

        var token = Guid.NewGuid().ToString("N");
        var folder = FolderOf(store, actor.UserId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, token + ".csv"), bytes, ct);
        return Outcome<string>.Success(token);
    }

    /// <summary>The CSV file of a token (the account's own), or missing.</summary>
    public static async Task<Outcome<byte[]>> ReadAsync(AppDbContext db, Actor actor, ContactPhotoStore store, string? token, CancellationToken ct = default)
    {
        await ContactBook.RequireAsync(db, actor, write: true, ct);
        var path = PathOf(store, actor.UserId, token);
        return path is not null && File.Exists(path) ? Outcome<byte[]>.Success(await File.ReadAllBytesAsync(path, ct)) : Outcome<byte[]>.Missing();
    }

    public static void Forget(ContactPhotoStore store, Actor actor, string? token)
    {
        if (PathOf(store, actor.UserId, token) is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // the housekeeping removes it after a day
            }
        }
    }

    /// <summary>Files older than a day go (an import nobody finished).</summary>
    public static int CleanUp(ContactPhotoStore store)
    {
        var root = Path.Combine(Path.GetDirectoryName(store.Root)!, "imports");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // next hour
                }
            }
        }

        return removed;
    }

    private static string FolderOf(ContactPhotoStore store, long userId)
        => Path.Combine(Path.GetDirectoryName(store.Root)!, "imports", userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string? PathOf(ContactPhotoStore store, long userId, string? token)
        => token is { Length: 32 } && token.All(Uri.IsHexDigit) ? Path.Combine(FolderOf(store, userId), token + ".csv") : null;
}
