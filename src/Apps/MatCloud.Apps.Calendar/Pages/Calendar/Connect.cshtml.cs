using MatCloud.Apps.Calendar.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>
/// "Microsoft verbinden": the sign-in at Microsoft with a code (works with every address of MatCloud, no secret): the page shows the code and the address,
/// the person confirms it in another tab, the page asks every few seconds (/live/calendar/microsoft/{id}) and goes to "Konten" when the account is there.
/// </summary>
public sealed class ConnectModel : AppPageModel
{
    public MicrosoftCodeStart? Start { get; private set; }

    public string? Account { get; private set; }

    public void OnGet(string? account) => Account = account;

    public async Task<IActionResult> OnPostAsync(string? account)
    {
        Account = account;
        var outcome = await CalendarConnections.StartMicrosoftAsync(Actor, account, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(null, outcome.Error);
            return Page();
        }

        Start = outcome.Value;
        return Page();
    }
}
