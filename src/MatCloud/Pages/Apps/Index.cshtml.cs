using MatCloud.Core.Business;
using MatCloud.Core.Web;

namespace MatCloud.Pages.Apps;

/// <summary>The apps of the person with what each keeps in the cloud (the apps that are switched on).</summary>
public sealed class IndexModel : AppPageModel
{
    public IReadOnlyList<MyAppView> Apps { get; private set; } = [];

    public async Task OnGetAsync() => Apps = await AppService.MineAsync(Db, Actor, HttpContext.RequestAborted);
}
