using MatCloud.Core.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages;

/// <summary>
/// "Gerät verbinden": the page a Thix app opens for its sign-in in the browser (<c>/Connect?code=ABCD-EFGH</c>). The person signs in (the sign-in
/// comes back here), compares the code with the one in the app and confirms or refuses it. Without a code (or a wrong one) the page asks for it.
/// </summary>
public sealed class ConnectModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    public DeviceLinkView? Link { get; private set; }

    /// <summary>The device that was just connected (shown after the confirmation).</summary>
    public DeviceView? Connected { get; private set; }

    public bool Refused { get; private set; }

    public string UserName => Me.Snapshot?.UserName ?? string.Empty;

    public async Task OnGetAsync()
    {
        if (!string.IsNullOrWhiteSpace(Code))
        {
            var found = await DeviceLinkService.FindAsync(Db, Code, HttpContext.RequestAborted);
            if (found.Ok)
            {
                Link = found.Value;
            }
            else
            {
                AddError(nameof(Code), "Diesen Code gibt es nicht, er ist abgelaufen oder schon benutzt. Bitte in der App noch einmal anmelden.");
            }
        }
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        var outcome = await DeviceLinkService.ApproveAsync(Db, Actor, Code, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(nameof(Code), outcome.Error ?? "Diesen Code gibt es nicht.");
            return Page();
        }

        Connected = outcome.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostDenyAsync()
    {
        await DeviceLinkService.DenyAsync(Db, Actor, Code, HttpContext.RequestAborted);
        Refused = true;
        return Page();
    }
}
