using MatCloud.Apps.Calendar.Business;
using MatCloud.Core.Web;

namespace MatCloud.Apps.Calendar.Pages.Calendar;

/// <summary>"Termine": the events of a period as a list, grouped by day – search (title, place, notes), calendar and period above, the pages below.</summary>
public sealed class EventsModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<EventEntry> Items { get; private set; } = [];

    public int Total { get; private set; }

    public long Latest { get; private set; }

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var calendar = ListQuery.Text(Request, "calendar");
        var period = ListQuery.Choice(Request, "period", "7", "30", "90", "past") ?? "30";
        var page = ListQuery.Page(Request);
        var ct = HttpContext.RequestAborted;
        var today = Dates.Local(DateTime.UtcNow).Date;
        var (from, to) = period switch
        {
            "past" => (today.AddDays(-14), today.AddDays(1)),
            "7" => (today, today.AddDays(7)),
            "90" => (today, today.AddDays(90)),
            _ => (today, today.AddDays(30)),
        };

        Latest = await CalendarPages.LatestAsync(Db, Actor, ct);
        var calendars = await CalendarPages.CalendarsAsync(Db, Actor, ct);
        var all = await CalendarPages.EventsAsync(Db, Actor, calendars, from, to, shownOnly: false, search: null, ct);
        var matching = await CalendarPages.EventsAsync(Db, Actor, calendars.Where(c => calendar is null || c.Key == calendar).ToList(), from, to, false, search, ct);
        var ordered = period == "past" ? matching.OrderByDescending(e => e.StartLocal).ToList() : matching.OrderBy(e => e.StartLocal).ToList();
        Total = all.Count;
        Items = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        Toolbar = new ToolbarModel
        {
            Id = "events",
            Action = "/Calendar/Events",
            Label = "Termine",
            Search = search,
            SearchPlaceholder = "Termine durchsuchen",
            Filters =
            [
                new FilterField("calendar", "Kalender", [.. calendars.Where(c => c.HasEvents).Select(c => new FilterOption(c.Key, $"{c.Name} ({c.AccountName})"))], calendar),
                new FilterField("period", "Zeitraum",
                    [new FilterOption("7", "Nächste 7 Tage"), new FilterOption("30", "Nächste 30 Tage"), new FilterOption("90", "Nächste 90 Tage"), new FilterOption("past", "Letzte 14 Tage")],
                    period == "30" ? null : period),
            ],
            Total = all.Count,
            Matching = matching.Count,
            Noun = "Termine",
            NounOne = "Termin",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }
}
