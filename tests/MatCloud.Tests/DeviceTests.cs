using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Tests;

/// <summary>Devices per app, the sign-in in the browser and the switch of an app, against a real PostgreSQL (MATCLOUD_TEST_DB).</summary>
public sealed class DeviceTests
{
    private static DeviceLinkRequest Link(string app = CloudApps.Note) => new("ANNA-PC", "windows", "1.2.3", app);

    [DbFact]
    public async Task ADeviceSignsInForOneAppAndTheSameComputerReplacesItsEntry()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        Assert.False((await DeviceService.LoginAsync(test.Db, new DeviceLoginRequest("anna", "nope", "PC-1", "windows", "1.0", CloudApps.Note), null, "test")).Ok);
        Assert.Equal("app", (await DeviceService.LoginAsync(test.Db, new DeviceLoginRequest("anna", "anna-password", "PC-1", "windows", "1.0", "unknown"), null, "test")).Field);

        var note = await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("Anna", "anna-password", "PC-1", "windows", "1.0", CloudApps.Note), null, "test");
        Assert.True(note.Ok);
        Assert.Equal(CloudApps.Note, note.Value!.Account.App);
        var device = (await DeviceService.AuthenticateAsync(test.NewContext(), note.Value.Token))!;
        Assert.Equal(CloudApps.Note, device.App);
        Assert.Equal(CloudApps.Note, device.Actor.App);

        // the same computer with another app is another device; with the same app again it replaces the old one
        await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("anna", "anna-password", "PC-1", "windows", "1.0", CloudApps.Clip), null, "test");
        var again = await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("anna", "anna-password", "PC-1", "windows", "1.1", CloudApps.Note), null, "test");
        Assert.Null(await DeviceService.AuthenticateAsync(test.NewContext(), note.Value.Token));
        Assert.NotNull(await DeviceService.AuthenticateAsync(test.NewContext(), again.Value!.Token));
        var (devices, total) = await DeviceService.ListAsync(test.NewContext(), new Actor(anna.Id, false), null, null, "app", false);
        Assert.Equal(2, total);
        Assert.Equal([CloudApps.Clip, CloudApps.Note], devices.Select(d => d.App));
        Assert.Single((await DeviceService.ListAsync(test.NewContext(), new Actor(anna.Id, false), null, CloudApps.Note, "seen", true)).Items);
    }

    [DbFact]
    public async Task TheSignInInTheBrowserGivesTheTokenOnceAfterTheConfirmation()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var start = await DeviceLinkService.StartAsync(test.Db, Link(), "10.0.0.5", "https://cloud.example.com/");
        Assert.True(start.Ok);
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", start.Value!.Code);
        Assert.Equal("https://cloud.example.com/Connect?code=" + start.Value.Code, start.Value.VerificationUrl);

        Assert.Equal(DeviceLinkState.Pending, (await DeviceLinkService.PollAsync(test.NewContext(), start.Value.PollToken, "test")).State);

        // the person types the code in small letters without the dash
        var found = await DeviceLinkService.FindAsync(test.NewContext(), start.Value.Code.Replace("-", string.Empty).ToLowerInvariant());
        Assert.True(found.Ok);
        Assert.Equal("ThixNote", found.Value!.AppName);
        Assert.Equal("ANNA-PC", found.Value.DeviceName);

        var approved = await DeviceLinkService.ApproveAsync(test.NewContext(), new Actor(anna.Id, false), start.Value.Code);
        Assert.True(approved.Ok);
        Assert.Equal(CloudApps.Note, approved.Value!.App);

        var poll = await DeviceLinkService.PollAsync(test.NewContext(), start.Value.PollToken, "test");
        Assert.Equal(DeviceLinkState.Approved, poll.State);
        Assert.Equal("anna", poll.Login!.Account.UserName);
        Assert.NotNull(await DeviceService.AuthenticateAsync(test.NewContext(), poll.Login.Token));

        // once: the next poll and the code are used up
        Assert.Equal(DeviceLinkState.Expired, (await DeviceLinkService.PollAsync(test.NewContext(), start.Value.PollToken, "test")).State);
        Assert.False((await DeviceLinkService.ApproveAsync(test.NewContext(), new Actor(anna.Id, false), start.Value.Code)).Ok);
    }

    [DbFact]
    public async Task ARefusedOrExpiredSignInGivesNoToken()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var refused = (await DeviceLinkService.StartAsync(test.Db, Link(), null, "http://localhost:4950")).Value!;
        Assert.True((await DeviceLinkService.DenyAsync(test.NewContext(), new Actor(anna.Id, false), refused.Code)).Ok);
        Assert.Equal(DeviceLinkState.Denied, (await DeviceLinkService.PollAsync(test.NewContext(), refused.PollToken, "test")).State);

        var late = (await DeviceLinkService.StartAsync(test.NewContext(), Link(), null, "http://localhost:4950")).Value!;
        await using (var db = test.NewContext())
        {
            await db.DeviceLinks.ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(DeviceLinkState.Expired, (await DeviceLinkService.PollAsync(test.NewContext(), late.PollToken, "test")).State);
        Assert.False((await DeviceLinkService.ApproveAsync(test.NewContext(), new Actor(anna.Id, false), late.Code)).Ok);
        Assert.Equal(DeviceLinkState.Expired, (await DeviceLinkService.PollAsync(test.NewContext(), "no-token", "test")).State);

        // a device cannot confirm a code
        var other = (await DeviceLinkService.StartAsync(test.NewContext(), Link(), null, "http://localhost:4950")).Value!;
        Assert.False((await DeviceLinkService.ApproveAsync(test.NewContext(), new Actor(anna.Id, false, 9, "PC", CloudApps.Note), other.Code)).Ok);
    }

    [DbFact]
    public async Task ASwitchedOffAppSignsNoDeviceIn()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true, password: "admin-password");
        Assert.True((await AppService.SetEnabledAsync(test.Db, new Actor(admin.Id, true), CloudApps.Note, false)).Ok);

        var login = await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("admin", "admin-password", "PC", "windows", "1", CloudApps.Note), null, "test");
        Assert.Equal("app", login.Field);
        Assert.Equal("app", (await DeviceLinkService.StartAsync(test.NewContext(), Link(), null, "http://x")).Field);
        Assert.DoesNotContain((await AppService.EnabledAppsAsync(test.NewContext())), a => a.Id == CloudApps.Note);
        Assert.False((await AppService.ListAsync(test.NewContext(), new Actor(admin.Id, true))).Single(a => a.App.Id == CloudApps.Note).App.IsEnabled);

        // only an administrator switches
        await Assert.ThrowsAsync<NotAllowedException>(() => AppService.SetEnabledAsync(test.NewContext(), new Actor(admin.Id, false), CloudApps.Note, true));
        Assert.True((await AppService.SetEnabledAsync(test.NewContext(), new Actor(admin.Id, true), CloudApps.Note, true)).Ok);
        Assert.True((await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("admin", "admin-password", "PC", "windows", "1", CloudApps.Note), null, "test")).Ok);
    }

    [DbFact]
    public async Task AnAccountOfMatIdentityIsMadeOnceAndALocalOneIsLinkedOnlyAfterItsPassword()
    {
        await using var test = await TestDatabase.CreateAsync();

        // the very first person through MatIdentity becomes the administrator; the same subject finds the same account
        var first = await ExternalSignInService.SignInAsync(test.Db, new ExternalIdentity("sub-1", "eva@example.com", "Eva", "eva", null));
        Assert.True(first.User!.IsAdmin);
        Assert.Equal("eva", first.User.UserName);
        Assert.Equal(first.User.Id, (await ExternalSignInService.SignInAsync(test.NewContext(), new ExternalIdentity("sub-1", null, "Eva", "eva", null))).User!.Id);

        // a local account with the same name is not entered by the name
        var anna = await test.AddUserAsync("anna");
        var match = await ExternalSignInService.SignInAsync(test.NewContext(), new ExternalIdentity("sub-2", null, "Anna", "anna", "user"));
        Assert.Equal(anna.Id, match.LinkUserId);
        Assert.Null(match.User);
        var linked = await ExternalSignInService.LinkAsync(test.NewContext(), anna.Id, new ExternalIdentity("sub-2", null, "Anna", "anna", "user"));
        Assert.Equal("sub-2", linked.User!.ExternalSubject);
        Assert.False((await ExternalSignInService.LinkAsync(test.NewContext(), anna.Id, new ExternalIdentity("sub-3", null, null, null, null))).User is not null);

        // the role of MatIdentity is followed, but the last administrator stays one
        Assert.True((await ExternalSignInService.SignInAsync(test.NewContext(), new ExternalIdentity("sub-1", null, "Eva", "eva", "user"))).User!.IsAdmin);
        Assert.True((await ExternalSignInService.SignInAsync(test.NewContext(), new ExternalIdentity("sub-2", null, "Anna", "anna", "admin"))).User!.IsAdmin);
        Assert.False((await ExternalSignInService.SignInAsync(test.NewContext(), new ExternalIdentity("sub-1", null, "Eva", "eva", "user"))).User!.IsAdmin);

        // an account without a password never signs in with one
        Assert.False((await UserService.SignInAsync(test.NewContext(), "eva", "")).Ok);
        Assert.False((await UserService.SignInAsync(test.NewContext(), "eva", "anything-at-all")).Ok);
    }

    [DbFact]
    public async Task TheBackChannelLogoutEndsTheSessionsOfThePerson()
    {
        await using var test = await TestDatabase.CreateAsync();
        var eva = (await ExternalSignInService.SignInAsync(test.Db, new ExternalIdentity("sub-1", null, "Eva", "eva", null))).User!;
        var one = await SessionService.CreateAsync(test.NewContext(), eva.Id, true, null, null, "id-token", "sid-a");
        var two = await SessionService.CreateAsync(test.NewContext(), eva.Id, true, null, null, "id-token", "sid-b");

        Assert.Equal(1, await SessionService.EndIdentitySessionsAsync(test.NewContext(), null, "sid-a"));
        Assert.Null(await SessionService.LoadAsync(test.NewContext(), one.Token));
        Assert.NotNull(await SessionService.LoadAsync(test.NewContext(), two.Token));
        Assert.Equal("id-token", await SessionService.IdentityTokenAsync(test.NewContext(), two.Token));

        Assert.Equal(1, await SessionService.EndIdentitySessionsAsync(test.NewContext(), "sub-1", null));
        Assert.Null(await SessionService.LoadAsync(test.NewContext(), two.Token));
        Assert.Equal(0, await SessionService.EndIdentitySessionsAsync(test.NewContext(), "unknown", null));
    }

    [Fact]
    public void CodesAreReadLikePeopleTypeThem()
    {
        Assert.Equal("ACDE-FGHJ", DeviceLinkService.Format("ACDEFGHJ"));
        Assert.Equal("ACDEFGHJ", DeviceLinkService.Normalize(" acde-fghj "));
        Assert.Null(DeviceLinkService.Normalize("ACDE-FGH"));
        Assert.Null(DeviceLinkService.Normalize("ACDE-FGH0"));
        Assert.Null(DeviceLinkService.Normalize(null));
    }
}
