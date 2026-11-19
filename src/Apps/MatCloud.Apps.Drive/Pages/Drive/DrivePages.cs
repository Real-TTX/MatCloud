using System.Web;
using MatCloud.Apps.Drive.Business;
using MatCloud.Contract.Drive;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MatCloud.Apps.Drive.Pages.Drive;

/// <summary>A list of items as the partial <c>_DriveList</c> draws it: list or tiles, with the folder of each item (search, recent …) or without.</summary>
public sealed record DriveListView(IReadOnlyList<DriveItem> Items, bool Tiles, bool ShowLocation, bool Trash, string Here);

/// <summary>
/// What every page of the Drive has: the store, and a 404 when the account may not use the Drive (switched off by an administrator) – the business
/// layer checks again on every call.
/// </summary>
public abstract class DrivePageModel : AppPageModel
{
    protected DriveStore Store => HttpContext.RequestServices.GetRequiredService<DriveStore>();

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await DriveAccess.MayUseAsync(Db, Actor, HttpContext.RequestAborted))
        {
            context.Result = NotFound();
            return;
        }

        await next();
    }

    /// <summary>The message of a failed change, at the top of the next page.</summary>
    protected void Report<T>(DriveOutcome<T> outcome, string success)
    {
        if (outcome.Ok)
        {
            Flash(success);
        }
        else
        {
            FlashError(outcome.Error ?? "Das hat nicht geklappt.");
        }
    }
}

