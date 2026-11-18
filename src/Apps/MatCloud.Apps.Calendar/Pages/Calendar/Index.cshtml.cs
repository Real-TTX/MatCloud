using System.Globalization;
using MatCloud.Apps.Calendar.Business;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Kalender": the shown calendars in MatUI's schedule (Tag | Woche | Monat | Agenda, like ThixCalendar's calendar page: one slim bar, the grid gets the room).
/// The page itself carries the calendars (switched on and off beside the grid, the choice travels to ThixCalendar); the events of what is shown come from
/// <see cref="OnGetEventsAsync"/> as JSON (calendar-schedule.js).
/// </summary>
public sealed class IndexModel : AppPageModel
{
    /// <summary>The widest span the events are given for at once (the agenda of a month with the weeks around it fits).</summary>
    private const int MaxDays = 62;

    public string View { get; private set; } = "week";

    public DateTime Date { get; private set; }

    public IReadOnlyList<CalendarEntry> Calendars { get; private set; } = [];

    public long Latest { get; private set; }

    public DateTime Today { get; } = Dates.Local(DateTime.UtcNow).Date;

    public string PeriodUrl(DateTime date, string? view = null) => $"/Calendar?view={view ?? View}&date={date:yyyy-MM-dd}";

    public static string KindIcon(CalendarAccountKind kind) => CalendarPages.KindIcon(kind);

    public async Task OnGetAsync(string? view, string? date)
    {
        View = view is "day" or "month" or "agenda" ? view : "week";
        Date = ParseDate(date) ?? Today;
        var ct = HttpContext.RequestAborted;
        Latest = await CalendarPages.LatestAsync(Db, Actor, ct);
        Calendars = await CalendarPages.CalendarsAsync(Db, Actor, ct);
    }

    /// <summary>
    /// The events of the shown calendars on the local days <paramref name="from"/> … <paramref name="to"/> (both included) as MatUI's schedule takes them:
    /// local times ("2026-11-18T09:00"; an all-day event as dates with its last day as the end), the address of the event, the colour of its calendar.
    /// </summary>
    public async Task<IActionResult> OnGetEventsAsync(string? from, string? to)
    {
        var first = ParseDate(from);
        var last = ParseDate(to);
        if (first is null || last is null || last < first || (last.Value - first.Value).TotalDays > MaxDays)
        {
            return BadRequest();
        }

        var ct = HttpContext.RequestAborted;
        var calendars = await CalendarPages.CalendarsAsync(Db, Actor, ct);
        var events = await CalendarPages.EventsAsync(Db, Actor, calendars, first.Value, last.Value.AddDays(1), shownOnly: true, search: null, ct);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new
        {
            latest = await CalendarPages.LatestAsync(Db, Actor, ct),
            events = events.Select(e => new
            {
                id = e.Key,
                title = e.Data.Title.Length > 0 ? e.Data.Title : "(ohne Titel)",
                start = e.Data.IsAllDay ? e.StartLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : e.StartLocal.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                end = e.Data.IsAllDay
                    ? (e.EndLocal > e.StartLocal ? e.EndLocal.AddDays(-1) : e.StartLocal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : e.EndLocal.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                allDay = e.Data.IsAllDay,
                location = e.Data.Location,
                note = e.Calendar.Name,
                status = e.IsCancelled ? "Abgesagt" : e.IsDeclined ? "Abgelehnt" : null,
                url = e.EditUrl,
                colour = e.Calendar.Color,
                off = e.IsCancelled || e.IsDeclined,
            }),
        });
    }

    /// <summary>Shows or hides a calendar. calendar-schedule.js posts it in the background (the answer is all it needs); without script the page comes back.</summary>
    public async Task<IActionResult> OnPostShowAsync(string key, bool visible, string? returnUrl)
    {
        var outcome = await CalendarPages.SetVisibleAsync(Db, Actor, key, visible, HttpContext.RequestAborted);
        if (Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal))
        {
            return outcome.Ok ? new JsonResult(new { ok = true }) : NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError("Den Kalender gibt es nicht mehr.");
        }

        return Back(returnUrl, "/Calendar");
    }

    private static DateTime? ParseDate(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;
}
