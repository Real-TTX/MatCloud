using MatCloud.Apps.Calendar.Business;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Api;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Konten": the accounts of ThixCalendar with who keeps them in step (the computers or MatCloud) and how the last sync went; an account can be handed to
/// MatCloud or back to the computers, signed in again, synced now or removed. Below: connect a new one (Microsoft with a code, Google through this MatCloud's
/// web client, an ICS address).
/// </summary>
public sealed class AccountsModel : AppPageModel
{
    public IReadOnlyList<CalendarAccountView> Accounts { get; private set; } = [];

    public bool GoogleInBrowser { get; private set; }

    public bool GoogleFromComputer { get; private set; }

    public long Latest { get; private set; }

    [BindProperty] public string IcsName { get; set; } = string.Empty;

    [BindProperty] public string IcsUrl { get; set; } = string.Empty;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostModeAsync(string key, CalendarSyncMode mode)
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var outcome = await CalendarConnections.SetModeAsync(Db, keys, Actor, key, mode, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            FlashError("Das Konto gibt es nicht mehr.");
        }
        else
        {
            Flash(mode == CalendarSyncMode.Cloud
                ? "Das Konto läuft jetzt über MatCloud. Melde es hier an (oder in ThixCalendar am Computer), dann gleicht MatCloud ab."
                : "Das Konto läuft wieder über die Computer: MatCloud hat seine Anmeldung vergessen.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncAsync(string key)
    {
        await CalendarConnections.SyncNowAsync(Db, Actor, key, HttpContext.RequestAborted);
        Flash("Der Abgleich läuft – die Seite zeigt das Ergebnis gleich.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string key)
    {
        var outcome = await CalendarConnections.DeleteAccountAsync(Db, Actor, key, HttpContext.RequestAborted);
        if (outcome.Ok)
        {
            Flash("Das Konto ist entfernt – auch in ThixCalendar auf deinen Computern.");
        }
        else
        {
            FlashError("Das Konto gibt es nicht mehr.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostIcsAsync()
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var outcome = await CalendarConnections.AddIcsAsync(Db, keys, Actor, IcsName, IcsUrl, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field == "Url" ? nameof(IcsUrl) : nameof(IcsName), outcome.Error);
            await LoadAsync();
            return Page();
        }

        Flash("Das Abo ist da – MatCloud liest es gleich und hält es aktuell.");
        return RedirectToPage();
    }

    /// <summary>Starts the sign-in at Google in the browser (with <paramref name="account"/>: an existing account again).</summary>
    public async Task<IActionResult> OnGetGoogleAsync(string? account)
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var redirect = await DeviceEndpoints.BaseUrlAsync(HttpContext, Db) + "/Calendar/Google";
        var url = await CalendarConnections.StartGoogleAsync(Db, keys, Actor, redirect, account, HttpContext.RequestAborted);
        if (url is null)
        {
            FlashError("Im Browser geht Google nur, wenn der Administrator dieser MatCloud den Google-Zugang eingerichtet hat. Sonst: in ThixCalendar am Computer anmelden.");
            return RedirectToPage();
        }

        return Redirect(url);
    }

    private async Task LoadAsync()
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var ct = HttpContext.RequestAborted;
        Latest = await CalendarPages.LatestAsync(Db, Actor, ct);
        Accounts = await CalendarConnections.ListAsync(Db, Actor, ct);
        GoogleInBrowser = (await keys.GoogleWebClientAsync(Db, ct)).IsSet;
        GoogleFromComputer = keys.GoogleAppSecret.Length > 0;
    }
}
