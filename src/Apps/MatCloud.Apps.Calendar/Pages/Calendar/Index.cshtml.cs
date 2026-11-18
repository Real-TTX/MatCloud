using System.Globalization;
using MatCloud.Apps.Calendar.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Kalender": a week or a month of the shown calendars, like ThixCalendar's calendar page – one slim toolbar (‹ Heute ›, the period, Woche | Monat),
/// the grid below. The calendars are switched on and off in the list beside it (the choice travels to ThixCalendar).
/// </summary>
public sealed class IndexModel : AppPageModel
{

    public string View { get; private set; } = "week";

    public DateTime Date { get; private set; }

    /// <summary>The first day shown (a Monday) and the day after the last.</summary>
    public DateTime From { get; private set; }

    public DateTime To { get; private set; }

    /// <summary>The month the month view is about.</summary>
    public DateTime Month { get; private set; }

    public IReadOnlyList<CalendarEntry> Calendars { get; private set; } = [];

    public IReadOnlyList<EventEntry> Events { get; private set; } = [];

    public long Latest { get; private set; }

    public DateTime Today { get; } = Dates.Local(DateTime.UtcNow).Date;

    public string Title => View == "month"
        ? GermanDates.MonthYear(Month)
        : $"KW {ISOWeek.GetWeekOfYear(From)} · {GermanDates.Short(From)} – {GermanDates.Short(To.AddDays(-1), year: true)}";

    public string PeriodUrl(DateTime date, string? view = null) => $"/Calendar?view={view ?? View}&date={date:yyyy-MM-dd}";

    public string Previous => PeriodUrl(View == "month" ? Month.AddMonths(-1) : Date.AddDays(-7));

    public string Next => PeriodUrl(View == "month" ? Month.AddMonths(1) : Date.AddDays(7));

    public IEnumerable<DateTime> Days()
    {
        for (var day = From; day < To; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    public IEnumerable<EventEntry> On(DateTime day) => Events.Where(e => e.Touches(day));

    public static string DayName(DateTime day) => GermanDates.DayShort(day);

    public async Task OnGetAsync(string? view, string? date)
    {
        View = view == "month" ? "month" : "week";
        Date = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : Today;
        if (View == "month")
        {
            Month = new DateTime(Date.Year, Date.Month, 1);
            From = Monday(Month);
            To = Monday(Month.AddMonths(1).AddDays(-1)).AddDays(7);
        }
        else
        {
            From = Monday(Date);
            To = From.AddDays(7);
            Month = new DateTime(Date.Year, Date.Month, 1);
        }

        var ct = HttpContext.RequestAborted;
        Latest = await CalendarPages.LatestAsync(Db, Actor, ct);
        Calendars = await CalendarPages.CalendarsAsync(Db, Actor, ct);
        Events = await CalendarPages.EventsAsync(Db, Actor, Calendars, From, To, shownOnly: true, search: null, ct);
    }

    public async Task<IActionResult> OnPostShowAsync(string key, bool visible, string? returnUrl)
    {
        var outcome = await CalendarPages.SetVisibleAsync(Db, Actor, key, visible, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            FlashError("Den Kalender gibt es nicht mehr.");
        }

        return Back(returnUrl, "/Calendar");
    }

    private static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
