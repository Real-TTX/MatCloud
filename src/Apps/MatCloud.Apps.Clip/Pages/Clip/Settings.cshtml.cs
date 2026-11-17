using System.Globalization;
using MatCloud.Apps.Clip.Business;
using MatCloud.Contract.Clip;
using MatCloud.Core.Controls;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Clip.Pages.Clip;

/// <summary>The settings of the clipboard: how long clips are kept (for every device of the account).</summary>
public sealed class SettingsModel : AppPageModel
{
    public static IReadOnlyList<Choice> RetentionChoices { get; } = ClipContract.RetentionChoices
        .Select(d => new Choice(d.ToString(CultureInfo.InvariantCulture), ClipSettingsService.RetentionName(d), d == 0 ? "Einträge bleiben, bis du sie löschst" : null))
        .ToList();

    [BindProperty]
    public int RetentionDays { get; set; } = ClipContract.DefaultRetentionDays;

    public async Task OnGetAsync()
        => RetentionDays = (await ClipSettingsService.GetAsync(Db, Actor, HttpContext.RequestAborted)).RetentionDays;

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await ClipSettingsService.SetRetentionAsync(Db, Actor, RetentionDays, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage();
    }
}
