using System.Globalization;
using MatCloud.Apps.Calendar.Business;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Controls;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Termin": a new event or one to change. An event of an own calendar is written into the items at once; one of an account MatCloud keeps goes to Microsoft /
/// Google first. An event that cannot be changed here (an ICS address, a read-only calendar) is shown without the form's inputs.
/// </summary>
public sealed class EventModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> ReminderChoices =
    [
        new(string.Empty, "Standard des Kalenders"), new("0", "Zu Beginn"), new("5", "5 Minuten vorher"), new("10", "10 Minuten vorher"),
        new("15", "15 Minuten vorher"), new("30", "30 Minuten vorher"), new("60", "1 Stunde vorher"), new("120", "2 Stunden vorher"),
        new("1440", "1 Tag vorher"),
    ];

    public EventEntry? Existing { get; private set; }

    public IReadOnlyList<CalendarEntry> Writable { get; private set; } = [];

    public bool IsNew => Existing is null;

    public bool CanEdit => IsNew || Existing!.Calendar.IsWritable;

    [BindProperty] public string? Key { get; set; }

    [BindProperty] public string Src { get; set; } = "own";

    [BindProperty] public string Title { get; set; } = string.Empty;

    [BindProperty] public string Calendar { get; set; } = string.Empty;

    [BindProperty] public bool IsAllDay { get; set; }

    [BindProperty] public string StartDate { get; set; } = string.Empty;

    [BindProperty] public string StartTime { get; set; } = string.Empty;

    [BindProperty] public string EndDate { get; set; } = string.Empty;

    [BindProperty] public string EndTime { get; set; } = string.Empty;

    [BindProperty] public string Location { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Reminder { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(string? key, string? src, string? date)
    {
        if (!await LoadAsync(key, src))
        {
            return NotFound();
        }

        if (Existing is null)
        {
            var day = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Dates.Local(DateTime.UtcNow).Date;
            var start = day == Dates.Local(DateTime.UtcNow).Date ? Dates.Local(DateTime.UtcNow).AddHours(1) : day.AddHours(9);
            start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
            Fill(start, start.AddHours(1));
            Calendar = Writable.FirstOrDefault()?.Key ?? string.Empty;
            return Page();
        }

        var e = Existing.Data;
        Title = e.Title;
        Calendar = e.Calendar;
        IsAllDay = e.IsAllDay;
        Fill(Existing.StartLocal, e.IsAllDay ? Existing.EndLocal.AddDays(-1) : Existing.EndLocal);
        Location = e.Location;
        Description = e.Description;
        Reminder = e.ReminderMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync(Key, Src))
        {
            return NotFound();
        }

        if (!CanEdit)
        {
            FlashError("Dieser Termin lässt sich nur lesen.");
            return RedirectToPage(new { key = Key, src = Src });
        }

        if (!TryTimes(out var start, out var end))
        {
            return Page();
        }

        var target = Writable.FirstOrDefault(c => c.Key == Calendar);
        if (target is null)
        {
            AddError(nameof(Calendar), "Bitte einen Kalender wählen.");
            return Page();
        }

        int? reminder = int.TryParse(Reminder, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
        var ct = HttpContext.RequestAborted;
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        if (target.IsOwn && (Existing is null || !Existing.IsCloud))
        {
            var outcome = await CalendarEvents.SaveOwnAsync(Db, Actor, Existing?.Key,
                new CalendarEventItem(target.Key, Title.Trim(), start, end, IsAllDay, Location.Trim(), Description, string.Empty, reminder,
                    Existing?.Data.Status ?? CalendarEventStatus.Confirmed), ct);
            if (!outcome.Ok)
            {
                AddError(outcome.Field, outcome.Error);
                return Page();
            }
        }
        else if (!target.IsOwn && (Existing is null || Existing.IsCloud))
        {
            var data = (Existing?.Data ?? new CalendarCloudEvent()) with
            {
                Calendar = target.Key,
                Title = Title.Trim(),
                Start = start,
                End = end,
                IsAllDay = IsAllDay,
                Location = Location.Trim(),
                Description = Description,
                ReminderMinutes = reminder,
            };
            var outcome = await CalendarEvents.ChangeCloudAsync(Db, keys, Actor, new CalendarEventChange(Existing?.Key, data), ct);
            if (!outcome.Ok)
            {
                AddError(outcome.Field is "Title" or "End" or "Location" or "Description" ? outcome.Field : null, outcome.Error);
                return Page();
            }
        }
        else
        {
            AddError(nameof(Calendar), "Ein Termin wechselt nicht zwischen eigenen Kalendern und Konten von Microsoft oder Google.");
            return Page();
        }

        Flash(IsNew ? "Der Termin ist angelegt – auch in ThixCalendar." : "Der Termin ist gespeichert – auch in ThixCalendar.");
        return Redirect($"/Calendar?view=week&date={StartDate}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync(Key, Src) || Existing is null)
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        string? error;
        if (Existing.IsCloud)
        {
            var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
            var outcome = await CalendarEvents.ChangeCloudAsync(Db, keys, Actor, new CalendarEventChange(Existing.Key, null, Delete: true), ct);
            error = outcome.Ok ? null : outcome.Error ?? "Den Termin gibt es nicht mehr.";
        }
        else
        {
            var outcome = await CalendarEvents.DeleteOwnAsync(Db, Actor, Existing.Key, ct);
            error = outcome.Ok ? null : outcome.Error ?? "Den Termin gibt es nicht mehr.";
        }

        if (error is not null)
        {
            FlashError(error);
            return RedirectToPage(new { key = Key, src = Src });
        }

        Flash("Der Termin ist gelöscht – auch in ThixCalendar.");
        return Redirect("/Calendar");
    }

    private async Task<bool> LoadAsync(string? key, string? src)
    {
        var ct = HttpContext.RequestAborted;
        Writable = (await CalendarPages.CalendarsAsync(Db, Actor, ct)).Where(c => c.IsWritable).ToList();
        Src = src == "cloud" ? "cloud" : "own";
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        Existing = await CalendarPages.EventAsync(Db, Actor, key, Src == "cloud", ct);
        Key = key;
        return Existing is not null;
    }

    private void Fill(DateTime start, DateTime end)
    {
        StartDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        EndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The times of the form as absolute times (all-day: dates at midnight UTC, the end the midnight after the last day).</summary>
    private bool TryTimes(out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        if (!DateTime.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDay))
        {
            AddError(nameof(StartDate), "Bitte ein Datum für den Beginn wählen.");
            return false;
        }

        if (!DateTime.TryParseExact(EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDay))
        {
            AddError(nameof(EndDate), "Bitte ein Datum für das Ende wählen.");
            return false;
        }

        if (IsAllDay)
        {
            start = new DateTimeOffset(startDay, TimeSpan.Zero);
            end = new DateTimeOffset(endDay.AddDays(1), TimeSpan.Zero);
        }
        else
        {
            if (!TimeOnly.TryParseExact(StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startClock))
            {
                AddError(nameof(StartTime), "Bitte eine Uhrzeit für den Beginn wählen.");
                return false;
            }

            if (!TimeOnly.TryParseExact(EndTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endClock))
            {
                AddError(nameof(EndTime), "Bitte eine Uhrzeit für das Ende wählen.");
                return false;
            }

            start = Absolute(startDay.Add(startClock.ToTimeSpan()));
            end = Absolute(endDay.Add(endClock.ToTimeSpan()));
        }

        if (end < start)
        {
            AddError(nameof(EndDate), "Das Ende liegt vor dem Beginn.");
            return false;
        }

        return true;
    }

    private static DateTimeOffset Absolute(DateTime local) => new(local, Dates.Zone.GetUtcOffset(local));
}
