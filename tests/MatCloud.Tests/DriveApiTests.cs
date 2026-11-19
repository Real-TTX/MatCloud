using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MatCloud.Apps.Drive.Business;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MatCloud.Tests;

/// <summary>
/// The device API of the Drive over HTTP and the calls of <see cref="MatCloudClient"/> a desktop app makes (ThixWrite, ThixCalc, ThixShow): the whole
/// app in memory against a real PostgreSQL (MATCLOUD_TEST_DB).
/// </summary>
public sealed class DriveApiTests
{
    private static async Task<MatCloudClient> ClientAsync(WebApplicationFactory<Program> app, string appId, string device = "ANNA-PC")
    {
        var (_, login) = await TestApp.DeviceAsync(app, appId, device);
        return new MatCloudClient(app.Server.BaseAddress.ToString(), login.Token, $"Thix{appId}/1.0.0 (windows)", app.Server.CreateHandler());
    }

    private static MemoryStream Text(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<string> DownloadTextAsync(MatCloudClient client, long id)
    {
        using var target = new MemoryStream();
        await client.DownloadDriveFileAsync(id, target, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(target.ToArray());
    }

    [DbFact]
    public async Task ThixWrite_opens_and_saves_a_document_and_is_told_when_somebody_saved_in_between()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        using var pc = await ClientAsync(app, CloudApps.Write, "ANNA-PC");
        using var mac = await ClientAsync(app, CloudApps.Write, "ANNA-MAC");
        var ct = CancellationToken.None;

        var folder = await pc.CreateDriveFolderAsync(null, "Briefe", ct);
        var created = await pc.UploadDriveFileAsync(folder.Id, "Brief.docx", Text("Entwurf"), DriveNameConflict.Fail, ct);
        Assert.Equal("/Briefe", created.Location);

        // both open it
        using var pcBytes = new MemoryStream();
        var pcTag = await pc.DownloadDriveFileAsync(created.Id, pcBytes, ct);
        var macTag = (await mac.ListDriveFolderAsync(folder.Id, ct)).Items.Single().ETag!;
        Assert.Equal(created.ETag, pcTag);
        Assert.Equal(pcTag, macTag);

        // the Mac saves, then the PC: 412 with the file as it is now
        var macSaved = await mac.SaveDriveFileAsync(created.Id, Text("Vom Mac"), macTag, ct);
        var conflict = await Assert.ThrowsAsync<DriveConflictException>(() => pc.SaveDriveFileAsync(created.Id, Text("Vom PC"), pcTag, ct));
        Assert.True(conflict.IsChangedMeanwhile);
        Assert.Equal(macSaved.ETag, conflict.Current!.ETag);
        Assert.Equal("ANNA-MAC", conflict.Current.ModifiedBy);

        // "Als Kopie speichern" and "Überschreiben"
        var copy = await pc.UploadDriveFileAsync(folder.Id, "Brief.docx", Text("Vom PC"), DriveNameConflict.Rename, ct);
        Assert.Equal("Brief (2).docx", copy.Name);
        var nameTaken = await Assert.ThrowsAsync<DriveConflictException>(() => pc.UploadDriveFileAsync(folder.Id, "Brief.docx", Text("x"), DriveNameConflict.Fail, ct));
        Assert.False(nameTaken.IsChangedMeanwhile);
        Assert.Equal(created.Id, nameTaken.Current!.Id);
        var overwritten = await pc.SaveDriveFileAsync(created.Id, Text("Vom PC"), conflict.Current.ETag!, ct);
        Assert.Equal(3, overwritten.Version);
        Assert.Equal("Vom PC", await DownloadTextAsync(mac, created.Id));

        // recent, versions, quota
        var recent = await mac.GetDriveRecentAsync(ct);
        Assert.Equal(created.Id, recent.Items[0].Id);
        var versions = await pc.ListDriveVersionsAsync(created.Id, ct);
        Assert.Equal([3, 2, 1], versions.Versions.Select(v => v.Number));
        using var first = new MemoryStream();
        await pc.DownloadDriveFileAsync(created.Id, first, ct, versions.Versions[2].Id);
        Assert.Equal("Entwurf", System.Text.Encoding.UTF8.GetString(first.ToArray()));
        var quota = await pc.GetDriveQuotaAsync(ct);
        Assert.Equal("Entwurf".Length + "Vom Mac".Length + 2 * "Vom PC".Length, quota.UsedBytes);
        Assert.Equal(DriveContract.MaxSimpleUploadBytes, quota.MaxSimpleUploadBytes);

        // rename, move, star, trash, restore, delete for good
        var renamed = await pc.RenameDriveItemAsync(copy.Id, "Brief Kopie.docx", ct);
        var moved = await pc.MoveDriveItemAsync(renamed.Id, null, ct);
        Assert.Null(moved.ParentId);
        await pc.StarDriveItemAsync(moved.Id, true, ct);
        Assert.Equal(moved.Id, (await mac.GetDriveStarredAsync(ct)).Items.Single().Id);
        Assert.True((await pc.TrashDriveItemAsync(moved.Id, ct)).IsTrashed);
        Assert.Equal(moved.Id, (await pc.GetDriveTrashAsync(ct)).Items.Single().Id);
        Assert.False((await pc.RestoreDriveItemAsync(moved.Id, ct)).IsTrashed);
        await pc.TrashDriveItemAsync(moved.Id, ct);
        await pc.DeleteDriveItemAsync(moved.Id, ct);
        var gone = await Assert.ThrowsAsync<CloudException>(() => pc.GetDriveItemAsync(moved.Id, ct));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Single((await pc.SearchDriveAsync("brief.docx", ct)).Items);
    }

    [DbFact]
    public async Task Only_ThixDrive_and_the_office_apps_reach_the_Drive_and_switching_it_off_stops_them()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        var admin = await test.AddUserAsync("admin", admin: true);
        await using var app = TestApp.Create(test);
        var ct = CancellationToken.None;

        foreach (var appId in new[] { CloudApps.Drive, CloudApps.Write, CloudApps.Calc, CloudApps.Show })
        {
            using var client = await ClientAsync(app, appId);
            await client.CreateDriveFolderAsync(null, appId, ct);
        }

        foreach (var appId in new[] { CloudApps.Clip, CloudApps.Note, CloudApps.Calendar, CloudApps.Fred })
        {
            using var client = await ClientAsync(app, appId);
            var refused = await Assert.ThrowsAsync<CloudException>(() => client.ListDriveFolderAsync(null, ct));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var upload = await Assert.ThrowsAsync<CloudException>(() => client.UploadDriveFileAsync(null, "x.txt", Text("x"), DriveNameConflict.Fail, ct));
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        }

        // a device of the Drive does not reach the routes of another module
        var (drive, _) = await TestApp.DeviceAsync(app, CloudApps.Drive, "PC-2");
        Assert.Equal(HttpStatusCode.Forbidden, (await drive.GetAsync(Contract.Clip.ClipContract.Settings)).StatusCode);

        using var write = await ClientAsync(app, CloudApps.Write);
        Assert.Equal(4, (await write.ListDriveFolderAsync(null, ct)).Total);
        Assert.True((await AppService.SetEnabledAsync(test.NewContext(), new Actor(admin.Id, true), CloudApps.Drive, false)).Ok);
        var off = await Assert.ThrowsAsync<CloudException>(() => write.ListDriveFolderAsync(null, ct));
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Contains("ThixDrive", off.Message);

        // ThixWrite itself is still on: its account and its general sync work
        Assert.Equal(CloudApps.Write, (await write.GetAccountAsync(ct)).App);
        Assert.True((await AppService.SetEnabledAsync(test.NewContext(), new Actor(admin.Id, true), CloudApps.Drive, true)).Ok);
        Assert.Equal(4, (await write.ListDriveFolderAsync(null, ct)).Total);
    }

