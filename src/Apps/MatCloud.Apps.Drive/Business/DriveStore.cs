using System.Security.Cryptography;

namespace MatCloud.Apps.Drive.Business;

/// <summary>Bytes that arrived in the temporary folder, hashed while they were written: not yet in the store (<see cref="DriveStore.Keep"/>).</summary>
public sealed record ArrivedFile(string TempPath, long Size, string Sha256);

/// <summary>The bytes were more than the limit allowed (they were not kept).</summary>
public sealed class TooLargeException() : Exception("Die Datei ist zu groß.");

/// <summary>
/// The bytes of the Drive in the data volume: one file per content, named by its SHA-256, <c>{data}/drive/blobs/ab/cd/&lt;sha256&gt;</c>. The same
/// content is kept once (two versions or two files with the same bytes share it); the table DriveBlob knows which blobs exist, the housekeeping removes
/// those no version names any more. Bytes arrive in <c>{data}/drive/temp</c> first and are moved into place when they are complete, so a broken upload
/// never leaves half a blob. Nothing is held in memory: everything is streamed.
/// </summary>
public sealed class DriveStore
{
    private const int BufferSize = 81920;

    public DriveStore(IConfiguration configuration)
        : this(Path.Combine(Path.GetFullPath(configuration["DataDirectory"] ?? "/data"), "drive"))
    {
    }

    public DriveStore(string root)
    {
        Blobs = Path.Combine(root, "blobs");
        Temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(Blobs);
        Directory.CreateDirectory(Temp);
    }

    public string Blobs { get; }

    /// <summary>The temporary folder (uploads in pieces, bytes on their way into the store).</summary>
    public string Temp { get; }

    public string PathOf(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("not a SHA-256", nameof(sha256));
        }

        return Path.Combine(Blobs, sha256[..2], sha256[2..4], sha256);
    }

    public bool Exists(string sha256) => File.Exists(PathOf(sha256));

    public Stream OpenRead(string sha256) => new FileStream(PathOf(sha256), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

    /// <summary>The file of a piecewise upload in the temporary folder.</summary>
    public string UploadPath(Guid token) => Path.Combine(Temp, token.ToString("N") + ".upload");

    /// <summary>Writes a stream into a temporary file while hashing it; at most <paramref name="maxBytes"/> (else <see cref="TooLargeException"/>, nothing stays).</summary>
    public async Task<ArrivedFile> ReceiveAsync(Stream input, long maxBytes, CancellationToken ct)
    {
        var temp = Path.Combine(Temp, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw new TooLargeException();
                    }

                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            return new ArrivedFile(temp, size, Convert.ToHexStringLower(sha.GetHashAndReset()));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Hashes a finished piecewise upload (read once, streamed).</summary>
    public static async Task<ArrivedFile> HashAsync(string tempPath, CancellationToken ct)
    {
        await using var file = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        return new ArrivedFile(tempPath, file.Length, hash);
    }

    /// <summary>
    /// Puts arrived bytes into the store: <paramref name="isNew"/> (the database had no blob of this content, see
    /// <see cref="DriveFiles"/>) moves the file into place, else the temporary file goes (the content is there already).
    /// </summary>
    public void Keep(ArrivedFile arrived, bool isNew)
    {
        if (!isNew && Exists(arrived.Sha256))
        {
            TryDelete(arrived.TempPath);
            return;
        }

        var target = PathOf(arrived.Sha256);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Move(arrived.TempPath, target, overwrite: true);
    }

    public void Delete(string sha256) => TryDelete(PathOf(sha256));

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the housekeeping tries again
        }
    }

    /// <summary>Temporary files older than <paramref name="age"/> go (broken uploads); answers how many.</summary>
    public int CleanTemp(TimeSpan age, IReadOnlySet<string> keep)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(Temp))
        {
            if (!keep.Contains(System.IO.Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - age)
            {
                TryDelete(file);
                count++;
            }
        }

        return count;
    }
}
