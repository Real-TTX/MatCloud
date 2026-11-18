using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>
/// Changes events. Of an account MatCloud keeps in step (cloud-events): at the provider first, then what the provider stored goes into the items – the
/// same rule as in ThixCalendar ("the providers are the truth"). Of an own calendar (events): straight into the items of the general sync.
/// </summary>
public static class CalendarEvents
{
    public const int MaxTitleLength = 500;
    public const int MaxTextLength = 100_000;

    /// <summary>A device of ThixCalendar (or the browser) changes an event of an account MatCloud keeps in step.</summary>
    public static async Task<Outcome<CalendarEventChangeResult>> ChangeCloudAsync(AppDbContext db, CalendarKeys keys, Actor actor, CalendarEventChange? change,
        CancellationToken ct)
    {
        if (!actor.MayUse(CalendarItems.App))
        {
            throw new NotAllowedException("Dieses Gerät darf diese Daten nicht lesen.");
        }

        if (change is null || (change.Event is null && change.Data is null) || (!change.Delete && change.Data is null))
        {
            return Outcome<CalendarEventChangeResult>.Fail("Der Termin fehlt.", "data");
        }

        var existing = change.Event is null ? null
            : await CalendarItems.FindAsync<CalendarCloudEvent>(db, actor.UserId, CalendarContract.CloudEvents, change.Event, ct);
        if (change.Event is not null && existing is null)
        {
            return Outcome<CalendarEventChangeResult>.Missing();
        }

        if (!change.Delete && Validate(change.Data!) is { } problem)
        {
            return Outcome<CalendarEventChangeResult>.Fail(problem.Message, problem.Field);
        }

        var calendarKey = change.Data?.Calendar is { Length: > 0 } target && !change.Delete ? target : existing!.Data.Calendar;
        var calendar = await CalendarItems.FindAsync<CalendarCloudCalendar>(db, actor.UserId, CalendarContract.CloudCalendars, calendarKey, ct);
        var account = calendarKey.Split('/')[0];
        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == actor.UserId && c.Account == account, ct);
        if (calendar is null || connection is null)
        {
            return Outcome<CalendarEventChangeResult>.Fail("MatCloud hält diesen Kalender nicht (mehr) – das Konto läuft über die Computer.", "calendar");
        }

        if (connection.State == CalendarCloudState.SignInRequired)
        {
            return Outcome<CalendarEventChangeResult>.Fail("Das Konto braucht eine neue Anmeldung, bevor sich Termine ändern lassen.", "calendar");
        }

        if (calendar.Data.IsReadOnly || connection.Kind == CalendarAccountKind.Ics)
        {
            return Outcome<CalendarEventChangeResult>.Fail("Dieser Kalender lässt sich nur lesen.", "calendar");
        }