/// <summary>
/// A list page of the Drive (a folder, recent, starred): the toolbar (search, type, sort, view), the rows with their selection, the actions on the
/// selection below the list (rename, move, trash) and the star of a row.
/// </summary>
public abstract class DriveListPageModel : DrivePageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; protected set; } = null!;

    public PagerModel? Pager { get; protected set; }

    public DriveListView List { get; protected set; } = null!;

    /// <summary>The folders a selection can be moved to (the move dialog).</summary>
    public IReadOnlyList<(long Id, string Path)> Folders { get; protected set; } = [];

    /// <summary>The latest revision the page shows (the live list waits for a newer one).</summary>
    public long Revision { get; protected set; }

    public string Here => Request.Path + Request.QueryString;

    protected string? Search { get; private set; }

    protected string? Category { get; private set; }

    protected string SortValue { get; private set; } = "name";

    protected bool Descending { get; private set; }

    protected bool Tiles { get; private set; }

    protected int PageNumber { get; private set; } = 1;

    protected DriveQuery Query => new(SortOf(SortValue), Descending, Category, (PageNumber - 1) * PageSize, PageSize);

    /// <summary>Reads search, type, sort, view and page from the address.</summary>
    protected void ReadTools(string defaultSort = "name", bool defaultDescending = false)
    {
        Search = ListQuery.Text(Request, "q");
        Category = ListQuery.Choice(Request, "type", DriveTypes.Categories.Select(c => c.Value).ToArray());
        SortValue = ListQuery.Choice(Request, "sort", "name", "modified", "size", "type") ?? defaultSort;
        Descending = ListQuery.Descending(Request, defaultDescending);
        Tiles = ListQuery.Choice(Request, "view", "tiles") is not null;
        PageNumber = ListQuery.Page(Request);
    }

    /// <summary>The toolbar of the page; <paramref name="keep"/> are the values of the address that are not tools (the folder).</summary>
    protected ToolbarModel BuildToolbar(string action, string label, int total, int matching, IDictionary<string, string?>? keep = null, string placeholder = "In der ganzen Ablage suchen",
        string defaultSort = "name", bool defaultDescending = false, bool sortable = true)
    {
        var kept = new Dictionary<string, string?>(keep ?? new Dictionary<string, string?>());
        if (Tiles)
        {
            kept["view"] = "tiles";
        }

        return new ToolbarModel
        {
            Id = "drive",
            Action = action,
            Label = label,
            Search = Search,
            SearchPlaceholder = placeholder,
            Filters = [new FilterField("type", "Typ", DriveTypes.Categories.Select(c => new FilterOption(c.Value, c.Label)).ToList(), Category)],
            SortOptions = sortable ? [new SortOption("name", "Name"), new SortOption("modified", "Geändert"), new SortOption("size", "Größe"), new SortOption("type", "Typ")] : [],
            Sort = SortValue,
            Descending = Descending,
            DefaultSort = defaultSort,
            DefaultDescending = defaultDescending,
            DescendingLabel = "Absteigend (Z–A, neueste, größte zuerst)",
            AscendingLabel = "Aufsteigend (A–Z, älteste, kleinste zuerst)",
            Total = total,
            Matching = matching,
            Noun = "Elemente",
            NounOne = "Element",
            Keep = kept,
            Views =
            [
                new ViewOption(WithView(Here, null), "list", "Liste", !Tiles),
                new ViewOption(WithView(Here, "tiles"), "grid", "Kacheln", Tiles),
            ],
        };
    }

    /// <summary>A short list (recent, starred) narrowed by the search and the type of the toolbar.</summary>
    protected IReadOnlyList<DriveItem> Narrow(IReadOnlyList<DriveItem> items)
        => items.Where(i => (Search is null || i.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
                            && (Category is null || DriveTypes.CategoryOf(i.Kind, i.Name) == Category)).ToList();

    protected PagerModel? BuildPager(int total)
        => total <= PageSize ? null : new PagerModel { Page = PageNumber, PageSize = PageSize, Total = total, Url = Toolbar.UrlWithPage };

    protected async Task LoadFoldersAsync() => Folders = await DriveItems.FoldersAsync(Db, Actor, HttpContext.RequestAborted);

    // ---- the actions on the selection (below the list) ----

    public async Task<IActionResult> OnPostRenameAsync(long[] ids, string? name, string? returnUrl)
    {
        if (ids.Length != 1)
        {
            FlashError("Bitte genau ein Element zum Umbenennen wählen.");
            return Back(returnUrl, "/Drive");
        }

        Report(await DriveItems.RenameAsync(Db, Actor, ids[0], name, HttpContext.RequestAborted), "Umbenannt.");
        return Back(returnUrl, "/Drive");
    }

    public async Task<IActionResult> OnPostMoveAsync(long[] ids, string? target, string? returnUrl)
    {
        long? to = long.TryParse(target, out var id) ? id : null;
        var moved = 0;
        foreach (var item in ids)
        {
            var outcome = await DriveItems.MoveAsync(Db, Actor, item, to, HttpContext.RequestAborted);
            if (!outcome.Ok)
            {
                FlashError(outcome.Error ?? "Das hat nicht geklappt.");
                return Back(returnUrl, "/Drive");
            }

            moved++;
        }

        Flash(moved == 1 ? "1 Element verschoben." : $"{moved} Elemente verschoben.");
        return Back(returnUrl, "/Drive");
    }

    public async Task<IActionResult> OnPostTrashAsync(long[] ids, string? returnUrl)
    {
        var trashed = 0;
        foreach (var item in ids)
        {
            trashed += (await DriveItems.TrashAsync(Db, Actor, item, HttpContext.RequestAborted)).Ok ? 1 : 0;
        }

        Flash(trashed == 1 ? "1 Element liegt im Papierkorb." : $"{trashed} Elemente liegen im Papierkorb.");
        return Back(returnUrl, "/Drive");
    }

    public async Task<IActionResult> OnPostStarAsync(long id, bool on, string? returnUrl)
    {
        var outcome = await DriveItems.StarAsync(Db, Actor, id, on, HttpContext.RequestAborted);
        return outcome.Ok ? Back(returnUrl, "/Drive") : NotFound();
    }

    internal static DriveSort SortOf(string value) => value switch
    {
        "modified" => DriveSort.Modified,
        "size" => DriveSort.Size,
        "type" => DriveSort.Type,
        _ => DriveSort.Name,
    };

    private static string WithView(string url, string? view)
    {
        var path = url.Split('?')[0];
        var query = HttpUtility.ParseQueryString(url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : string.Empty);
        query.Remove("view");
        query.Remove("page");
        if (view is not null)
        {
            query["view"] = view;
        }

        var text = query.ToString();
        return string.IsNullOrEmpty(text) ? path : path + "?" + text;
    }
}

/// <summary>What the pages say about items: icons, kinds, sizes, the brand of the app that saved a version.</summary>
public static class DriveUi
{
    public static string IconOf(DriveItem item) => DriveTypes.IconOf(DriveTypes.CategoryOf(item.Kind, item.Name));

    public static string KindOf(DriveItem item) => DriveTypes.LabelOf(item.Kind, item.Name);

    public static string UrlOf(DriveItem item) => item.Kind == DriveItemKind.Folder && !item.IsTrashed ? $"/Drive?folder={item.Id}" : $"/Drive/Item?id={item.Id}";

    public static string FolderUrl(long? folderId) => folderId is null ? "/Drive" : $"/Drive?folder={folderId}";

    public static string SizeOf(DriveItem item) => item.Kind == DriveItemKind.Folder ? "–" : Ui.Bytes(item.Size);

    /// <summary>"ThixWrite auf ANNA-PC", "Web".</summary>
    public static string SourceOf(string? source, string? app) => app is null ? source ?? "–" : $"{AppBrands.Of(app).Name} auf {source}";

    /// <summary>The office app of a document (its brand), or null.</summary>
    public static AppBrand? OfficeBrandOf(DriveItem item)
        => item.Kind == DriveItemKind.File && DriveTypes.OfficeAppOf(DriveTypes.CategoryOf(item.Kind, item.Name)) is { } app ? AppBrands.Of(app) : null;
}
