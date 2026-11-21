using System.Security.Cryptography;
using MatCloud.Contract.Contacts;

namespace MatCloud.Apps.Contacts.Business;

/// <summary>
/// The pictures of contacts in the data volume, once per account and content: <c>{data}/contacts/photos/&lt;user id&gt;/ab/&lt;sha256&gt;</c> (like the
/// blobs of the Drive, but per account: an account never reaches another's pictures, and deleting its data removes its folder). A picture is written to
/// a temporary name first and moved into place, so a broken write never leaves half a picture. The housekeeping removes pictures no contact names any more
/// (<see cref="ContactHousekeeping"/>).
/// </summary>
public sealed class ContactPhotoStore
{
    public ContactPhotoStore(IConfiguration configuration)
        : this(Path.Combine(Path.GetFullPath(configuration["DataDirectory"] ?? "/data"), "contacts", "photos"))
    {
    }

    public ContactPhotoStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string FolderOf(long userId) => Path.Combine(Root, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string PathOf(long userId, string id)
    {
        if (!ContactsContract.IsValidPhotoId(id))
        {
            throw new ArgumentException("not a picture id", nameof(id));
        }

        return Path.Combine(FolderOf(userId), id[..2], id);
    }

    public bool Exists(long userId, string id) => ContactsContract.IsValidPhotoId(id) && File.Exists(PathOf(userId, id));

    /// <summary>Keeps the bytes (the same bytes again change nothing) and answers their id.</summary>
    public async Task<string> KeepAsync(long userId, byte[] bytes, CancellationToken ct)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathOf(userId, id);
        if (File.Exists(path))
        {
            return id;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        return id;
    }

    public async Task<byte[]?> ReadAsync(long userId, string id, CancellationToken ct)
        => Exists(userId, id) ? await File.ReadAllBytesAsync(PathOf(userId, id), ct) : null;

    /// <summary>The ids and sizes of the account's pictures.</summary>
    public IEnumerable<(string Id, long Size, DateTime Written)> List(long userId)
    {
        var folder = FolderOf(userId);
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (ContactsContract.IsValidPhotoId(name))
            {
                var info = new FileInfo(file);
                yield return (name, info.Length, info.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>The accounts that have pictures.</summary>
    public IEnumerable<long> Users()
        => Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Select(n => long.TryParse(n, out var id) ? id : 0).Where(id => id > 0)
            : [];

    public void Delete(long userId, string id)
    {
        try
        {
            File.Delete(PathOf(userId, id));
        }
        catch (IOException)
        {
            // the housekeeping tries again
        }
    }

    /// <summary>Every picture of the account goes.</summary>
    public void DeleteAll(long userId)
    {
        var folder = FolderOf(userId);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // the housekeeping of a later hour finds the pictures without contacts
        }
    }
}
