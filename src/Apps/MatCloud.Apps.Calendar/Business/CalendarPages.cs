using System.Text.Json;
using System.Text.Json.Nodes;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Models;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>A calendar as the pages show it. <see cref="HasEvents"/>: its events are in MatCloud (an own calendar, or an account MatCloud keeps in step).</summary>
public sealed record CalendarEntry(
    string Key, string Account, string AccountName, CalendarAccountKind Kind, string Name, string Color, bool IsVisible, bool IsReadOnly, bool HasEvents)
{
    public bool IsOwn => Kind == CalendarAccountKind.Local;

    /// <summary>Events can be created and changed here.</summary>
    public bool IsWritable => HasEvents && !IsReadOnly && Kind != CalendarAccountKind.Ics;
}

/// <summary>An event (one occurrence) as the pages show it, with its calendar. <see cref="IsCloud"/>: of an account MatCloud keeps (cloud-events).</summary>
public sealed record EventEntry(string Key, bool IsCloud, CalendarEntry Calendar, CalendarCloudEvent Data)
{
    public DateTime StartLocal => Data.IsAllDay ? Data.Start.UtcDateTime.Date : TimeZoneInfo.ConvertTime(Data.Start, Dates.Zone).DateTime;

    public DateTime EndLocal => Data.IsAllDay ? Data.End.UtcDateTime.Date : TimeZoneInfo.ConvertTime(Data.End, Dates.Zone).DateTime;

    /// <summary>The days it touches (an all-day event: up to the day before its end).</summary>
    public bool Touches(DateTime day) => StartLocal < day.Date.AddDays(1) && (EndLocal > day.Date || (EndLocal == StartLocal && StartLocal.Date == day.Date));

    public string TimeText => Data.IsAllDay ? "Ganztägig" : $"{StartLocal:HH:mm} – {EndLocal:HH:mm}";

    public bool IsCancelled => Data.Status == CalendarEventStatus.Cancelled;

    public bool IsDeclined => Data.Response == CalendarResponse.Declined;

    public string EditUrl => $"/Calendar/Event?key={Uri.EscapeDataString(Key)}&src={(IsCloud ? "cloud" : "own")}";
}

/// <summary>The settings of ThixCalendar the pages change (the item keeps every other field as ThixCalendar wrote it).</summary>
public sealed record CalendarPageSettings(CalendarSyncMode DefaultSyncMode, int SyncIntervalMinutes);

/// <summary>What the pages read: the calendars, the events of a time span, one event, the settings – from the items of ThixCalendar in the general sync.</summary>
public static class CalendarPages
{
    public static readonly IReadOnlyList<int> SyncIntervals = [5, 10, 15, 30, 60];

