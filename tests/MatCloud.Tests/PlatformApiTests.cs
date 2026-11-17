using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Contract.Clip;
using MatCloud.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MatCloud.Tests;

/// <summary>The whole app over HTTP: the general sync and the sign-in in the browser for devices, the platform contract (/api), against a real PostgreSQL.</summary>
public sealed class PlatformApiTests
{
    [DbFact]
    public async Task TwoDevicesOfAnAppSyncThroughTheGeneralSyncAndTheClipRoutesAreClosedToThem()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        var (pc, _) = await TestApp.DeviceAsync(app, CloudApps.Note, "PC");
        var (mac, _) = await TestApp.DeviceAsync(app, CloudApps.Note, "Mac");

        var waiting = mac.GetFromJsonAsync<WaitResult>($"{SyncContract.Wait}?since=0&timeout=20", CloudContract.Json);
        var item = new SyncItemDto { Collection = "notes", Key = Guid.NewGuid().ToString(), Data = "{\"path\":\"Einkauf.md\"}", UpdatedAt = DateTimeOffset.UtcNow };
        var push = await pc.PostAsJsonAsync(SyncContract.Push, new SyncPushRequest([item]), CloudContract.Json);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        Assert.Equal("1", push.Headers.GetValues(CloudContract.ProtocolHeader).Single());
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10)))!.Latest > 0);

        var changes = (await mac.GetFromJsonAsync<SyncChangeSet>($"{SyncContract.Changes}?since=0", CloudContract.Json))!;
        Assert.Equal(item.Key, Assert.Single(changes.Items).Key);
        Assert.Equal("PC", changes.Items[0].Source);

        var bad = await pc.PostAsJsonAsync(SyncContract.Push, new SyncPushRequest([item with { Collection = "NOTES" }]), CloudContract.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("items[0].collection", (await bad.Content.ReadFromJsonAsync<ApiError>(CloudContract.Json))!.Field);

        // a ThixNote device is no ThixClip device, and ThixClip has no general sync
        Assert.Equal(HttpStatusCode.Forbidden, (await pc.GetAsync(ClipContract.Changes)).StatusCode);
        var (clip, _) = await TestApp.DeviceAsync(app, CloudApps.Clip, "PC");
        Assert.Equal(HttpStatusCode.Forbidden, (await clip.GetAsync(SyncContract.Changes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clip.GetAsync(ClipContract.Changes)).StatusCode);

        // a newer protocol is told to wait for an update of the cloud
        using var newer = new HttpRequestMessage(HttpMethod.Get, CloudContract.Account);
        newer.Headers.Add(CloudContract.ProtocolHeader, "99");
        newer.Headers.Authorization = pc.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.SendAsync(newer)).StatusCode);
    }

    [DbFact]
    public async Task ADeviceSignsInInTheBrowserWithTheMatCloudClient()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        await using var app = TestApp.Create(test);
        using var client = new MatCloudClient("http://localhost", null, "ThixNote/1.0 (windows)", app.Server.CreateHandler());

        var start = await client.StartLinkAsync(new DeviceLinkRequest("ANNA-PC", "windows", "1.0", CloudApps.Note), CancellationToken.None);
        Assert.StartsWith("http://localhost/Connect?code=", start.VerificationUrl);
        var waiting = client.WaitForLinkAsync(start with { IntervalSeconds = 1 }, CancellationToken.None);

        // the person confirms it in the browser (the business layer of the page)
        await using (var db = test.NewContext())
        {
            Assert.True((await MatCloud.Core.Business.DeviceLinkService.ApproveAsync(db, new MatCloud.Core.Business.Actor(anna.Id, false), start.Code)).Ok);
        }

        var login = await waiting.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("ANNA-PC", login.Account.DeviceName);
        using var device = new MatCloudClient("http://localhost", login.Token, "ThixNote/1.0 (windows)", app.Server.CreateHandler());
        Assert.Equal(CloudApps.Note, (await device.GetAccountAsync(CancellationToken.None)).App);
        await device.LogoutAsync(CancellationToken.None);
        var gone = await Assert.ThrowsAsync<CloudException>(() => device.GetAccountAsync(CancellationToken.None));
        Assert.True(gone.IsUnauthorized);

        // a switched-off app: 403
        var unknown = await Assert.ThrowsAsync<CloudException>(() => client.StartLinkAsync(new DeviceLinkRequest("PC", "windows", "1", "nope"), CancellationToken.None));
        Assert.Equal("app", unknown.Field);
    }

    [DbFact]
    public async Task ThePlatformContractDescribesMatCloudAndAnswersItsHealth()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("admin", admin: true);
        await using var app = TestApp.Create(test, settings: new Dictionary<string, string?> { ["MAT_API_KEY"] = "test-key-of-the-broker" });
        var client = app.CreateClient();

        var definition = await client.GetFromJsonAsync<JsonElement>("/api");
        Assert.Equal(2, definition.GetProperty("contract").GetInt32());
        Assert.Equal("matcloud", definition.GetProperty("app").GetProperty("id").GetString());

        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await health.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key-of-the-broker");
        var state = await client.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal(1, state.GetProperty("values").GetProperty("users").GetProperty("value").GetInt32());

        var configure = await client.PutAsJsonAsync("/api/configure", new { values = new { publicUrl = "https://cloud.example.com" } });
        Assert.Equal(HttpStatusCode.OK, configure.StatusCode);
        var values = await client.GetFromJsonAsync<JsonElement>("/api/configure");
        Assert.Equal("https://cloud.example.com", values.GetProperty("values").GetProperty("publicUrl").GetString());

        // the public address goes into the link of a sign-in in the browser
        var start = await app.CreateClient().PostAsJsonAsync(CloudContract.DeviceLink, new DeviceLinkRequest("PC", "windows", "1", CloudApps.Note), CloudContract.Json);
        Assert.StartsWith("https://cloud.example.com/Connect?code=", (await start.Content.ReadFromJsonAsync<DeviceLinkStart>(CloudContract.Json))!.VerificationUrl);

        var cleanUp = await client.PostAsync("/api/actions/cleanUp", null);
        Assert.Equal(HttpStatusCode.OK, cleanUp.StatusCode);
    }

    [DbFact]
    public async Task PagesNeedASignInAndShowTheAppsOfThePerson()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var app = TestApp.Create(test);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // a fresh installation leads to the setup assistant
        Assert.Equal("/Account/Setup", (await client.GetAsync("/")).Headers.Location?.OriginalString);
        var setupPage = await client.GetAsync("/Account/Setup");
        Assert.Equal(HttpStatusCode.OK, setupPage.StatusCode);
        Assert.Contains("MatCloud einrichten", await setupPage.Content.ReadAsStringAsync());

        foreach (var path in new[] { "/manifest.webmanifest", "/sw.js", "/robots.txt", "/offline.html", "/health", "/_content/MatCloud.Apps.Clip/js/clip-live.js" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }

        // with an account: the live list of the clips answers 401 without a session (never the sign-in page), the pages lead to the sign-in
        await test.AddUserAsync("anna");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/live/clip/revision?since=0&timeout=1")).StatusCode);
        Assert.Contains("/Account/Login", (await client.GetAsync("/Apps")).Headers.Location?.OriginalString);
        var login = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("script-src 'self'", login.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public void TheDefinitionIsValidAgainstTheSpriteOfMatUi()
    {
        var definition = MatPlatformBuilder.Build(MatCloudPlatform.Describe);
        var sprite = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MatCloud", "wwwroot", "matui", "matui-icons.svg");
        var problems = MatPlatformValidator.Validate(definition, MatPlatformValidator.ReadSprite(sprite));
        Assert.Empty(problems);
    }
}

/// <summary>An installation by Mattiverse: the administrator comes from MATCLOUD_ADMIN_PASSWORD, no setup assistant.</summary>
public sealed class SeedTests
{
    [DbFact]
    public async Task TheAdministratorOfAFreshInstallationComesFromTheVariable()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var app = TestApp.Create(test, settings: new Dictionary<string, string?> { ["MATCLOUD_ADMIN_PASSWORD"] = "seeded-password-1" });
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.NotEqual("/Account/Setup", (await client.GetAsync("/")).Headers.Location?.OriginalString);
        var login = await client.PostAsJsonAsync(CloudContract.DeviceLogin, new DeviceLoginRequest("admin", "seeded-password-1", "PC", "windows", "1", CloudApps.Note), CloudContract.Json);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
