using System.Net;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The public calendar - deliberately NOT a Blazor component. Blazor Server pages are served
/// through a shared host shell (App.razor) that unconditionally loads blazor.web.js and the
/// reconnect/error-UI infrastructure, regardless of whether the specific page declares any
/// interactivity - for an anonymous visitor, that shared runtime's own background circuit activity
/// gets rejected and surfaces as a visible "unhandled error" banner, with no clean way to opt one
/// page out of it (confirmed via live testing - removing render mode from the page itself, and then
/// from HeadOutlet too, didn't stop it). This endpoint sidesteps that whole category of problem by
/// hand-building the HTML response directly, the same way PublicAvailabilityEndpoints already does
/// for the JSON API - no circuit, no Blazor client runtime, nothing to reject.
///
/// Three views (Month/Week/Day, mirroring the staff calendar), all rendered server-side as complete
/// HTML documents - a view/nav change is a plain full-page navigation, not client-side routing, to
/// keep this surface free of any framework. The Week/Day hourly grids reuse the exact same
/// CalendarStyles hour-axis math and lane-layout algorithm as the staff Week/Day grids do.
///
/// Category filtering (added 2026-08-04) follows the same "no framework" rule: the SHOW row is a
/// plain GET form, submitted via full page reload, matching the pattern /public/search's own
/// date-range/sheets form already established rather than introducing client-side JS state.
/// </summary>
public static class PublicCalendarEndpoint
{
    internal enum ViewMode { Month, Week, Day }

    /// <summary>Which sheet categories are showing, and whether Club Events are showing - mirrors
    /// the staff calendar's own SHOW row granularity exactly (6 sheet categories + one club-events
    /// toggle, not finer-grained per-club-event-category filtering, since the staff calendar
    /// doesn't have that either).</summary>
    internal sealed record FilterState(bool IsFiltered, HashSet<BookingCategory> Categories, bool ShowClubEvents)
    {
        public static readonly FilterState Default = new(false, AllCategories, true);
    }

    // Event is reserved for Club Events (CalendarStyles.SheetCategories already excludes it) - not
    // a sheet-booking category a member could filter by here.
    internal static readonly HashSet<BookingCategory> AllCategories = [.. CalendarStyles.SheetCategories];

