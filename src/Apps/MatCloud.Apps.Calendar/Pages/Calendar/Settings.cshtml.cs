using MatCloud.Apps.Calendar.Business;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Api;
using MatCloud.Core.Controls;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Einstellungen" of the calendar: who keeps a new online account in step (the default travels to ThixCalendar on every computer) and how often MatCloud
/// syncs; for an administrator also the Google web client of this MatCloud (the sign-in at Google in the browser).
/// </summary>
public sealed class SettingsModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Modes =
    [
        new(nameof(CalendarSyncMode.Cloud), "Über MatCloud", "MatCloud hält die Anmeldung und gleicht ab – auch wenn kein Computer läuft; der Browser zeigt alle Termine."),
        new(nameof(CalendarSyncMode.Computers), "Auf den Computern", "Jeder Computer mit ThixCalendar meldet sich selbst an; die Anmeldungen bleiben auf den Computern."),
    ];

    public static IReadOnlyList<Choice> Intervals => CalendarPages.SyncIntervals.Select(m => new Choice(m.ToString(), $"alle {m} Minuten")).ToList();

    [BindProperty] public CalendarSyncMode DefaultSyncMode { get; set; }

    [BindProperty] public int SyncIntervalMinutes { get; set; }

    [BindProperty] public string GoogleClientId { get; set; } = string.Empty;

    [BindProperty] public string GoogleClientSecret { get; set; } = string.Empty;

    public bool IsAdmin => Me.IsAdmin;

    public bool GoogleFromEnvironment => CalendarKeys.GoogleWebClientFromEnvironment;

    public bool HasGoogleSecret { get; private set; }

    public string RedirectUri { get; private set; } = string.Empty;

    public async Task OnGetAsync()
    {
        var settings = await CalendarPages.SettingsAsync(Db, Actor, HttpContext.RequestAborted);
        DefaultSyncMode = settings.DefaultSyncMode;
        SyncIntervalMinutes = settings.SyncIntervalMinutes;
        await LoadGoogleAsync(fill: true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await CalendarPages.SaveSettingsAsync(Db, Actor, DefaultSyncMode, SyncIntervalMinutes, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            await LoadGoogleAsync(fill: true);
            return Page();
        }

        Flash("Gespeichert – ThixCalendar übernimmt es auf deinen Computern.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGoogleAsync()
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var outcome = await CalendarAdmin.SaveGoogleWebClientAsync(Db, keys, Actor, GoogleClientId, GoogleClientSecret, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            var settings = await CalendarPages.SettingsAsync(Db, Actor, HttpContext.RequestAborted);
            DefaultSyncMode = settings.DefaultSyncMode;
            SyncIntervalMinutes = settings.SyncIntervalMinutes;
            await LoadGoogleAsync(fill: false);
            return Page();
        }

        Flash(GoogleClientId.Trim().Length > 0 ? "Der Google-Zugang ist eingerichtet: Google-Konten lassen sich jetzt auch im Browser verbinden." : "Der Google-Zugang ist entfernt.");
        return RedirectToPage();
    }

    private async Task LoadGoogleAsync(bool fill)
    {
        RedirectUri = await DeviceEndpoints.BaseUrlAsync(HttpContext, Db) + "/Calendar/Google";
        if (!IsAdmin)
        {
            return;
        }

        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var client = await keys.GoogleWebClientAsync(Db, HttpContext.RequestAborted);
        HasGoogleSecret = client.ClientSecret.Length > 0;
        if (fill)
        {
            GoogleClientId = client.ClientId;
        }
    }
}
