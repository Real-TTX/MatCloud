using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCloud.Apps.Calendar.Business;
using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Providers;

namespace MatCloud.Tests;

/// <summary>
/// ThixCalendar in MatCloud (the module Calendar): a device hands an account over, MatCloud syncs it on the server (Microsoft Graph played by
/// <see cref="FakeMicrosoft"/>), the devices read what only MatCloud writes, events are changed at the provider first, an account goes back to the computers.
/// Against a real PostgreSQL (MATCLOUD_TEST_DB); the network never.
/// </summary>
[Collection(nameof(CalendarTests))]
public sealed class CalendarTests : IDisposable
{
    private readonly FakeMicrosoft _microsoft = new();

    public CalendarTests()
    {
        CalendarWorker.Enabled = false;
        ProviderHttp.HandlerForTests = _microsoft;
    }

    public void Dispose() => ProviderHttp.HandlerForTests = null;

    private static Actor Browser(User user) => new(user.Id, user.IsAdmin);

    private static Actor Device(User user, string name = "ANNA-PC") => new(user.Id, user.IsAdmin, 7, name, CloudApps.Calendar);

    private static CalendarKeys Keys() => new(DataProtectionProvider.Create("MatCloud.Tests"));

    /// <summary>The account as ThixCalendar pushes it (sync mode Cloud).</summary>
    private static SyncItemDto Account(string key, CalendarSyncMode? mode = CalendarSyncMode.Cloud) => new()
    {
        Collection = CalendarContract.Accounts,
        Key = key,
        Data = JsonSerializer.Serialize(new CalendarAccountItem(CalendarAccountKind.Microsoft, "Arbeit", "anna@firma.de", string.Empty, true, mode), CloudContract.Json),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private static async Task<CalendarConnection> HandOverAsync(TestDatabase test, User anna, string account = "acc1")
    {
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Calendar, new SyncPushRequest([Account(account)]));
        var handed = await CalendarConnections.HandOverAsync(test.Db, Keys(), Device(anna),
            new CalendarHandover(account, CalendarAccountKind.Microsoft, "anna@firma.de", "refresh-1"), CancellationToken.None);
        Assert.True(handed.Ok, handed.Error);
        return await test.NewContext().CalendarConnections.SingleAsync(c => c.Account == account);
    }

    private static async Task SyncAsync(TestDatabase test, string account = "acc1")
    {
        await using var db = test.NewContext();
        var connection = await db.CalendarConnections.SingleAsync(c => c.Account == account);
        await CalendarCloudSync.SyncAsync(db, Keys(), connection, CancellationToken.None);
    }

    private static async Task<List<SyncItemDto>> ItemsAsync(TestDatabase test, User anna, string collection)
        => (await SyncService.ChangesAsync(test.NewContext(), Device(anna), CloudApps.Calendar, 0, 1000)).Items.Where(i => i.Collection == collection).ToList();

    [DbFact]
    public async Task A_handed_over_account_is_synced_by_MatCloud_and_reaches_every_device()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var connection = await HandOverAsync(test, anna);
        Assert.Equal(CalendarCloudState.Connecting, connection.State);
        Assert.NotEqual("refresh-1", connection.ProtectedToken); // protected, never stored as it is

        await SyncAsync(test);

        var calendars = await ItemsAsync(test, anna, CalendarContract.CloudCalendars);
        var calendar = Assert.Single(calendars);
        Assert.Equal(CalendarContract.CalendarKey("acc1", "CAL1"), calendar.Key);
        Assert.Equal("MatCloud", calendar.Source);
        var events = await ItemsAsync(test, anna, CalendarContract.CloudEvents);
        var meeting = Assert.Single(events);
        Assert.Equal(CalendarContract.EventKey(calendar.Key, "E1"), meeting.Key);
        var data = JsonSerializer.Deserialize<CalendarCloudEvent>(meeting.Data!, CloudContract.Json)!;
        Assert.Equal("Team-Meeting", data.Title);
        Assert.Equal(calendar.Key, data.Calendar);
        var state = JsonSerializer.Deserialize<CalendarCloudAccount>(Assert.Single(await ItemsAsync(test, anna, CalendarContract.CloudAccounts)).Data!, CloudContract.Json)!;
        Assert.Equal(CalendarCloudState.Ready, state.State);

