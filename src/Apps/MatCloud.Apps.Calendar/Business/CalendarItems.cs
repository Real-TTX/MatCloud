using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Models;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>A live item of the general sync of ThixCalendar, its data read.</summary>
public sealed record CalendarRow<T>(string Key, T Data, DateTime UpdatedDate, string? Source, string Hash);

/// <summary>
/// The items of ThixCalendar in the general sync (app calendar): reading them for one account of MatCloud and writing them through
/// <see cref="SyncService.PushAsync"/>, so every device of ThixCalendar sees a change at once. The JSON is ThixCalendar's (<see cref="CloudContract.Json"/>).
/// </summary>
internal static class CalendarItems
{
    public const string App = CloudApps.Calendar;

    /// <summary>The source MatCloud's own writes are marked with (the sync on the server).</summary>
    public const string ServerSource = "MatCloud";

    public static Actor Server(long userId) => new(userId, false, null, ServerSource);

    public static IQueryable<SyncItem> Live(AppDbContext db, long userId, string collection)
        => db.SyncItems.AsNoTracking().Where(i => i.UserId == userId && i.App == App && i.Collection == collection && i.UpdateState != SoftEntity.Deleted);

    public static async Task<List<CalendarRow<T>>> ReadAsync<T>(AppDbContext db, long userId, string collection, CancellationToken ct)
    {
        var rows = await Live(db, userId, collection).ToListAsync(ct);
        return rows.Select(Read<T>).OfType<CalendarRow<T>>().ToList();
    }

    public static async Task<CalendarRow<T>?> FindAsync<T>(AppDbContext db, long userId, string collection, string key, CancellationToken ct)
    {
        var row = await Live(db, userId, collection).FirstOrDefaultAsync(i => i.Key == key, ct);
        return row is null ? null : Read<T>(row);
    }

    public static CalendarRow<T>? Read<T>(SyncItem row)
    {
        try
        {
            return row.Data is null || JsonSerializer.Deserialize<T>(row.Data, CloudContract.Json) is not { } data
                ? null
                : new CalendarRow<T>(row.Key, data, row.ClientUpdateDate, row.Source, row.ContentHash ?? Hash(row.Data));
        }
        catch (JsonException)
        {
            return null; // an item of a newer ThixCalendar this server does not understand: left alone
        }
    }

    public static SyncItemDto Item<T>(string collection, string key, T data)
    {
        var json = JsonSerializer.Serialize(data, CloudContract.Json);
        return new SyncItemDto { Collection = collection, Key = key, Data = json, ContentHash = Hash(json), UpdatedAt = DateTimeOffset.UtcNow };
    }

    public static SyncItemDto Deleted(string collection, string key) =>
        new() { Collection = collection, Key = key, IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>Pushes the items in batches of the sync's size (each batch under the lock of the stream; the devices wake after each).</summary>
    public static async Task PushAsync(AppDbContext db, Actor actor, IReadOnlyList<SyncItemDto> items, CancellationToken ct)
    {
        foreach (var batch in items.Chunk(SyncContract.MaxBatchSize))
        {
            await SyncService.PushAsync(db, actor, App, new SyncPushRequest(batch), ct);
        }
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A new key as ThixCalendar makes them (accounts, own calendars and events).</summary>
    public static string NewKey() => Guid.NewGuid().ToString("N");

    // ===== events: the provider's shape and the cloud's =====

    public static CalendarCloudEvent ToCloud(string calendarKey, CalendarEvent e) => new()
    {
        Calendar = calendarKey,
        RemoteId = e.RemoteId,
        Title = e.Title,
        Start = e.Start.ToUniversalTime(),
        End = e.End.ToUniversalTime(),
        IsAllDay = e.IsAllDay,
        Location = e.Location,
        Description = e.Description,
        MeetingUrl = e.MeetingUrl,
        WebLink = e.WebLink,
        Organizer = e.Organizer,
        ReminderMinutes = e.ReminderMinutes,
        Status = (CalendarEventStatus)(int)e.Status,
        Response = (CalendarResponse)(int)e.Response,
        IsRecurring = e.IsRecurring,
    };

    public static CalendarEvent FromCloud(CalendarCloudEvent e) => new()
    {
        RemoteId = e.RemoteId,
        Title = e.Title,
        Start = e.Start,
        End = e.End,
        IsAllDay = e.IsAllDay,
        Location = e.Location,
        Description = e.Description,
        MeetingUrl = e.MeetingUrl,
        WebLink = e.WebLink,
        Organizer = e.Organizer,
        ReminderMinutes = e.ReminderMinutes,
        Status = (EventStatus)(int)e.Status,
        Response = (ResponseStatus)(int)e.Response,
        IsRecurring = e.IsRecurring,
    };
}
