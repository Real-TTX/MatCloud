using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;

namespace MatCloud.Tests;

/// <summary>The general sync (the apps without a module of their own) against a real PostgreSQL (MATCLOUD_TEST_DB).</summary>
public sealed class GeneralSyncTests
{
    private static Actor Device(User user, string app = CloudApps.Note, string name = "PC-1", long id = 1) => new(user.Id, user.IsAdmin, id, name, app);

    private static SyncItemDto Item(string key, string? data, DateTimeOffset? at = null, bool deleted = false, string collection = "notes") => new()
    {
        Collection = collection,
        Key = key,
        Data = deleted ? null : data,
        UpdatedAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        IsDeleted = deleted,
    };

    [DbFact]
    public async Task APushIsReadBackInRevisionOrderByTheOtherDevices()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var mac = Device(anna, name: "Mac", id: 2);

        var push = await SyncService.PushAsync(test.Db, pc, CloudApps.Note, new SyncPushRequest([Item("a", "{\"t\":1}"), Item("b", "zwei", collection: "folders")]));
        Assert.All(push.Results, r => Assert.True(r.Applied));
        Assert.True(push.Results[1].Revision > push.Results[0].Revision);

        var changes = await SyncService.ChangesAsync(test.NewContext(), mac, CloudApps.Note, 0, 100);
        Assert.Equal(["a", "b"], changes.Items.Select(i => i.Key));
        Assert.Equal("PC-1", changes.Items[0].Source);
        Assert.Equal(push.Latest, changes.Latest);
        Assert.False(changes.HasMore);
        Assert.Empty((await SyncService.ChangesAsync(test.NewContext(), mac, CloudApps.Note, changes.Latest, 100)).Items);