        // Microsoft rotated the refresh token: the new one is kept (and used next time)
        var stored = await test.NewContext().CalendarConnections.SingleAsync();
        Assert.Equal("refresh-2", Keys().Unprotect(stored.ProtectedToken));
        Assert.True(stored.NextSync > DateTime.UtcNow.AddMinutes(4));
    }

    [DbFact]
    public async Task A_second_sync_writes_only_what_changed_and_removes_what_is_gone()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await HandOverAsync(test, anna);
        await SyncAsync(test);
        var latest = await SyncService.LatestAsync(test.NewContext(), anna.Id, CloudApps.Calendar);

        await SyncAsync(test);
        Assert.Equal(latest, await SyncService.LatestAsync(test.NewContext(), anna.Id, CloudApps.Calendar)); // nothing changed: no new revision

        _microsoft.Events.Clear();
        await SyncAsync(test);
        var gone = Assert.Single(await ItemsAsync(test, anna, CalendarContract.CloudEvents));
        Assert.True(gone.IsDeleted);
    }

    [DbFact]
    public async Task A_device_cannot_write_what_only_MatCloud_writes()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var forged = new SyncItemDto { Collection = CalendarContract.CloudEvents, Key = "acc1/x/y", Data = "{}", UpdatedAt = DateTimeOffset.UtcNow };

        var answer = await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Calendar, new SyncPushRequest([forged]));

        Assert.False(Assert.Single(answer.Results).Applied);
        Assert.Empty(await ItemsAsync(test, anna, CalendarContract.CloudEvents));
    }

    [DbFact]
    public async Task A_refused_sign_in_asks_for_a_new_one_and_stops_syncing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await HandOverAsync(test, anna);
        _microsoft.RefuseTokens = true;

        await SyncAsync(test);

        var stored = await test.NewContext().CalendarConnections.SingleAsync();
        Assert.Equal(CalendarCloudState.SignInRequired, stored.State);
        Assert.Equal(string.Empty, stored.ProtectedToken);
        Assert.True(stored.NextSync > DateTime.UtcNow.AddYears(100));
        var state = JsonSerializer.Deserialize<CalendarCloudAccount>(Assert.Single(await ItemsAsync(test, anna, CalendarContract.CloudAccounts)).Data!, CloudContract.Json)!;
        Assert.Equal(CalendarCloudState.SignInRequired, state.State);
    }

    [DbFact]
    public async Task Back_to_the_computers_MatCloud_forgets_the_sign_in_and_its_items()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await HandOverAsync(test, anna);
        await SyncAsync(test);

        // ThixCalendar switched the account back to the computers
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Calendar, new SyncPushRequest([Account("acc1", CalendarSyncMode.Computers) with { UpdatedAt = DateTimeOffset.UtcNow }]));
        await SyncAsync(test);

        Assert.Empty(await test.NewContext().CalendarConnections.ToListAsync());
        Assert.All(await ItemsAsync(test, anna, CalendarContract.CloudEvents), i => Assert.True(i.IsDeleted));
        Assert.All(await ItemsAsync(test, anna, CalendarContract.CloudCalendars), i => Assert.True(i.IsDeleted));
        Assert.All(await ItemsAsync(test, anna, CalendarContract.CloudAccounts), i => Assert.True(i.IsDeleted));
    }

    [DbFact]
    public async Task An_event_is_changed_at_Microsoft_first_then_in_the_items()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await HandOverAsync(test, anna);
        await SyncAsync(test);
        var calendar = CalendarContract.CalendarKey("acc1", "CAL1");
        var start = DateTimeOffset.UtcNow.Date.AddDays(3).AddHours(14);

        var created = await CalendarEvents.ChangeCloudAsync(test.NewContext(), Keys(), Device(anna),
            new CalendarEventChange(null, new CalendarCloudEvent { Calendar = calendar, Title = "Review", Start = start, End = start.AddHours(1) }), CancellationToken.None);

        Assert.True(created.Ok, created.Error);
        Assert.Equal(1, _microsoft.Created);
        Assert.Equal(CalendarContract.EventKey(calendar, "NEW1"), created.Value!.Event);
        Assert.Contains(await ItemsAsync(test, anna, CalendarContract.CloudEvents), i => i.Key == created.Value.Event && !i.IsDeleted);

        var deleted = await CalendarEvents.ChangeCloudAsync(test.NewContext(), Keys(), Device(anna), new CalendarEventChange(created.Value.Event, null, Delete: true), CancellationToken.None);
        Assert.True(deleted.Ok, deleted.Error);
        Assert.Equal(1, _microsoft.Deleted);
        Assert.True((await ItemsAsync(test, anna, CalendarContract.CloudEvents)).Single(i => i.Key == created.Value.Event).IsDeleted);
    }

    [DbFact]
    public async Task An_event_without_a_title_is_refused_before_Microsoft_is_asked()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await HandOverAsync(test, anna);
        await SyncAsync(test);

        var outcome = await CalendarEvents.ChangeCloudAsync(test.NewContext(), Keys(), Device(anna),
            new CalendarEventChange(null, new CalendarCloudEvent { Calendar = CalendarContract.CalendarKey("acc1", "CAL1"), Title = " " }), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("Title", outcome.Field);
        Assert.Equal(0, _microsoft.Created);
    }

    [DbFact]
    public async Task The_settings_of_the_browser_keep_what_ThixCalendar_wrote()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        await SyncService.PushAsync(test.Db, Device(anna), CloudApps.Calendar, new SyncPushRequest([new SyncItemDto
        {
            Collection = CalendarContract.Settings, Key = CalendarContract.SettingsKey,
            Data = """{"dayStartHour":7,"defaultReminderMinutes":10,"syncIntervalMinutes":15}""", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }]));

        var saved = await CalendarPages.SaveSettingsAsync(test.NewContext(), Browser(anna), CalendarSyncMode.Computers, 30, CancellationToken.None);

        Assert.True(saved.Ok, saved.Error);
        var item = Assert.Single(await ItemsAsync(test, anna, CalendarContract.Settings));
        var json = JsonNode.Parse(item.Data!)!;
        Assert.Equal(7, json["dayStartHour"]!.GetValue<int>());
        Assert.Equal(30, json["syncIntervalMinutes"]!.GetValue<int>());
        Assert.Equal("Computers", json["defaultSyncMode"]!.GetValue<string>());
        Assert.Equal(new CalendarPageSettings(CalendarSyncMode.Computers, 30), await CalendarPages.SettingsAsync(test.NewContext(), Browser(anna), CancellationToken.None));
    }

    [Fact]
    public void The_providers_are_the_copy_of_ThixCalendar_that_thixcalendar_version_names()
    {
        var folder = Path.Combine(Root(), "src", "Apps", "MatCloud.Apps.Calendar", "ThixCalendar");
        var lines = File.ReadAllLines(Path.Combine(folder, "thixcalendar.version")).Skip(1).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            var (hash, file) = (line[..64], line[66..]);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(hash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), $"ThixCalendar/{file} was edited here: copy it again from ThixCalendar.");
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MatCloud.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MatCloud.slnx not found.");
    }

    /// <summary>Microsoft's token endpoint and Graph, in memory.</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public List<JsonObject> Events { get; } = [Event("E1", "Team-Meeting", DateTime.UtcNow.Date.AddDays(1).AddHours(8))];

        public bool RefuseTokens { get; set; }

        public int Created { get; private set; }

        public int Deleted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal))
            {
                return Task.FromResult(RefuseTokens
                    ? Json(new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "AADSTS70000: revoked" }, HttpStatusCode.BadRequest)
                    : Json(new JsonObject { ["access_token"] = "access", ["refresh_token"] = "refresh-2", ["expires_in"] = 3600 }));
            }

            if (url.Contains("/me/calendars?", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new JsonObject
                {
                    ["value"] = new JsonArray(new JsonObject { ["id"] = "CAL1", ["name"] = "Kalender", ["hexColor"] = "#2F6FED", ["canEdit"] = true, ["isDefaultCalendar"] = true }),
                }));
            }

            if (url.Contains("/me/calendars/CAL1/calendarView", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new JsonObject { ["value"] = new JsonArray(Events.Select(e => (JsonNode)e.DeepClone()).ToArray()) }));
            }

            if (url.Contains("/me/calendars/CAL1/events", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                Created++;
                return Task.FromResult(Json(Event("NEW1", "Review", DateTime.UtcNow.Date.AddDays(3).AddHours(14))));
            }

            if (url.Contains("/me/events/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                Deleted++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }

        private static JsonObject Event(string id, string title, DateTime utcStart) => new()
        {
            ["id"] = id,
            ["subject"] = title,
            ["isAllDay"] = false,
            ["start"] = new JsonObject { ["dateTime"] = utcStart.ToString("yyyy-MM-dd'T'HH:mm:ss.0000000"), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = utcStart.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.0000000"), ["timeZone"] = "UTC" },
            ["isReminderOn"] = true,
            ["reminderMinutesBeforeStart"] = 15,
            ["responseStatus"] = new JsonObject { ["response"] = "organizer" },
        };

        private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
