using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>
/// The sync of one account MatCloud keeps in step: a fresh access token from the kept sign-in (a new refresh token is kept at once), the calendars and
/// the events of the window from the provider, and only what changed goes into the items only MatCloud writes (cloud-calendars, cloud-events; what the
/// provider no longer has becomes a tombstone), then the state into cloud-accounts. An account the user took back to the computers (or deleted) is let go:
/// its sign-in is forgotten, its items are removed.
/// </summary>
internal static class CalendarCloudSync
{
    /// <summary>The next sync of an account that needs a new sign-in: never (until it gets one).</summary>
    internal static readonly DateTime Never = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Syncs the connection now (the caller saves nothing; this does).</summary>
    public static async Task SyncAsync(AppDbContext db, CalendarKeys keys, CalendarConnection connection, CancellationToken ct)
    {
        db.ActingUserId = connection.UserId;
        var account = await CalendarItems.FindAsync<CalendarAccountItem>(db, connection.UserId, CalendarContract.Accounts, connection.Account, ct);
        if (account is null && connection.State == CalendarCloudState.Connecting && connection.UpdateDate > DateTime.UtcNow.AddMinutes(-15))
        {
            // Handed over just now: the item of the account may still be on its way from the device.
            connection.NextSync = DateTime.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (account is null || account.Data.SyncMode != CalendarSyncMode.Cloud || account.Data.Kind != connection.Kind)
        {
            await RemoveAsync(db, connection, ct);
            return;
        }

        if (connection.Kind == CalendarAccountKind.Ics)
        {
            connection.Url = account.Data.Url;
        }

        var now = DateTime.UtcNow;
        try
        {
            var provider = await ProviderAsync(db, keys, connection, ct);
            var items = await ChangesAsync(db, connection, provider, ct);
            await CalendarItems.PushAsync(db, CalendarItems.Server(connection.UserId), items, ct);
            connection.State = CalendarCloudState.Ready;
            connection.Problem = string.Empty;
            connection.Failures = 0;
            connection.LastSync = now;
        }
        catch (ProviderException exception) when (exception.IsSignInExpired)
        {
            connection.State = CalendarCloudState.SignInRequired;
            connection.ProtectedToken = string.Empty;
            connection.Problem = Clip(exception.Message);
        }
        catch (ProviderException exception)
        {
            connection.State = connection.State == CalendarCloudState.Connecting && connection.Failures == 0 ? CalendarCloudState.Connecting : CalendarCloudState.Failing;
            connection.Failures++;
            connection.Problem = Clip(exception.Message);
            if (connection.Failures > 1)
            {
                connection.State = CalendarCloudState.Failing;
            }
        }

        connection.NextSync = connection.State switch
        {
            CalendarCloudState.SignInRequired => Never,
            CalendarCloudState.Ready => now.AddMinutes(await IntervalAsync(db, connection.UserId, ct)),
            _ => now.AddMinutes(Math.Min(60, CalendarContract.MinSyncMinutes << Math.Min(connection.Failures, 4))),
        };
        await db.SaveChangesAsync(ct);
        await WriteStateAsync(db, connection, ct);
    }

    /// <summary>The provider of the connection, signed in with a fresh access token.</summary>
    internal static async Task<ICalendarProvider> ProviderAsync(AppDbContext db, CalendarKeys keys, CalendarConnection connection, CancellationToken ct)
    {
        if (connection.Kind == CalendarAccountKind.Ics)
        {
            return new IcsProvider(connection.Url);
        }

        var profile = await keys.ProfileAsync(db, connection, ct)
                      ?? throw new ProviderException("Für Google fehlt dieser MatCloud die Registrierung von ThixCalendar – der Administrator richtet sie ein.");
        var refreshToken = keys.Unprotect(connection.ProtectedToken);
        if (refreshToken.Length == 0)
        {
            throw new ProviderException("Die Anmeldung fehlt – bitte neu anmelden.", isSignInExpired: true);
        }

        var tokens = await OAuthClient.RefreshAsync(profile, refreshToken, ct);
        if (tokens.RefreshToken is { Length: > 0 } rotated && rotated != refreshToken)
        {
            connection.ProtectedToken = keys.Protect(rotated);
            await db.SaveChangesAsync(ct); // the old one may be spent already: never lose the new one
        }

        var accessToken = tokens.AccessToken;
        Task<string> AccessToken(CancellationToken _) => Task.FromResult(accessToken);
        return connection.Kind == CalendarAccountKind.Microsoft ? new GraphProvider(AccessToken) : new GoogleProvider(AccessToken);
    }

    /// <summary>The calendars and events of the provider against the items of the account: only what differs.</summary>
    private static async Task<List<SyncItemDto>> ChangesAsync(AppDbContext db, CalendarConnection connection, ICalendarProvider provider, CancellationToken ct)
    {
        var prefix = connection.Account + "/";
        var calendars = await provider.ListCalendarsAsync(ct);
        var items = new List<SyncItemDto>();
        var known = await KnownAsync(db, connection.UserId, CalendarContract.CloudCalendars, prefix, ct);
        var knownEvents = await KnownAsync(db, connection.UserId, CalendarContract.CloudEvents, prefix, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);
        var from = DateTimeOffset.UtcNow.Date.AddDays(-CalendarContract.WindowPastDays);
        var to = DateTimeOffset.UtcNow.Date.AddDays(CalendarContract.WindowFutureDays + 1);
        foreach (var calendar in calendars)
        {
            var calendarKey = CalendarContract.CalendarKey(connection.Account, calendar.RemoteId);
            if (!seen.Add(calendarKey))
            {
                continue;
            }

            Add(items, known, CalendarContract.CloudCalendars, calendarKey, new CalendarCloudCalendar(
                connection.Account, calendar.RemoteId, calendar.Name, calendar.Color ?? CalendarColors.Default, calendar.IsReadOnly, calendar.IsPrimary,
                calendar.DefaultReminderMinutes));

            var info = new CalendarInfo
            {
                RemoteId = calendar.RemoteId,
                Name = calendar.Name,
                IsReadOnly = calendar.IsReadOnly,
                DefaultReminderMinutes = calendar.DefaultReminderMinutes,
            };
            foreach (var calendarEvent in await provider.ListEventsAsync(info, from, to, ct))
            {
                var eventKey = CalendarContract.EventKey(calendarKey, calendarEvent.RemoteId);
                if (calendarEvent.RemoteId.Length > 0 && seenEvents.Add(eventKey))
                {
                    Add(items, knownEvents, CalendarContract.CloudEvents, eventKey, CalendarItems.ToCloud(calendarKey, calendarEvent));
                }
            }
        }

        items.AddRange(known.Keys.Where(key => !seen.Contains(key)).Select(key => CalendarItems.Deleted(CalendarContract.CloudCalendars, key)));
        items.AddRange(knownEvents.Keys.Where(key => !seenEvents.Contains(key)).Select(key => CalendarItems.Deleted(CalendarContract.CloudEvents, key)));
        return items;
    }

    /// <summary>The live items of a collection that belong to the account (their keys begin with its SyncId), with their hashes.</summary>
    private static async Task<Dictionary<string, string?>> KnownAsync(AppDbContext db, long userId, string collection, string prefix, CancellationToken ct)
        => await CalendarItems.Live(db, userId, collection).Where(i => i.Key.StartsWith(prefix))
            .ToDictionaryAsync(i => i.Key, i => i.ContentHash, StringComparer.Ordinal, ct);

    private static void Add<T>(List<SyncItemDto> items, IReadOnlyDictionary<string, string?> known, string collection, string key, T data)
    {
        var item = CalendarItems.Item(collection, key, data);
        if (!known.TryGetValue(key, out var hash) || hash != item.ContentHash)
        {
            items.Add(item);
        }
    }

    /// <summary>Lets an account go: forgets its sign-in and removes everything MatCloud wrote for it.</summary>
    internal static async Task RemoveAsync(AppDbContext db, CalendarConnection connection, CancellationToken ct)
    {
        var prefix = connection.Account + "/";
        var keys = await db.SyncItems.AsNoTracking()
            .Where(i => i.UserId == connection.UserId && i.App == CalendarItems.App && i.UpdateState != SoftEntity.Deleted &&
                        ((i.Collection == CalendarContract.CloudAccounts && i.Key == connection.Account) ||
                         ((i.Collection == CalendarContract.CloudCalendars || i.Collection == CalendarContract.CloudEvents) && i.Key.StartsWith(prefix))))
            .Select(i => new { i.Collection, i.Key })
            .ToListAsync(ct);
        db.CalendarConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        await CalendarItems.PushAsync(db, CalendarItems.Server(connection.UserId), keys.Select(k => CalendarItems.Deleted(k.Collection, k.Key)).ToList(), ct);
    }

    /// <summary>The state of the account for the devices and the pages; written when it changed (and the time of the last sync at most every hour).</summary>
    internal static async Task WriteStateAsync(AppDbContext db, CalendarConnection connection, CancellationToken ct)
    {
        var state = new CalendarCloudAccount(connection.Kind, connection.UserName, connection.State,
            connection.LastSync is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null, connection.Problem);
        var current = await CalendarItems.FindAsync<CalendarCloudAccount>(db, connection.UserId, CalendarContract.CloudAccounts, connection.Account, ct);
        if (current is not null && current.Data with { LastSync = null } == state with { LastSync = null } &&
            (current.Data.LastSync is null) == (state.LastSync is null) &&
            (state.LastSync is null || state.LastSync - current.Data.LastSync < TimeSpan.FromHours(1)))
        {
            return;
        }

        await CalendarItems.PushAsync(db, CalendarItems.Server(connection.UserId), [CalendarItems.Item(CalendarContract.CloudAccounts, connection.Account, state)], ct);
    }

    /// <summary>The sync interval the user chose in ThixCalendar (at least <see cref="CalendarContract.MinSyncMinutes"/>).</summary>
    private static async Task<int> IntervalAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var settings = await CalendarItems.FindAsync<CalendarSettingsItem>(db, userId, CalendarContract.Settings, CalendarContract.SettingsKey, ct);
        return Math.Max(CalendarContract.MinSyncMinutes, settings?.Data.SyncIntervalMinutes ?? 15);
    }

    private static string Clip(string text) => text.Length <= 500 ? text : text[..500];
}
