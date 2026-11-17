using MatCloud.Core.Business;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Pages.Account;

/// <summary>The tab "Allgemein" of the account: the name. Also saves the theme of the menu button.</summary>
public sealed class IndexModel : AppPageModel
{
    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    public AccountView Account { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await AccountService.GetAsync(Db, Actor);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Account = outcome.Value!;
        DisplayName = Account.DisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await AccountService.SaveGeneralAsync(Db, Actor, DisplayName);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            Account = (await AccountService.GetAsync(Db, Actor)).Value!;
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage();
    }

    /// <summary>The theme button of the menu posts here (theme=system|light|dark).</summary>
    public async Task<IActionResult> OnPostThemeAsync(string theme)
    {
        await AccountService.SetThemeAsync(Db, Actor, theme);
        return new NoContentResult();
    }
}