    [DbFact]
    public async Task The_status_codes_of_the_contract()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        var (http, _) = await TestApp.DeviceAsync(app, CloudApps.Calc, "PC");

        var created = await http.PostAsync(DriveContract.Files + "?name=Zahlen.xlsx", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("1", created.Headers.GetValues(CloudContract.ProtocolHeader).Single());
        var item = (await created.Content.ReadFromJsonAsync<DriveItem>(CloudContract.Json))!;

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await http.PutAsync(DriveContract.Content(item.Id), new ByteArrayContent([4]))).StatusCode);
        var stale = new HttpRequestMessage(HttpMethod.Put, DriveContract.Content(item.Id)) { Content = new ByteArrayContent([4]) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"999999\"");
        var refused = await http.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.Equal(item.ETag, (await refused.Content.ReadFromJsonAsync<DriveConflict>(CloudContract.Json))!.Current!.ETag);

        var taken = await http.PostAsync(DriveContract.Files + "?name=zahlen.XLSX", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("name", (await taken.Content.ReadFromJsonAsync<ApiError>(CloudContract.Json))!.Field);
        var bad = await http.PostAsync(DriveContract.Files + "?name=" + Uri.EscapeDataString("../x.txt"), new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(DriveContract.Item(987654))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(DriveContract.Items + "?parentId=987654")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync(DriveContract.Delete(item.Id))).StatusCode);

        var tooBig = await http.PostAsJsonAsync(DriveContract.Uploads, new DriveUploadRequest(DriveLimits.Current.MaxFileBytes + 1, Name: "riesig.bin"), CloudContract.Json);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

        // ranges and the ETag of a download
        var whole = await http.GetAsync(DriveContract.Content(item.Id));
        Assert.Equal(item.ETag, whole.Headers.ETag!.ToString());
        Assert.Equal("bytes", whole.Headers.AcceptRanges.Single());
        var range = new HttpRequestMessage(HttpMethod.Get, DriveContract.Content(item.Id));
        range.Headers.Range = new RangeHeaderValue(1, 2);
        var part = await http.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(new byte[] { 2, 3 }, await part.Content.ReadAsByteArrayAsync());
        var unchanged = new HttpRequestMessage(HttpMethod.Get, DriveContract.Content(item.Id));
        unchanged.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(item.ETag!));
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(unchanged)).StatusCode);
    }

    [DbFact]
    public async Task A_large_file_goes_up_in_pieces_and_comes_back_whole()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        using var show = await ClientAsync(app, CloudApps.Show);
        var ct = CancellationToken.None;

        // above the single request: in pieces (from a stream that cannot seek: the client spools it to a temporary file)
        var size = DriveContract.MaxSimpleUploadBytes + 3 * 1024 * 1024 + 17;
        var reported = new List<long>();
        var item = await show.UploadDriveFileAsync(null, "Vortrag.pptx", new PatternStream(size), DriveNameConflict.Fail, ct, new Progress<long>(reported.Add));
        Assert.Equal(size, item.Size);
        Assert.Equal(PatternStream.Hash(size), item.Sha256);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var sink = new HashingSink(hash);
        await show.DownloadDriveFileAsync(item.Id, sink, ct);
        Assert.Equal(size, sink.Length);
        Assert.Equal(item.Sha256, Convert.ToHexStringLower(hash.GetHashAndReset()));

        // a new version in pieces with If-Match
        var saved = await show.SaveDriveFileAsync(item.Id, new PatternStream(size, seed: 3), item.ETag!, ct);
        Assert.Equal(2, saved.Version);
        Assert.Equal(PatternStream.Hash(size, seed: 3), saved.Sha256);
    }

    /// <summary>A stream of <c>size</c> bytes made on the fly (it cannot seek): a large file without a large array.</summary>
    internal sealed class PatternStream(long size, int seed = 1) : Stream
    {
        private long _position;

        public static string Hash(long size, int seed = 1)
        {
            using var stream = new PatternStream(size, seed);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, size - _position);
            for (var i = 0; i < n; i++)
            {
                buffer[i] = (byte)((_position + i) * 31 + seed);
            }

            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Where a download goes when only its hash matters.</summary>
    private sealed class HashingSink(IncrementalHash hash) : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            _length += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Runs alone (no other test meanwhile): the bytes the whole process allocates are measured.</summary>
[CollectionDefinition(nameof(DriveMemoryTests), DisableParallelization = true)]
public sealed class DriveMemoryCollection;

[Collection(nameof(DriveMemoryTests))]
public sealed class DriveMemoryTests
{
    /// <summary>The business layer streams: 192 MB arrive with far less than that allocated (the buffer of the store, never the file).</summary>
    [DbFact]
    public async Task A_file_is_streamed_into_the_store_not_held_in_memory()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var store = new DriveStore(Directory.CreateTempSubdirectory("matcloud-drive-").FullName);
        var me = new Actor(anna.Id, false);
        const long size = 192L * 1024 * 1024;

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var arrived = await store.ReceiveAsync(new DriveApiTests.PatternStream(size), long.MaxValue, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(size, arrived.Size);
        Assert.True(allocated < 16 * 1024 * 1024, $"{allocated} bytes allocated for a file of {size} bytes");
        File.Delete(arrived.TempPath);

        before = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = await DriveFiles.CreateAsync(test.Db, store, me, null, "Film.mp4", DriveNameConflict.Fail, new DriveApiTests.PatternStream(size), size, long.MaxValue);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(outcome.Ok, outcome.Error);
        Assert.True(allocated < 32 * 1024 * 1024, $"{allocated} bytes allocated for a file of {size} bytes");
        Assert.Equal(DriveApiTests.PatternStream.Hash(size), outcome.Value!.Sha256);
    }
}