    public static async Task<IReadOnlyList<CalendarEntry>> CalendarsAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        RequireBrowser(actor);
        var accounts = (await CalendarItems.ReadAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, ct)).ToDictionary(a => a.Key, a => a.Data);
        var choices = (await CalendarItems.ReadAsync<CalendarCalendarItem>(db, actor.UserId, CalendarContract.Calendars, ct)).ToDictionary(c => c.Key, c => c.Data);
        var cloud = (await CalendarItems.ReadAsync<CalendarCloudCalendar>(db, actor.UserId, CalendarContract.CloudCalendars, ct)).ToDictionary(c => c.Key, c => c.Data);
        var result = new List<CalendarEntry>();
        foreach (var key in choices.Keys.Union(cloud.Keys))
        {
            var accountKey = key.Split('/')[0];
            if (!accounts.TryGetValue(accountKey, out var account))
            {
                continue;
            }

            choices.TryGetValue(key, out var choice);
            cloud.TryGetValue(key, out var provider);
            var own = account.Kind == CalendarAccountKind.Local;
            var name = provider?.Name ?? choice?.Name ?? "Kalender";
            var color = choice?.Color is { Length: > 0 } chosen ? chosen : provider?.Color ?? CalendarColors.Default;
            result.Add(new CalendarEntry(key, accountKey, account.Name, account.Kind, name, color, account.IsVisible && (choice?.IsVisible ?? true),
                provider?.IsReadOnly ?? false, own || provider is not null));
        }

        return result.OrderBy(c => c.IsOwn ? 0 : 1).ThenBy(c => c.AccountName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The events that touch the local days [<paramref name="from"/>, <paramref name="to"/>), of the shown calendars (or of all).</summary>
    public static async Task<IReadOnlyList<EventEntry>> EventsAsync(AppDbContext db, Actor actor, IReadOnlyList<CalendarEntry> calendars, DateTime from, DateTime to,
        bool shownOnly, string? search, CancellationToken ct)
    {
        RequireBrowser(actor);
        var byKey = calendars.Where(c => !shownOnly || c.IsVisible).ToDictionary(c => c.Key);
        var result = new List<EventEntry>();
        foreach (var row in await CalendarItems.ReadAsync<CalendarEventItem>(db, actor.UserId, CalendarContract.Events, ct))
        {
            if (byKey.TryGetValue(row.Data.Calendar, out var calendar))
            {
                result.Add(new EventEntry(row.Key, false, calendar, FromOwn(row.Data)));
            }
        }

        foreach (var row in await CalendarItems.ReadAsync<CalendarCloudEvent>(db, actor.UserId, CalendarContract.CloudEvents, ct))
        {
            if (byKey.TryGetValue(row.Data.Calendar, out var calendar))
            {
                result.Add(new EventEntry(row.Key, true, calendar, row.Data));
            }
        }

        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return result
            .Where(e => e.StartLocal < to && (e.EndLocal > from || (e.EndLocal == e.StartLocal && e.StartLocal >= from)))
            .Where(e => words.All(w => e.Data.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                       e.Data.Location.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                       e.Data.Description.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(e => e.Data.IsAllDay ? 0 : 1).ThenBy(e => e.StartLocal).ThenBy(e => e.Data.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static async Task<EventEntry?> EventAsync(AppDbContext db, Actor actor, string key, bool cloud, CancellationToken ct)
    {
        var calendars = await CalendarsAsync(db, actor, ct);
        if (cloud)
        {
            var row = await CalendarItems.FindAsync<CalendarCloudEvent>(db, actor.UserId, CalendarContract.CloudEvents, key, ct);
            var calendar = row is null ? null : calendars.FirstOrDefault(c => c.Key == row.Data.Calendar);
            return row is null || calendar is null ? null : new EventEntry(row.Key, true, calendar, row.Data);
        }

        var own = await CalendarItems.FindAsync<CalendarEventItem>(db, actor.UserId, CalendarContract.Events, key, ct);
        var ownCalendar = own is null ? null : calendars.FirstOrDefault(c => c.Key == own.Data.Calendar);
        return own is null || ownCalendar is null ? null : new EventEntry(own.Key, false, ownCalendar, FromOwn(own.Data));
    }

    public static async Task<long> LatestAsync(AppDbContext db, Actor actor, CancellationToken ct) => await SyncService.LatestAsync(db, actor.UserId, CalendarItems.App, ct);

    // ===== settings =====

    public static async Task<CalendarPageSettings> SettingsAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        RequireBrowser(actor);
        var settings = await CalendarItems.FindAsync<CalendarSettingsItem>(db, actor.UserId, CalendarContract.Settings, CalendarContract.SettingsKey, ct);
        return new CalendarPageSettings(settings?.Data.DefaultSyncMode ?? CalendarSyncMode.Cloud,
            settings?.Data.SyncIntervalMinutes is > 0 and var minutes ? minutes : 15);
    }

    /// <summary>Saves the default sync mode and the interval into ThixCalendar's settings item (every other field stays as it is).</summary>
    public static async Task<Outcome> SaveSettingsAsync(AppDbContext db, Actor actor, CalendarSyncMode mode, int interval, CancellationToken ct)
    {
        RequireBrowser(actor);
        if (!SyncIntervals.Contains(interval))
        {
            return Outcome.Fail("Bitte einen der angebotenen Abstände wählen.", "SyncIntervalMinutes");
        }

        var row = await CalendarItems.Live(db, actor.UserId, CalendarContract.Settings).FirstOrDefaultAsync(i => i.Key == CalendarContract.SettingsKey, ct);
        var json = (row?.Data is { } data ? JsonNode.Parse(data) as JsonObject : null) ?? new JsonObject();
        json["defaultSyncMode"] = JsonSerializer.SerializeToNode(mode, CloudContract.Json);
        json["syncIntervalMinutes"] = interval;
        var item = CalendarItems.Item(CalendarContract.Settings, CalendarContract.SettingsKey, json);
        await CalendarItems.PushAsync(db, actor, [item], ct);
        return Outcome.Success();
    }

    // ===== the own calendars =====

    /// <summary>Shows or hides a calendar (the choice travels to ThixCalendar on every computer).</summary>
    public static async Task<Outcome> SetVisibleAsync(AppDbContext db, Actor actor, string key, bool visible, CancellationToken ct)
    {
        RequireBrowser(actor);
        var calendars = await CalendarsAsync(db, actor, ct);
        var calendar = calendars.FirstOrDefault(c => c.Key == key);
        if (calendar is null)
        {
            return Outcome.Missing();
        }

        var choice = await CalendarItems.FindAsync<CalendarCalendarItem>(db, actor.UserId, CalendarContract.Calendars, key, ct);
        var remote = await CalendarItems.FindAsync<CalendarCloudCalendar>(db, actor.UserId, CalendarContract.CloudCalendars, key, ct);
        var data = choice?.Data ?? new CalendarCalendarItem(remote?.Data.RemoteId ?? string.Empty, calendar.Name, string.Empty, true, true, null, 0);
        await CalendarItems.PushAsync(db, actor, [CalendarItems.Item(CalendarContract.Calendars, key, data with { IsVisible = visible })], ct);
        return Outcome.Success();
    }

    private static CalendarCloudEvent FromOwn(CalendarEventItem e) => new()
    {
        Calendar = e.Calendar,
        Title = e.Title,
        Start = e.Start,
        End = e.End,
        IsAllDay = e.IsAllDay,
        Location = e.Location,
        Description = e.Description,
        MeetingUrl = e.MeetingUrl,
        ReminderMinutes = e.ReminderMinutes,
        Status = e.Status,
    };

    private static void RequireBrowser(Actor actor)
    {
        if (actor.DeviceId is not null)
        {
            throw new NotAllowedException("Nur im Browser.");
        }
    }
}
