using MatCloud.Core.Business;
using MatCloud.Core.Web;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages.Admin;

/// <summary>
/// The system (administrators only): the numbers of the installation, the public address (for the sign-in of devices in the browser; fixed while
/// <c>MAT_PUBLIC_URL</c> is set) and the housekeeping that otherwise runs every hour.
/// </summary>
public sealed class IndexModel(MatPlatformSettings platform) : AppPageModel
{
    [BindProperty]
    public string PublicUrl { get; set; } = string.Empty;

    public SystemSnapshot Snapshot { get; private set; } = null!;

    /// <summary>The public address set by <c>MAT_PUBLIC_URL</c> (then it is not edited here).</summary>
    public string? FixedPublicUrl => platform.AppPublicUrl;

    public bool IdentityEnabled => platform.IdentityEnabled;

    public async Task OnGetAsync()
    {
        UserService.RequireAdmin(Actor);
        Snapshot = await SystemService.SnapshotAsync(Db, HttpContext.RequestAborted);
        PublicUrl = await SystemService.PublicUrlAsync(Db, HttpContext.RequestAborted) ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await SystemService.SetPublicUrlAsync(Db, Actor, PublicUrl, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            Snapshot = await SystemService.SnapshotAsync(Db, HttpContext.RequestAborted);
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        UserService.RequireAdmin(Actor);
        var (sessions, links, tombstones, modules) = await SystemService.CleanUpAsync(Db, HttpContext.RequestAborted);
        Flash($"Aufgeräumt: {sessions} Sitzungen, {links} Geräte-Anmeldungen, {tombstones} alte Löschmarken, {modules} abgelaufene Einträge der Apps.");
        return RedirectToPage();
    }
}
