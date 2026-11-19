using MatCloud.Apps.Drive.Business;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>"Zuletzt verwendet": the files the account opened or saved last – in the browser or in ThixWrite, ThixCalc, ThixShow on any computer.</summary>
public sealed class RecentModel : DriveListPageModel
{
    public const int Count = 50;

    public async Task OnGetAsync()
    {
        ReadTools();
        var ct = HttpContext.RequestAborted;
        Revision = await DriveItems.LatestAsync(Db, Actor.UserId, ct);
        var recent = (await DriveItems.RecentAsync(Db, Actor, Count, ct)).Items;
        var items = Narrow(recent);
        Toolbar = BuildToolbar("/Drive/Recent", "Zuletzt verwendet", recent.Count, items.Count, placeholder: "Zuletzt verwendete durchsuchen", sortable: false);
        List = new DriveListView(items, Tiles, ShowLocation: true, Trash: false, Here);
        await LoadFoldersAsync();
    }
}
