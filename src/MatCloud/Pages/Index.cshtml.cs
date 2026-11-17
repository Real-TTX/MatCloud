using MatCloud.Core.Business;
using MatCloud.Core.Web;

namespace MatCloud.Pages;

/// <summary>
/// The overview: every app that is switched on as a card with the devices of the person and what it keeps; an app with a module opens its pages,
/// an app of the general sync its page under Apps.
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public IReadOnlyList<MyAppView> Apps { get; private set; } = [];

    public int Devices => Apps.Sum(a => a.Devices);

    /// <summary>The address of this MatCloud as the browser reached it (what an app needs).</summary>
    public string ServerAddress => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public async Task OnGetAsync() => Apps = await AppService.MineAsync(Db, Actor, HttpContext.RequestAborted);
}