    public static void MapPublicCalendarEndpoint(this WebApplication app)
    {
        app.MapGet("/public/calendar", async (string? view, string? month, string? date,
            string? filtered, string[]? categories, string? showClubEvents,
            PublicAvailabilityService service, FacilityConfiguration facility, CancellationToken ct) =>
        {
            var today = facility.Today;
            var mode = ParseView(view);
            var filter = ParseFilter(filtered, categories, showClubEvents);
            var html = mode switch
            {
                ViewMode.Week => await RenderWeekPageAsync(ParseDate(date, today) ?? today, today, filter, service, ct),
                ViewMode.Day => await RenderDayPageAsync(ParseDate(date, today) ?? today, today, filter, service, ct),
                _ => await RenderMonthPageAsync(ResolveMonthAnchor(month, date, today), today, filter, service, ct),
            };
            return Results.Content(html, "text/html; charset=utf-8");
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    internal static ViewMode ParseView(string? view) => view?.ToLowerInvariant() switch
    {
        "week" => ViewMode.Week,
        "day" => ViewMode.Day,
        _ => ViewMode.Month,
    };

    // The month is clamped to a window around today, not accepted verbatim: DateTime.TryParse
    // allows years 0001-9999, i.e. ~120k distinct values - and every previously-unseen month is a
    // cache miss in GetMonthViewAsync that fans out live Graph calls across every mailbox and adds
    // a new never-evicted cache entry. Unclamped, an anonymous client iterating months could burn
    // Graph quota and grow the cache without bound. A year back and two years forward covers every
    // legitimate use (members browsing the season) while capping the anonymous surface at ~37 keys.
    // "today" is facility-local (FacilityConfiguration.Today), not DateTime.UtcNow - passed in by the
    // caller rather than read here, so this stays a pure function of its arguments.
    internal static DateTime? ParseMonth(string? month, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(month) || !DateTime.TryParse(month + "-01", out var parsed))
        {
            return null;
        }

        var min = new DateTime(today.Year - 1, today.Month, 1);
        var max = new DateTime(today.Year + 2, today.Month, 1);
        return parsed < min ? min : parsed > max ? max : parsed;
    }

    /// <summary>
    /// Which date Month view anchors on. <c>month=</c> wins when present, since that's what every
    /// Prev/Today/Next and Month-tab link emits. <c>date=</c> is the fallback so the date-jump
    /// picker works here too: it produces a full day, and Month view only needs the month that day
    /// falls in. Without this, a jump in Month view submitted <c>date=</c>, hit a handler reading
    /// only <c>month=</c>, and silently landed back on today.
    /// </summary>
    internal static DateTime ResolveMonthAnchor(string? month, string? date, DateTime today) =>
        ParseMonth(month, today) ?? ParseDate(date, today) ?? today;

    // Same clamping rationale as ParseMonth, applied to Week/Day's ?date= parameter.
    internal static DateTime? ParseDate(string? date, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(date) || !DateTime.TryParse(date, out var parsed))
        {
            return null;
        }

        var min = today.AddYears(-1);
        var max = today.AddYears(2);
        return parsed.Date < min ? min : parsed.Date > max ? max : parsed.Date;
    }

    // A bare link (no ?filtered= at all) means "no filter has ever been applied" - show everything,
    // exactly today's behavior, so existing bookmarks/embeds/shared links keep working unchanged.
    // Once the SHOW form is submitted, it always includes a hidden filtered=1 marker alongside
    // zero-or-more categories= values and showClubEvents - presence-based rather than parsing
    // "true"/"1" strings, since an unchecked checkbox is simply absent from a GET submission and
    // there's no other way to distinguish "unchecked" from "form never submitted".
    //
    // No recognized category present (whether none were ever listed, or every box was unchecked)
    // falls back to "all categories" rather than "none" - showing a blank calendar because every
    // box happened to be unchecked isn't a real use case worth supporting, and defaulting back to
    // "all" is a friendlier failure mode than an empty grid. showClubEvents doesn't get the same
    // fallback: unlike the multi-select category chips, it's a single on/off toggle where "off" is
    // the entire point of unchecking it, so its absence has to reliably mean off once filtered=1 is
    // present.
    internal static FilterState ParseFilter(string? filtered, string[]? categories, string? showClubEvents)
    {
        if (string.IsNullOrEmpty(filtered))
        {
            return FilterState.Default;
        }

        var selected = ParseCategories(categories);
        return new FilterState(true, selected.Count == 0 ? AllCategories : selected, !string.IsNullOrEmpty(showClubEvents));
    }

    internal static HashSet<BookingCategory> ParseCategories(string[]? categories)
    {
        if (categories is null || categories.Length == 0)
        {
            return [];
        }

        return [.. categories
            .Select(c => Enum.TryParse<BookingCategory>(c, out var parsed) && AllCategories.Contains(parsed) ? (BookingCategory?)parsed : null)
            .Where(c => c is not null)
            .Select(c => c!.Value)];
    }

    // Appended to every nav/tab href so Prev/Today/Next and the Month/Week/Day toggle don't silently
    // reset the filter on the next click. Empty for the unfiltered default, keeping those links exactly
    // as clean as before this feature existed. Categories are omitted entirely when the full set is
    // selected (the common case - a member only toggled Club Events, or hasn't excluded anything) since
    // ParseFilter already treats "no categories present" as "all" - listing every one explicitly would
    // just make the URL longer without changing what it means.
    internal static string FilterQuery(FilterState filter) =>
        string.Concat(FilterParams(filter).Select(p => $"&{p.Name}={p.Value}"));

    /// <summary>
    /// The same filter state as hidden form fields. A GET form <em>replaces</em> the entire query
    /// string on submit, so a form that needs to preserve the filter (the date jump below) can't
    /// lean on <see cref="FilterQuery"/> in its action attribute - the params would be discarded.
    /// </summary>
    private static string FilterHiddenFields(FilterState filter) =>
        string.Concat(FilterParams(filter).Select(p =>
            $"""<input type="hidden" name="{p.Name}" value="{H(p.Value)}">"""));

    // One source of truth for what the filter serialises to, consumed by both renderers above -
    // deliberately not two hand-maintained copies of the same rule, which is how a policy and its
    // duplicate drifted apart in D75.
    private static IEnumerable<(string Name, string Value)> FilterParams(FilterState filter)
    {
        if (!filter.IsFiltered)
        {
            yield break;
        }

        yield return ("filtered", "1");

        if (!filter.Categories.SetEquals(AllCategories))
        {
            foreach (var category in filter.Categories)
            {
                yield return ("categories", category.ToString());
            }
        }

        if (filter.ShowClubEvents)
        {
            yield return ("showClubEvents", "1");
        }
    }

    private static PublicMonthView ApplyFilter(PublicMonthView view, FilterState filter) => view with
    {
        Bookings = [.. view.Bookings.Where(b => filter.Categories.Contains(ParseCategory(b.CategoryLabel)))],
        ClubEvents = filter.ShowClubEvents ? view.ClubEvents : []
    };

    private static string MonthHref(DateTime month, string filterQuery) => $"/public/calendar?view=month&month={month:yyyy-MM}{filterQuery}";
    private static string WeekHref(DateTime date, string filterQuery) => $"/public/calendar?view=week&date={date:yyyy-MM-dd}{filterQuery}";
    private static string DayHref(DateTime date, string filterQuery) => $"/public/calendar?view=day&date={date:yyyy-MM-dd}{filterQuery}";

    // Every piece of dynamic text (titles, category labels) MUST go through this before landing in
    // the hand-built HTML - there's no Razor auto-escaping here to fall back on.
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static BookingCategory ParseCategory(string label) =>
        Enum.TryParse<BookingCategory>(label, out var category) ? category : BookingCategory.Other;

    private static string FormatClubEventRange(PublicClubEventLabel ce)
    {
        if (ce.IsAllDay)
        {
            return ce.Start.Date == ce.End.Date
                ? ce.Start.ToString("dddd, MMM d")
                : $"{ce.Start:MMM d} - {ce.End:MMM d, yyyy}";
        }

        return ce.Start.Date == ce.End.Date
            ? $"{ce.Start:dddd, MMM d} · {ce.Start:h:mmtt}-{ce.End:h:mmtt}"
            : $"{ce.Start:MMM d, h:mmtt} - {ce.End:MMM d, h:mmtt yyyy}";
    }

    private static IEnumerable<DateTime> MonthCells(DateTime anchorMonth)
    {
        var firstOfMonth = new DateTime(anchorMonth.Year, anchorMonth.Month, 1);
        var gridStart = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);
        var gridEnd = lastOfMonth.AddDays(6 - (int)lastOfMonth.DayOfWeek);

        for (var d = gridStart; d <= gridEnd; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    private static void AppendPageOpen(StringBuilder sb)
    {
        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Club Calendar</title>
            </head>
            <body style="font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:0;color:#1e2a33">
            <header style="background:#1e2a33;color:#fff;padding:10px 24px;font-weight:600;font-size:14px;display:flex;align-items:center;gap:16px">
                <span>GCC Ice &amp; Event Calendar</span>
                <a href="/public/practice-ice" style="color:#a9c7e4;font-size:12px;font-weight:600;text-decoration:none;margin-left:auto">Host practice ice</a>
                <a href="/public/search" style="color:#a9c7e4;font-size:12px;font-weight:600;text-decoration:none">Search available ice &#8250;</a>
            </header>
            <div style="padding:16px 24px;max-width:1150px">
            """);
    }

    private static void AppendPageClose(StringBuilder sb)
    {
        sb.Append("</div>");
        sb.Append(OverlayAndScript);
        sb.Append("</body></html>");
    }

    // Shared date/view navigator - Prev/Today/Next (each view-aware: steps by month/week/day) plus
    // a Month/Week/Day toggle. representativeDate is the anchor used to translate the CURRENT view
    // into each toggle target's own query-param scheme, so switching views doesn't lose context
    // (e.g. switching from Month to Week lands on the week containing the displayed month's 1st).
    private static string NavBar(string titleText, string prevHref, string todayHref, string nextHref, ViewMode current, DateTime representativeDate, FilterState filter)
    {
        var filterQuery = FilterQuery(filter);

        string Tab(string label, ViewMode mode, string href)
        {
            var style = mode == current
                ? "background:#2d5f8a;color:#fff;border:1px solid #2d5f8a"
                : "background:#fff;color:#2d5f8a;border:1px solid #d7dfe5";
            return $"""<a href="{href}" class="pub-cal-nav-link" style="padding:4px 12px;border-radius:6px;font-weight:600;font-size:13px;text-decoration:none;{style}">{label}</a>""";
        }

        return $"""
            <div style="display:flex;align-items:center;gap:14px;margin-bottom:6px;flex-wrap:wrap">
                <span style="font-size:16px;font-weight:600;color:#1e2a33">{H(titleText)}</span>
                <span style="display:flex;align-items:center;gap:8px;font-size:12px">
                    <a href="{prevHref}" class="pub-cal-nav-link" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">&#8249;</a>
                    <a href="{todayHref}" class="pub-cal-nav-link" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">Today</a>
                    <a href="{nextHref}" class="pub-cal-nav-link" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">&#8250;</a>
                </span>
                {DateJumpForm(current, representativeDate, filter)}
                <span style="display:flex;gap:4px;margin-left:auto">
                    {Tab("Month", ViewMode.Month, MonthHref(representativeDate, filterQuery))}
                    {Tab("Week", ViewMode.Week, WeekHref(representativeDate, filterQuery))}
                    {Tab("Day", ViewMode.Day, DayHref(representativeDate, filterQuery))}
                </span>
            </div>
            <div style="font-size:13px;color:#90a0ab;margin-bottom:12px">What's on the ice, at a glance - tap an entry to see its time.</div>
            """;
    }

    /// <summary>
    /// Jump straight to a date rather than stepping a month at a time. A plain GET form, matching
    /// how every other control on this page navigates (D66) - no client-side routing.
    ///
    /// Always submits <c>date=</c> (never <c>month=</c>), even in Month view, because a date picker
    /// yields a day and <c>ParseDate</c> then clamps and anchors the month from it - one param to
    /// handle instead of branching on the current view. The current filter rides along as hidden
    /// fields, since a GET submit discards whatever was in the query string before it.
    ///
    /// The picker auto-submits on change so it behaves like the staff calendar's; the Go button is
    /// the no-JS fallback and stays functional on its own. This page already ships small amounts of
    /// JS for the loading overlay (D23) and chip popups, so this introduces no new dependency - and
    /// D15 constrains it to being a plain endpoint, not to being JS-free.
    /// </summary>
    private static string DateJumpForm(ViewMode current, DateTime anchor, FilterState filter) => $"""
        <form method="get" style="display:flex;align-items:center;gap:6px">
            <input type="hidden" name="view" value="{current.ToString().ToLowerInvariant()}">
            {FilterHiddenFields(filter)}
            <input type="date" name="date" value="{anchor:yyyy-MM-dd}" aria-label="Jump to date"
                   onchange="this.form.submit()"
                   style="border:1px solid #d7dfe5;border-radius:6px;padding:4px 8px;font-size:13px;color:#1e2a33;font-family:inherit;cursor:pointer">
            <button type="submit" style="border:1px solid #d7dfe5;background:#fff;color:#2d5f8a;border-radius:6px;padding:4px 10px;font-size:13px;font-weight:600;cursor:pointer">Go</button>
        </form>
        """;

    // The SHOW row - a plain GET form (no client-side JS), same pattern /public/search's own filter
    // form already uses. Hidden fields carry the current view/date so applying a filter doesn't also
    // reset which page you're looking at. anchorHiddenField gives Month vs. Week/Day their own
    // param name (month= vs date=), matching MonthHref/WeekHref/DayHref's own scheme.
    private static string AppendCategoryFilterForm(ViewMode mode, DateTime anchor, FilterState filter)
    {
        var anchorField = mode == ViewMode.Month
            ? $"""<input type="hidden" name="month" value="{anchor:yyyy-MM}">"""
            : $"""<input type="hidden" name="date" value="{anchor:yyyy-MM-dd}">""";
        var viewField = $"""<input type="hidden" name="view" value="{mode.ToString().ToLowerInvariant()}">""";

        var categoryLabels = CalendarStyles.SheetCategories.Select(cat =>
        {
            var isOn = filter.Categories.Contains(cat);
            return $"""
                <label style="display:flex;align-items:center;gap:4px;cursor:pointer;white-space:nowrap">
                    <input type="checkbox" name="categories" value="{cat}"{(isOn ? " checked" : "")}>
                    <span style="display:inline-block;width:9px;height:9px;border-radius:2px;background:{CalendarStyles.CategoryColor(cat)}"></span>
                    {H(CalendarStyles.CategoryLabel(cat))}
                </label>
                """;
        });

        return $"""
            <form method="get" style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:12px;padding:8px 10px;background:#f6f8f9;border:1px solid #e7ecef;border-radius:8px;font-size:13px">
                <input type="hidden" name="filtered" value="1">
                {viewField}
                {anchorField}
                <span style="font-weight:600;color:#90a0ab;font-size:12px;letter-spacing:.05em">SHOW</span>
                {string.Join("", categoryLabels)}
                <label style="display:flex;align-items:center;gap:4px;cursor:pointer;white-space:nowrap">
                    <input type="checkbox" name="showClubEvents" value="1"{(filter.ShowClubEvents ? " checked" : "")}>
                    Club events
                </label>
                <button type="submit" style="margin-left:auto;background:#2d5f8a;color:#fff;border:none;padding:4px 14px;border-radius:6px;font-weight:600;font-size:13px;cursor:pointer">Apply</button>
            </form>
            """;
    }

    private static void AppendLegend(StringBuilder sb)
    {
        sb.Append($"""
            <div style="display:flex;gap:16px;margin-top:12px;font-size:13px;color:#5a7183;flex-wrap:wrap">
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#2d5f8a;vertical-align:-2px"></span> Confirmed</span>
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#eaf1f8;border:1.5px dashed #2d5f8a;vertical-align:-2px"></span> Hold (not yet confirmed)</span>
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#c2622f;border:{CalendarStyles.ClubEventBorderStyle};box-sizing:border-box;vertical-align:-2px"></span> Club event — dotted outline</span>
            </div>
            """);
    }

    // ── Month view ──────────────────────────────────────────────────────────────────────────────

    private static async Task<string> RenderMonthPageAsync(DateTime anchorMonth, DateTime today, FilterState filter, PublicAvailabilityService service, CancellationToken ct)
    {
        var view = ApplyFilter(await service.GetMonthViewAsync(anchorMonth, ct), filter);
        var filterQuery = FilterQuery(filter);
        var sb = new StringBuilder();
        AppendPageOpen(sb);

        sb.Append(NavBar(anchorMonth.ToString("MMMM yyyy"),
            MonthHref(anchorMonth.AddMonths(-1), filterQuery), MonthHref(today, filterQuery), MonthHref(anchorMonth.AddMonths(1), filterQuery),
            ViewMode.Month, anchorMonth, filter));
        sb.Append(AppendCategoryFilterForm(ViewMode.Month, anchorMonth, filter));

        sb.Append("""<div style="display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:4px;font-size:12px">""");
        foreach (var name in new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" })
        {
            sb.Append($"""<div style="text-align:center;color:#90a0ab;font-weight:600;padding:2px 0">{name}</div>""");
        }

        foreach (var cell in MonthCells(anchorMonth))
        {
            AppendDayCell(sb, cell, anchorMonth, view);
        }

        sb.Append("</div>");
        AppendLegend(sb);
        AppendPageClose(sb);
        return sb.ToString();
    }

    private static void AppendDayCell(StringBuilder sb, DateTime cell, DateTime anchorMonth, PublicMonthView view)
    {
        var inMonth = cell.Month == anchorMonth.Month;
        var dayClubEvents = view.ClubEvents.Where(ce => ce.Start.Date <= cell.Date && cell.Date <= ce.End.Date).ToList();
        var dayBookings = view.Bookings
            .Where(b => CalendarStyles.OccursOnDay(b.Start, b.End, cell.Date))
            .Distinct()
            .OrderBy(b => b.Start)
            .ToList();
        var visibleBookingCount = Math.Max(0, 3 - dayClubEvents.Count);

        sb.Append($"""<div class="pub-cal-day" style="border:1px solid #e7ecef;border-radius:6px;min-height:92px;padding:3px 4px;background:{(inMonth ? "#fff" : "#fafbfc")}">""");
        sb.Append($"""<div style="font-size:12px;color:{(inMonth ? "#90a0ab" : "#c1ccd4")};font-weight:600;padding:1px 2px">{cell.Day}</div>""");

        foreach (var ce in dayClubEvents)
        {
            var note = ce.MarksSheetsUnavailable ? "All sheets reserved" : "";
            // ce.Start belongs to the event's actual first day only - on a later day of a multi-day
            // timed club event, prefixing it here would misleadingly read as starting that day too.
            var cellTitleText = ce.IsAllDay || cell.Date != ce.Start.Date ? ce.Title : $"{CalendarStyles.CellStartTimeLabel(ce.Start)} - {ce.Title}";
            var (ceContBefore, ceContAfter) = CalendarStyles.ContinuationMarks(ce.Start, ce.End, cell.Date);
            var cellTitle = $"{(ceContBefore ? "← " : "")}{cellTitleText}{(ceContAfter ? " →" : "")}";
            sb.Append($"""
                <div class="pub-cal-chip" data-title="{H(ce.Title)}" data-subtitle="{H(CalendarStyles.ClubEventCategoryLabel(ce.Category))}" data-time="{H(FormatClubEventRange(ce))}" data-note="{H(note)}"
                     style="background:{CalendarStyles.ClubEventCategoryColor(ce.Category)};color:#fff;border:{CalendarStyles.ClubEventBorderStyle};box-sizing:border-box;border-radius:3px;padding:1.5px 4px;margin-top:2px;font-size:11px;font-weight:600;white-space:nowrap;overflow:hidden;cursor:pointer">{H(cellTitle)}</div>
                """);
        }

        for (var i = 0; i < dayBookings.Count; i++)
        {
            var b = dayBookings[i];
            var category = ParseCategory(b.CategoryLabel);
            var extra = i >= visibleBookingCount;
            var color = CalendarStyles.CategoryColor(category);
            var bg = b.IsConfirmed ? color : CalendarStyles.CategoryLightBg(category);
            var textColor = b.IsConfirmed ? "#fff" : color;
            var border = b.IsConfirmed ? "none" : $"1.5px dashed {color}";
            var subtitle = $"{CalendarStyles.CategoryLabel(category)} · {(b.IsConfirmed ? "Confirmed" : "Hold")}";
            var time = $"{b.Start:dddd, MMM d} · {b.Start:h:mmtt}-{b.End:h:mmtt}";
            var extraClass = extra ? " pub-cal-extra" : "";
            var display = extra ? "none" : "block";

            var (contBefore, contAfter) = CalendarStyles.ContinuationMarks(b.Start, b.End, cell.Date);
            // The start time belongs to the booking's actual first day only - repeating it on every
            // later day reads as a daily recurrence rather than one continuous event.
            var cellTitleText = contBefore ? b.Title : $"{CalendarStyles.CellStartTimeLabel(b.Start)} - {b.Title}";
            var cellTitle = $"{(contBefore ? "← " : "")}{cellTitleText}{(contAfter ? " →" : "")}";
            sb.Append($"""
                <div class="pub-cal-chip{extraClass}" data-title="{H(b.Title)}" data-subtitle="{H(subtitle)}" data-time="{H(time)}" data-note=""
                     style="display:{display};background:{bg};color:{textColor};border:{border};border-radius:3px;padding:1.5px 4px;margin-top:2px;font-size:11px;font-weight:600;white-space:nowrap;overflow:hidden;cursor:pointer">{H(cellTitle)}</div>
                """);
        }

        if (dayBookings.Count > visibleBookingCount)
        {
            sb.Append($"""<div class="pub-cal-more" style="color:#90a0ab;margin-top:2px;padding:0 2px;cursor:pointer;text-decoration:underline">+{dayBookings.Count - visibleBookingCount} more</div>""");
        }

        sb.Append("</div>");
    }

    // ── Week / Day views (hourly grids) ─────────────────────────────────────────────────────────

    private const double HourlyHeaderRowHeightPx = 32;
    private const double AllDayChipHeightPx = 18;
    private const double AllDayChipGapPx = 2;

    private static async Task<string> RenderWeekPageAsync(DateTime anchorDate, DateTime today, FilterState filter, PublicAvailabilityService service, CancellationToken ct)
    {
        var weekStart = anchorDate.AddDays(-(int)anchorDate.DayOfWeek);
        var view = ApplyFilter(await service.GetWeekViewAsync(weekStart, ct), filter);
        var days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToList();
        var filterQuery = FilterQuery(filter);

        var sb = new StringBuilder();
        AppendPageOpen(sb);

        var weekEnd = weekStart.AddDays(6);
        var title = weekStart.Year == weekEnd.Year
            ? $"{weekStart:MMM d} - {weekEnd:MMM d, yyyy}"
            : $"{weekStart:MMM d, yyyy} - {weekEnd:MMM d, yyyy}";
        sb.Append(NavBar(title,
            WeekHref(weekStart.AddDays(-7), filterQuery), WeekHref(today, filterQuery), WeekHref(weekStart.AddDays(7), filterQuery),
            ViewMode.Week, weekStart, filter));
        sb.Append(AppendCategoryFilterForm(ViewMode.Week, weekStart, filter));

        AppendHourlyGrid(sb, days, view, showDayHeaders: true);
        AppendLegend(sb);
        AppendPageClose(sb);
        return sb.ToString();
    }

    private static async Task<string> RenderDayPageAsync(DateTime day, DateTime today, FilterState filter, PublicAvailabilityService service, CancellationToken ct)
    {
        var view = ApplyFilter(await service.GetDayViewAsync(day, ct), filter);
        var filterQuery = FilterQuery(filter);

        var sb = new StringBuilder();
        AppendPageOpen(sb);

        sb.Append(NavBar(day.ToString("dddd, MMMM d, yyyy"),
            DayHref(day.AddDays(-1), filterQuery), DayHref(today, filterQuery), DayHref(day.AddDays(1), filterQuery),
            ViewMode.Day, day, filter));
        sb.Append(AppendCategoryFilterForm(ViewMode.Day, day, filter));

        AppendHourlyGrid(sb, [day], view, showDayHeaders: false);
        AppendLegend(sb);
        AppendPageClose(sb);
        return sb.ToString();
    }

    private static void AppendHourlyGrid(StringBuilder sb, List<DateTime> days, PublicMonthView view, bool showDayHeaders)
    {
        var maxAllDayCount = days.Select(d => AllDayEventsForDay(view, d).Count).DefaultIfEmpty(0).Max();
        var allDayRowHeightPx = maxAllDayCount == 0 ? 0 : maxAllDayCount * AllDayChipHeightPx + Math.Max(0, maxAllDayCount - 1) * AllDayChipGapPx + 4;
        var isMultiDay = days.Count > 1;

        sb.Append("""<div style="border:1px solid #e7ecef;border-radius:8px;padding:10px;background:#fff;overflow-x:auto">""");
        sb.Append("""<div style="display:flex;gap:3px;font-size:12px">""");

        sb.Append("""<div style="width:52px;flex-shrink:0">""");
        if (showDayHeaders)
        {
            sb.Append($"""<div style="height:{HourlyHeaderRowHeightPx}px"></div>""");
        }
        if (allDayRowHeightPx > 0)
        {
            sb.Append($"""<div style="height:{allDayRowHeightPx}px"></div>""");
        }
        foreach (var hour in CalendarStyles.HourRows)
        {
            sb.Append($"""<div style="box-sizing:border-box;height:{CalendarStyles.RowHeightPx}px;color:#90a0ab;font-size:11.5px;text-align:right;padding:2px 8px 0 0;font-weight:600">{CalendarStyles.FormatHour(hour)}</div>""");
            sb.Append($"""<div style="height:{CalendarStyles.RowGapPx}px"></div>""");
        }
        sb.Append("</div>");

        foreach (var day in days)
        {
            AppendDayColumn(sb, day, view, allDayRowHeightPx, showDayHeaders, isMultiDay);
        }

        sb.Append("</div></div>");
    }

    private static void AppendDayColumn(StringBuilder sb, DateTime day, PublicMonthView view, double allDayRowHeightPx, bool showHeader, bool isMultiDay)
    {
        var widthStyle = isMultiDay ? "flex:1;min-width:92px" : "flex:1;min-width:0";
        sb.Append($"""<div style="{widthStyle}">""");

        if (showHeader)
        {
            sb.Append($"""
                <div style="box-sizing:border-box;height:{HourlyHeaderRowHeightPx}px;text-align:center;padding-bottom:3px;border-bottom:1px solid #f2f5f7">
                    <div style="font-weight:600;color:#1e2a33;font-size:13.5px">{day:ddd}</div>
                    <div style="color:#90a0ab;font-size:11.5px">{day:MMM d}</div>
                </div>
                """);
        }

        var allDayEvents = AllDayEventsForDay(view, day);
        if (allDayRowHeightPx > 0)
        {
            sb.Append($"""<div style="display:flex;flex-direction:column;gap:2px;height:{allDayRowHeightPx}px;padding:2px 0;box-sizing:border-box">""");
            foreach (var ce in allDayEvents)
            {
                var text = ce.MarksSheetsUnavailable ? $"{ce.Title} - all sheets" : ce.Title;
                var (ceContBefore, ceContAfter) = CalendarStyles.ContinuationMarks(ce.Start, ce.End, day);
                var chipText = $"{(ceContBefore ? "← " : "")}{text}{(ceContAfter ? " →" : "")}";
                sb.Append($"""
                    <div class="pub-cal-chip" data-title="{H(ce.Title)}" data-subtitle="{H(CalendarStyles.ClubEventCategoryLabel(ce.Category))}" data-time="{H(FormatClubEventRange(ce))}" data-note="{H(ce.MarksSheetsUnavailable ? "All sheets reserved" : "")}"
                         style="background:{CalendarStyles.ClubEventCategoryColor(ce.Category)};color:#fff;border:{CalendarStyles.ClubEventBorderStyle};box-sizing:border-box;border-radius:4px;padding:2px 6px;font-size:11px;font-weight:600;cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{H(chipText)}</div>
                    """);
            }
            sb.Append("</div>");
        }

        sb.Append("""<div style="position:relative">""");
        foreach (var hour in CalendarStyles.HourRows)
        {
            sb.Append($"""<div style="box-sizing:border-box;height:{CalendarStyles.RowHeightPx}px;margin-bottom:{CalendarStyles.RowGapPx}px;border-radius:5px;background:{CalendarStyles.EmptySlotBg};border:1px solid #eef1f3"></div>""");
        }

        var items = TimedItemsForDay(view, day);
        foreach (var laid in CalendarStyles.LayoutLanes(items, i => i.Start, i => i.End))
        {
            var widthPct = 100.0 / laid.LaneCount;
            var leftPct = laid.Lane * widthPct;
            var top = CalendarStyles.TopPx(laid.Item.Start, day);
            var height = CalendarStyles.HeightPx(laid.Item.Start, laid.Item.End, day);

            if (laid.Item.ClubEvent is { } ce)
            {
                var bg = ce.MarksSheetsUnavailable ? "#a02c21" : CalendarStyles.ClubEventCategoryColor(ce.Category);
                // ce.Start belongs to the event's actual first day only - on a later day of a
                // multi-day timed club event, prefixing it here would misleadingly read as starting
                // that day too.
                var (ceContBefore, ceContAfter) = CalendarStyles.ContinuationMarks(ce.Start, ce.End, day);
                var titleText = day.Date == ce.Start.Date ? $"{CalendarStyles.CellStartTimeLabel(ce.Start)} - {ce.Title}" : ce.Title;
                var title = $"{(ceContBefore ? "← " : "")}{titleText}{(ceContAfter ? " →" : "")}";
                sb.Append($"""
                    <div class="pub-cal-chip" data-title="{H(ce.Title)}" data-subtitle="{H(CalendarStyles.ClubEventCategoryLabel(ce.Category))}" data-time="{H(FormatClubEventRange(ce))}" data-note="{H(ce.MarksSheetsUnavailable ? "All sheets reserved" : "")}"
                         style="box-sizing:border-box;position:absolute;top:{top}px;left:{leftPct}%;width:calc({widthPct}% - 2px);height:{height}px;z-index:2;border-radius:5px;background:{bg};border:{CalendarStyles.ClubEventBorderStyle};color:#fff;padding:2px 5px;overflow:hidden;cursor:pointer;font-size:11px;font-weight:600">{H(title)}</div>
                    """);
            }
            else if (laid.Item.Booking is { } b)
            {
                var category = ParseCategory(b.CategoryLabel);
                var color = CalendarStyles.CategoryColor(category);
                var bg = b.IsConfirmed ? color : CalendarStyles.CategoryLightBg(category);
                var textColor = b.IsConfirmed ? "#fff" : color;
                var border = b.IsConfirmed ? $"1.5px solid {color}" : $"1.5px dashed {color}";
                var subtitle = $"{CalendarStyles.CategoryLabel(category)} · {(b.IsConfirmed ? "Confirmed" : "Hold")}";
                var timeText = $"{b.Start:dddd, MMM d} · {b.Start:h:mmtt}-{b.End:h:mmtt}";
                var (contBefore, contAfter) = CalendarStyles.ContinuationMarks(b.Start, b.End, day);
                // The start time belongs to the booking's actual first day only - repeating it on every
                // later day reads as a daily recurrence rather than one continuous event.
                var titleText = contBefore ? b.Title : $"{CalendarStyles.CellStartTimeLabel(laid.Item.Start)} - {b.Title}";
                var title = $"{(contBefore ? "← " : "")}{titleText}{(contAfter ? " →" : "")}";
                sb.Append($"""
                    <div class="pub-cal-chip" data-title="{H(b.Title)}" data-subtitle="{H(subtitle)}" data-time="{H(timeText)}" data-note=""
                         style="box-sizing:border-box;position:absolute;top:{top}px;left:{leftPct}%;width:calc({widthPct}% - 2px);height:{height}px;z-index:1;border-radius:5px;background:{bg};border:{border};color:{textColor};padding:2px 5px;overflow:hidden;cursor:pointer;font-size:11px;font-weight:600">{H(title)}</div>
                    """);
            }
        }

        sb.Append("</div>"); // position:relative
        sb.Append("</div>"); // column
    }

    private sealed record DayItem(DateTime Start, DateTime End, PublicClubEventLabel? ClubEvent, PublicMonthBooking? Booking);

    private static List<PublicClubEventLabel> AllDayEventsForDay(PublicMonthView view, DateTime day) =>
        view.ClubEvents.Where(ce => ce.IsAllDay && ce.Start.Date <= day.Date && day.Date <= ce.End.Date)
            .OrderBy(ce => ce.Title)
            .ToList();

    // Timed club events and bookings are peers competing for the same hourly timeline and lane
    // space (CalendarStyles.LayoutLanes), same as the staff Week grid. Bookings are de-duplicated
    // via record equality (.Distinct()) - the same trick the Month view already relies on to
    // collapse an identical multi-sheet booking (same title/time/category/state on every sheet)
    // into one displayed item, since the public DTOs never carry sheet/group identity to dedupe by
    // more precisely.
    private static List<DayItem> TimedItemsForDay(PublicMonthView view, DateTime day)
    {
        var timedClubEvents = view.ClubEvents
            .Where(ce => !ce.IsAllDay && ce.Start.Date <= day.Date && day.Date <= ce.End.Date)
            .Select(ce => new DayItem(ce.Start, ce.End, ce, null));

        var bookings = view.Bookings
            .Where(b => CalendarStyles.OccursOnDay(b.Start, b.End, day.Date))
            .Distinct()
            .Select(b => new DayItem(b.Start, b.End, null, b));

        return timedClubEvents.Concat(bookings).OrderBy(i => i.Start).ToList();
    }

    // ── Shared overlay + loading-indicator script ───────────────────────────────────────────────

    private const string OverlayAndScript = """
        <div id="pub-cal-loading" style="display:none;position:fixed;inset:0;background:rgba(255,255,255,.7);align-items:center;justify-content:center;z-index:100">
            <div style="background:#1e2a33;color:#fff;padding:10px 20px;border-radius:8px;font-size:12px;font-weight:600">Loading…</div>
        </div>
        <div id="pub-cal-overlay" style="display:none;position:fixed;inset:0;background:rgba(30,42,51,.45);align-items:center;justify-content:center;z-index:50">
            <div style="background:#fff;border-radius:10px;box-shadow:0 12px 32px rgba(0,0,0,.35);padding:18px 20px;width:340px">
                <div id="pub-cal-overlay-title" style="font-size:15px;font-weight:600;color:#1e2a33;margin-bottom:2px"></div>
                <div id="pub-cal-overlay-subtitle" style="font-size:13px;color:#90a0ab;margin-bottom:10px"></div>
                <div id="pub-cal-overlay-time" style="font-size:12px;color:#1e2a33"></div>
                <div id="pub-cal-overlay-note" style="font-size:13px;color:#a02c21;font-weight:600;margin-top:6px"></div>
                <div style="text-align:center;margin-top:14px">
                    <span id="pub-cal-overlay-close" style="border:1px solid #d7dfe5;color:#5a7183;border-radius:6px;padding:7px 20px;cursor:pointer;font-size:12px;font-weight:600">Close</span>
                </div>
            </div>
        </div>
        <script>
            (function () {
                var overlay = document.getElementById('pub-cal-overlay');
                var titleEl = document.getElementById('pub-cal-overlay-title');
                var subtitleEl = document.getElementById('pub-cal-overlay-subtitle');
                var timeEl = document.getElementById('pub-cal-overlay-time');
                var noteEl = document.getElementById('pub-cal-overlay-note');

                function openOverlay(chip) {
                    titleEl.textContent = chip.getAttribute('data-title') || '';
                    subtitleEl.textContent = chip.getAttribute('data-subtitle') || '';
                    timeEl.textContent = chip.getAttribute('data-time') || '';
                    noteEl.textContent = chip.getAttribute('data-note') || '';
                    overlay.style.display = 'flex';
                }

                document.querySelectorAll('.pub-cal-chip').forEach(function (chip) {
                    chip.addEventListener('click', function () { openOverlay(chip); });
                });

                document.querySelectorAll('.pub-cal-more').forEach(function (moreEl) {
                    moreEl.addEventListener('click', function () {
                        var day = moreEl.closest('.pub-cal-day');
                        day.querySelectorAll('.pub-cal-extra').forEach(function (el) { el.style.display = 'block'; });
                        moreEl.style.display = 'none';
                    });
                });

                document.getElementById('pub-cal-overlay-close').addEventListener('click', function () {
                    overlay.style.display = 'none';
                });
                overlay.addEventListener('click', function (e) {
                    if (e.target === overlay) {
                        overlay.style.display = 'none';
                    }
                });

                // Date-range/view navigation is a full page reload (this endpoint hand-builds HTML
                // server-side, no client-side routing) - the browser gives no visible feedback of
                // its own while the server computes the next view (a Graph fan-out on a cache miss
                // can take a noticeable moment), so the page just sits unchanged until it's replaced.
                // Show an explicit loading state the instant a nav link is clicked; navigation then
                // proceeds normally and the whole DOM (including this overlay) is replaced anyway.
                var loading = document.getElementById('pub-cal-loading');
                document.querySelectorAll('.pub-cal-nav-link').forEach(function (link) {
                    link.addEventListener('click', function () {
                        loading.style.display = 'flex';
                    });
                });
            })();
        </script>
        """;
}
