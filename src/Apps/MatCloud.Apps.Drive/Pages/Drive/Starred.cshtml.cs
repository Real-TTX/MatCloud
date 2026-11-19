using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>"Mit Stern": the items the account starred, wherever they lie.</summary>
public sealed class StarredModel : DriveListPageModel
{
    public async Task OnGetAsync()
    {
        ReadTools();
        var ct = HttpContext.RequestAborted;
        Revision = await DriveItems.LatestAsync(Db, Actor.UserId, ct);
        var starred = (await DriveItems.StarredAsync(Db, Actor, Query with { Category = null, Offset = 0, Limit = DriveContract.MaxPageSize }, ct)).Items;
        var items = Narrow(starred);
        Toolbar = BuildToolbar("/Drive/Starred", "Mit Stern", starred.Count, items.Count, placeholder: "Markierte durchsuchen");
        List = new DriveListView(items.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList(), Tiles, ShowLocation: true, Trash: false, Here);
        Pager = BuildPager(items.Count);
        await LoadFoldersAsync();
    }
}
