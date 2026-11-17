using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Web;

namespace MatCloud.Pages.Account;

/// <summary>
/// The tab "Geräte": the apps on the computers signed in to the account (ThixClip on ANNA-PC, ThixNote on the MacBook). A device is added by signing in
/// in the app; it is removed on its own page.
/// </summary>
public sealed class DevicesModel : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<DeviceView> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var app = ListQuery.Choice(Request, "app", [.. CloudApps.All.Select(a => a.Id)]);
        var sort = ListQuery.Choice(Request, "sort", "seen", "name", "app", "created") ?? "seen";
        var descending = ListQuery.Descending(Request, byDefault: sort is not ("name" or "app"));
        var (items, total) = await DeviceService.ListAsync(Db, Actor, search, app, sort, descending);
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "devices",
            Action = "/Account/Devices",
            Label = "Geräte",
            Search = search,
            SearchPlaceholder = "Geräte durchsuchen",
            Filters = [new FilterField("app", "App", [.. CloudApps.All.Select(a => new FilterOption(a.Id, a.Name))], app)],
            SortOptions = [new SortOption("seen", "Zuletzt gesehen"), new SortOption("name", "Name"), new SortOption("app", "App"), new SortOption("created", "Angemeldet seit")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "seen",
            DefaultDescending = true,
            Total = total,
            Matching = items.Count,
            Noun = "Geräte",
            NounOne = "Gerät",
        };
    }
}