        var page = await SyncService.ChangesAsync(test.NewContext(), mac, CloudApps.Note, 0, 1);
        Assert.Single(page.Items);
        Assert.True(page.HasMore);
    }

    [DbFact]
    public async Task TheLastWriterWinsAndTheLoserGetsTheWinner()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var now = DateTimeOffset.UtcNow;
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([Item("a", "neu", now)]));

        var late = await SyncService.PushAsync(test.NewContext(), Device(anna, name: "Mac", id: 2), CloudApps.Note, new SyncPushRequest([Item("a", "alt", now.AddMinutes(-5))]));
        var result = Assert.Single(late.Results);
        Assert.False(result.Applied);
        Assert.Equal("neu", result.Current!.Data);

        var changes = await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Note, 0, 100);
        Assert.Equal("neu", Assert.Single(changes.Items).Data);
    }

    [DbFact]
    public async Task ADeleteIsATombstoneAndALaterWriteBringsTheItemBack()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SyncService.PushAsync(test.Db, pc, CloudApps.Note, new SyncPushRequest([Item("a", "text", start)]));
        var deleted = await SyncService.PushAsync(test.NewContext(), pc, CloudApps.Note, new SyncPushRequest([Item("a", null, start.AddMinutes(1), deleted: true)]));

        var tombstone = Assert.Single((await SyncService.ChangesAsync(test.NewContext(), pc, CloudApps.Note, 0, 100)).Items);
        Assert.True(tombstone.IsDeleted);
        Assert.Null(tombstone.Data);
        Assert.Equal(0, (await SyncService.UsageAsync(test.NewContext(), anna.Id, CloudApps.Note)).Items);

        await SyncService.PushAsync(test.NewContext(), pc, CloudApps.Note, new SyncPushRequest([Item("a", "wieder da", start.AddMinutes(2))]));
        var back = Assert.Single((await SyncService.ChangesAsync(test.NewContext(), pc, CloudApps.Note, deleted.Latest, 100)).Items);
        Assert.False(back.IsDeleted);
        Assert.Equal("wieder da", back.Data);
        Assert.Equal(1, (await SyncService.UsageAsync(test.NewContext(), anna.Id, CloudApps.Note)).Items);
    }

    [DbFact]
    public async Task AppsAndAccountsSeeOnlyTheirOwnItems()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var ben = await test.AddUserAsync("ben");
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Note, new SyncPushRequest([Item("a", "Notiz")]));

        Assert.Empty((await SyncService.ChangesAsync(test.NewContext(), Device(anna, CloudApps.Tunnel), CloudApps.Tunnel, 0, 100)).Items);
        Assert.Empty((await SyncService.ChangesAsync(test.NewContext(), Device(ben), CloudApps.Note, 0, 100)).Items);

        // a device of one app may not read another app; the app with a module has no general sync
        await Assert.ThrowsAsync<NotAllowedException>(() => SyncService.ChangesAsync(test.NewContext(), Device(anna, CloudApps.Tunnel), CloudApps.Note, 0, 100));
        await Assert.ThrowsAsync<NotAllowedException>(() => SyncService.ChangesAsync(test.NewContext(), Device(anna, CloudApps.Clip), CloudApps.Clip, 0, 100));
    }

    [DbFact]
    public async Task TheLongPollWakesUpOnAChangeOfTheApp()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var waiting = SyncService.WaitAsync(test.NewContext(), Device(anna, name: "Mac", id: 2), CloudApps.Note, 0, TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        // a change of another app does not wake it
        await SyncService.PushAsync(test.NewContext(), Device(anna, CloudApps.Tunnel), CloudApps.Tunnel, new SyncPushRequest([Item("x", "y")]));
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Note, new SyncPushRequest([Item("a", "b")]));
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)) > 0);
    }

    [DbFact]
    public async Task AWakeUpWithoutANewRevisionKeepsTheLongPollWaiting()
    {
        // The signals are in memory and named "app/account id": another database with an account of the same id (the tests run in parallel)
        // wakes the same stream. That is no change here: the long poll waits on (it used to answer at once, with nothing new).
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var mac = Device(anna, name: "Mac", id: 2);
        var waiting = SyncService.WaitAsync(test.NewContext(), mac, CloudApps.Note, 0, TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(200);
        for (var round = 0; round < 3; round++)
        {
            RevisionSignals.Notify(Revisions.Stream(CloudApps.Note, anna.Id));
            await Task.Delay(100);
        }

        Assert.False(waiting.IsCompleted);
        await SyncService.PushAsync(test.NewContext(), Device(anna), CloudApps.Note, new SyncPushRequest([Item("a", "b")]));
        var latest = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(latest > 0);

        // the data deleted: the revision falls below "since", the long poll ends at once (the device then finds itself signed out)
        await AppService.DeleteDataAsync(test.NewContext(), anna.Id, CloudApps.Note, CancellationToken.None);
        var afterDelete = SyncService.WaitAsync(test.NewContext(), mac, CloudApps.Note, latest, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(0, await afterDelete.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [DbFact]
    public async Task DeletingTheDataOfAnAppSignsItsDevicesOut()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        var login = await DeviceService.LoginAsync(test.Db, new DeviceLoginRequest("anna", "anna-password", "PC-1", "windows", "1.0", CloudApps.Note), null, "test");
        var clip = await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("anna", "anna-password", "PC-1", "windows", "1.0", CloudApps.Clip), null, "test");
        var device = (await DeviceService.AuthenticateAsync(test.NewContext(), login.Value!.Token))!;
        await SyncService.PushAsync(test.NewContext(), device.Actor, CloudApps.Note, new SyncPushRequest([Item("a", "Notiz")]));

        var browser = new Actor(device.UserId, false);
        Assert.True((await AppService.DeleteMyDataAsync(test.NewContext(), browser, CloudApps.Note)).Ok);
        Assert.Equal(0, (await SyncService.UsageAsync(test.NewContext(), device.UserId, CloudApps.Note)).Items);
        Assert.Null(await DeviceService.AuthenticateAsync(test.NewContext(), login.Value.Token));
        Assert.NotNull(await DeviceService.AuthenticateAsync(test.NewContext(), clip.Value!.Token));

        // a device cannot delete the data of its app this way
        Assert.True((await AppService.DeleteMyDataAsync(test.NewContext(), device.Actor, CloudApps.Note)).NotFound);
    }

    [Fact]
    public void APushIsCheckedItemByItem()
    {
        Assert.Null(SyncService.Validate(new SyncPushRequest([Item("a", "b")])));
        Assert.Equal("items", SyncService.Validate(null)!.Value.Field);
        Assert.Equal("items[0].collection", SyncService.Validate(new SyncPushRequest([Item("a", "b", collection: "Notes!")]))!.Value.Field);
        Assert.Equal("items[0].key", SyncService.Validate(new SyncPushRequest([Item("", "b")]))!.Value.Field);
        Assert.Equal("items[0].key", SyncService.Validate(new SyncPushRequest([Item(new string('k', 201), "b")]))!.Value.Field);
        Assert.Equal("items[0].data", SyncService.Validate(new SyncPushRequest([Item("a", null)]))!.Value.Field);
        Assert.Equal("items[0].data", SyncService.Validate(new SyncPushRequest([Item("a", new string('x', SyncContract.MaxDataLength + 1))]))!.Value.Field);
        Assert.Null(SyncService.Validate(new SyncPushRequest([Item("a", null, deleted: true)])));
        Assert.Equal("items", SyncService.Validate(new SyncPushRequest([Item("a", "1"), Item("a", "2")]))!.Value.Field);
        Assert.Equal("items", SyncService.Validate(new SyncPushRequest(Enumerable.Range(0, 201).Select(i => Item("k" + i, "x")).ToList()))!.Value.Field);
    }
}