        db.ActingUserId = actor.UserId;
        try
        {
            var provider = await CalendarCloudSync.ProviderAsync(db, keys, connection, ct);
            var info = Info(calendar.Data);
            var server = CalendarItems.Server(actor.UserId);
            if (change.Delete)
            {
                await provider.DeleteEventAsync(info, CalendarItems.FromCloud(existing!.Data), ct);
                await CalendarItems.PushAsync(db, server, [CalendarItems.Deleted(CalendarContract.CloudEvents, existing.Key)], ct);
                return Outcome<CalendarEventChangeResult>.Success(new CalendarEventChangeResult(existing.Key, null));
            }

            var wanted = CalendarItems.FromCloud(change.Data! with { Calendar = calendarKey });
            CalendarEvent stored;
            var items = new List<SyncItemDto>();
            if (existing is null)
            {
                stored = await provider.CreateEventAsync(info, wanted with { RemoteId = string.Empty }, ct);
            }
            else if (existing.Data.Calendar != calendarKey)
            {
                // Into another calendar: created there, then removed from the old one (the providers cannot move between calendars of every kind).
                stored = await provider.CreateEventAsync(info, wanted with { RemoteId = string.Empty }, ct);
                var oldCalendar = await CalendarItems.FindAsync<CalendarCloudCalendar>(db, actor.UserId, CalendarContract.CloudCalendars, existing.Data.Calendar, ct);
                if (oldCalendar is not null)
                {
                    await provider.DeleteEventAsync(Info(oldCalendar.Data), CalendarItems.FromCloud(existing.Data), ct);
                }

                items.Add(CalendarItems.Deleted(CalendarContract.CloudEvents, existing.Key));
            }
            else
            {
                stored = await provider.UpdateEventAsync(info, CalendarItems.FromCloud(existing.Data), wanted with { RemoteId = existing.Data.RemoteId }, ct);
            }

            var key = CalendarContract.EventKey(calendarKey, stored.RemoteId);
            if (existing is not null && existing.Key != key && !items.Any(i => i.Key == existing.Key))
            {
                items.Add(CalendarItems.Deleted(CalendarContract.CloudEvents, existing.Key));
            }

            var data = CalendarItems.ToCloud(calendarKey, stored);
            items.Add(CalendarItems.Item(CalendarContract.CloudEvents, key, data));
            await CalendarItems.PushAsync(db, server, items, ct);
            return Outcome<CalendarEventChangeResult>.Success(new CalendarEventChangeResult(key, data));
        }
        catch (ProviderException exception)
        {
            if (exception.IsSignInExpired)
            {
                connection.State = CalendarCloudState.SignInRequired;
                connection.ProtectedToken = string.Empty;
                connection.Problem = exception.Message;
                connection.NextSync = CalendarCloudSync.Never;
                await db.SaveChangesAsync(ct);
                await CalendarCloudSync.WriteStateAsync(db, connection, ct);
            }

            return Outcome<CalendarEventChangeResult>.Fail(exception.Message, "data");
        }
    }

    /// <summary>The browser saves an event of an own calendar ("Eigene Kalender"): <paramref name="key"/> null creates one.</summary>
    public static async Task<Outcome<string>> SaveOwnAsync(AppDbContext db, Actor actor, string? key, CalendarEventItem data, CancellationToken ct)
    {
        RequireBrowser(actor);
        var calendar = await CalendarItems.FindAsync<CalendarCalendarItem>(db, actor.UserId, CalendarContract.Calendars, data.Calendar, ct);
        var account = calendar is null ? null
            : await CalendarItems.FindAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, data.Calendar.Split('/')[0], ct);
        if (account?.Data.Kind != CalendarAccountKind.Local)
        {
            return Outcome<string>.Fail("Den Kalender gibt es nicht.", "Calendar");
        }

        if (Validate(data.Title, data.Start, data.End, data.Location, data.Description) is { } problem)
        {
            return Outcome<string>.Fail(problem.Message, problem.Field);
        }

        if (key is not null && await CalendarItems.FindAsync<CalendarEventItem>(db, actor.UserId, CalendarContract.Events, key, ct) is null)
        {
            return Outcome<string>.Missing();
        }

        key ??= CalendarItems.NewKey();
        var meeting = MeetingLinks.Find(data.Location, data.Description);
        await CalendarItems.PushAsync(db, actor, [CalendarItems.Item(CalendarContract.Events, key, data with
        {
            Start = data.Start.ToUniversalTime(),
            End = data.End.ToUniversalTime(),
            MeetingUrl = data.MeetingUrl.Length > 0 ? data.MeetingUrl : meeting,
        })], ct);
        return Outcome<string>.Success(key);
    }

    public static async Task<Outcome> DeleteOwnAsync(AppDbContext db, Actor actor, string key, CancellationToken ct)
    {
        RequireBrowser(actor);
        if (await CalendarItems.FindAsync<CalendarEventItem>(db, actor.UserId, CalendarContract.Events, key, ct) is null)
        {
            return Outcome.Missing();
        }

        await CalendarItems.PushAsync(db, actor, [CalendarItems.Deleted(CalendarContract.Events, key)], ct);
        return Outcome.Success();
    }

    public static (string Message, string Field)? Validate(CalendarCloudEvent data) =>
        Validate(data.Title, data.Start, data.End, data.Location, data.Description);

    public static (string Message, string Field)? Validate(string title, DateTimeOffset start, DateTimeOffset end, string location, string description)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ("Der Titel fehlt.", "Title");
        }

        if (title.Length > MaxTitleLength)
        {
            return ($"Der Titel hat höchstens {MaxTitleLength} Zeichen.", "Title");
        }

        if (end < start)
        {
            return ("Das Ende liegt vor dem Beginn.", "End");
        }

        if (location.Length > MaxTitleLength)
        {
            return ($"Der Ort hat höchstens {MaxTitleLength} Zeichen.", "Location");
        }

        return description.Length > MaxTextLength ? ($"Die Notizen haben höchstens {MaxTextLength / 1000}.000 Zeichen.", "Description") : null;
    }

    private static CalendarInfo Info(CalendarCloudCalendar calendar) => new()
    {
        RemoteId = calendar.RemoteId,
        Name = calendar.Name,
        IsReadOnly = calendar.IsReadOnly,
        IsPrimary = calendar.IsPrimary,
        DefaultReminderMinutes = calendar.DefaultReminderMinutes,
    };

    private static void RequireBrowser(Actor actor)
    {
        if (actor.DeviceId is not null)
        {
            throw new NotAllowedException("Nur im Browser.");
        }
    }
}
