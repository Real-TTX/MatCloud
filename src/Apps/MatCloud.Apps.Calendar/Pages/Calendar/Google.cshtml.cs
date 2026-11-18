using MatCloud.Apps.Calendar.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>Where Google sends the browser back after the sign-in (the redirect address of this MatCloud's Google web client): connects and goes to "Konten".</summary>
public sealed class GoogleModel : AppPageModel
{
    public async Task<IActionResult> OnGetAsync(string? state, string? code, string? error)
    {
        var keys = HttpContext.RequestServices.GetRequiredService<CalendarKeys>();
        var outcome = await CalendarConnections.FinishGoogleAsync(Db, keys, Actor, state, code, error, HttpContext.RequestAborted);
        if (outcome.Ok)
        {
            Flash("Das Google-Konto ist verbunden – MatCloud gleicht es jetzt ab.");
        }
        else
        {
            FlashError(outcome.Error ?? "Die Anmeldung bei Google hat nicht geklappt.");
        }

        return Redirect("/Calendar/Accounts");
    }
}
