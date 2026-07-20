using System.Net;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The public month calendar - deliberately NOT a Blazor component. Blazor Server pages are served
/// through a shared host shell (App.razor) that unconditionally loads blazor.web.js and the
/// reconnect/error-UI infrastructure, regardless of whether the specific page declares any
/// interactivity - for an anonymous visitor, that shared runtime's own background circuit activity
/// gets rejected and surfaces as a visible "unhandled error" banner, with no clean way to opt one
/// page out of it (confirmed via live testing - removing render mode from the page itself, and then
/// from HeadOutlet too, didn't stop it). This endpoint sidesteps that whole category of problem by
/// hand-building the HTML response directly, the same way PublicAvailabilityEndpoints already does
/// for the JSON API - no circuit, no Blazor client runtime, nothing to reject.
/// </summary>
public static class PublicCalendarEndpoint
{
    public static void MapPublicCalendarEndpoint(this WebApplication app)
    {
        app.MapGet("/public/calendar", async (string? month, PublicAvailabilityService service, CancellationToken ct) =>
        {
            var anchorMonth = ParseMonth(month) ?? DateTime.UtcNow.Date;
            var view = await service.GetMonthViewAsync(anchorMonth, ct);
            return Results.Content(RenderPage(anchorMonth, view), "text/html; charset=utf-8");
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    // The month is clamped to a window around today, not accepted verbatim: DateTime.TryParse
    // allows years 0001-9999, i.e. ~120k distinct values - and every previously-unseen month is a
    // cache miss in GetMonthViewAsync that fans out live Graph calls across every mailbox and adds
    // a new never-evicted cache entry. Unclamped, an anonymous client iterating months could burn
    // Graph quota and grow the cache without bound. A year back and two years forward covers every
    // legitimate use (members browsing the season) while capping the anonymous surface at ~37 keys.
    private static DateTime? ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month) || !DateTime.TryParse(month + "-01", out var parsed))
        {
            return null;
        }

        var min = new DateTime(DateTime.UtcNow.Year - 1, DateTime.UtcNow.Month, 1);
        var max = new DateTime(DateTime.UtcNow.Year + 2, DateTime.UtcNow.Month, 1);
        return parsed < min ? min : parsed > max ? max : parsed;
    }

    private static string Query(DateTime month) => month.ToString("yyyy-MM");

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

    private static string RenderPage(DateTime anchorMonth, PublicMonthView view)
    {
        var sb = new StringBuilder();

        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Club Calendar</title>
            </head>
            <body style="font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:0;color:#1e2a33">
            <header style="background:#1e2a33;color:#fff;padding:10px 24px;font-weight:600;font-size:14px">Facility Scheduler</header>
            <div style="padding:16px 24px;max-width:1100px">
            """);

        sb.Append($"""
            <div style="display:flex;align-items:center;gap:14px;margin-bottom:6px">
                <span style="font-size:16px;font-weight:600;color:#1e2a33">{H(anchorMonth.ToString("MMMM yyyy"))}</span>
                <span style="display:flex;align-items:center;gap:8px;font-size:12px">
                    <a href="/public/calendar?month={Query(anchorMonth.AddMonths(-1))}" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">&#8249;</a>
                    <a href="/public/calendar?month={Query(DateTime.UtcNow.Date)}" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">Today</a>
                    <a href="/public/calendar?month={Query(anchorMonth.AddMonths(1))}" style="color:#2d5f8a;font-weight:600;padding:0 4px;text-decoration:none">&#8250;</a>
                </span>
            </div>
            <div style="font-size:11px;color:#90a0ab;margin-bottom:12px">What's on the ice, at a glance - tap an entry to see its time.</div>
            """);

        sb.Append("""<div style="display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:4px;font-size:10px">""");
        foreach (var name in new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" })
        {
            sb.Append($"""<div style="text-align:center;color:#90a0ab;font-weight:600;padding:2px 0">{name}</div>""");
        }

        foreach (var cell in MonthCells(anchorMonth))
        {
            AppendDayCell(sb, cell, anchorMonth, view);
        }

        sb.Append("</div>");

        sb.Append($"""
            <div style="display:flex;gap:16px;margin-top:12px;font-size:11px;color:#5a7183;flex-wrap:wrap">
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#2d5f8a;vertical-align:-2px"></span> Confirmed</span>
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#eaf1f8;border:1.5px dashed #2d5f8a;vertical-align:-2px"></span> Hold (not yet confirmed)</span>
                <span><span style="display:inline-block;width:11px;height:11px;border-radius:2px;background:#c2622f;border:{CalendarStyles.ClubEventBorderStyle};box-sizing:border-box;vertical-align:-2px"></span> Club event — dotted outline</span>
            </div>
            </div>
            """);

        sb.Append(OverlayAndScript);
        sb.Append("</body></html>");

        return sb.ToString();
    }

    private static void AppendDayCell(StringBuilder sb, DateTime cell, DateTime anchorMonth, PublicMonthView view)
    {
        var inMonth = cell.Month == anchorMonth.Month;
        var dayClubEvents = view.ClubEvents.Where(ce => ce.Start.Date <= cell.Date && cell.Date <= ce.End.Date).ToList();
        var dayBookings = view.Bookings
            .Where(b => b.Start.Date == cell.Date)
            .Distinct()
            .OrderBy(b => b.Start)
            .ToList();
        var visibleBookingCount = Math.Max(0, 3 - dayClubEvents.Count);

        sb.Append($"""<div class="pub-cal-day" style="border:1px solid #e7ecef;border-radius:6px;min-height:92px;padding:3px 4px;background:{(inMonth ? "#fff" : "#fafbfc")}">""");
        sb.Append($"""<div style="font-size:10px;color:{(inMonth ? "#90a0ab" : "#c1ccd4")};font-weight:600;padding:1px 2px">{cell.Day}</div>""");

        foreach (var ce in dayClubEvents)
        {
            var note = ce.MarksSheetsUnavailable ? "All sheets reserved" : "";
            var cellTitle = ce.IsAllDay ? ce.Title : $"{CalendarStyles.CellStartTimeLabel(ce.Start)} - {ce.Title}";
            sb.Append($"""
                <div class="pub-cal-chip" data-title="{H(ce.Title)}" data-subtitle="{H(ce.Category.ToString())}" data-time="{H(FormatClubEventRange(ce))}" data-note="{H(note)}"
                     style="background:{CalendarStyles.ClubEventCategoryColor(ce.Category)};color:#fff;border:{CalendarStyles.ClubEventBorderStyle};box-sizing:border-box;border-radius:3px;padding:1.5px 4px;margin-top:2px;font-size:9px;font-weight:600;white-space:nowrap;overflow:hidden;cursor:pointer">{H(cellTitle)}</div>
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

            var cellTitle = $"{CalendarStyles.CellStartTimeLabel(b.Start)} - {b.Title}";
            sb.Append($"""
                <div class="pub-cal-chip{extraClass}" data-title="{H(b.Title)}" data-subtitle="{H(subtitle)}" data-time="{H(time)}" data-note=""
                     style="display:{display};background:{bg};color:{textColor};border:{border};border-radius:3px;padding:1.5px 4px;margin-top:2px;font-size:9px;font-weight:600;white-space:nowrap;overflow:hidden;cursor:pointer">{H(cellTitle)}</div>
                """);
        }

        if (dayBookings.Count > visibleBookingCount)
        {
            sb.Append($"""<div class="pub-cal-more" style="color:#90a0ab;margin-top:2px;padding:0 2px;cursor:pointer;text-decoration:underline">+{dayBookings.Count - visibleBookingCount} more</div>""");
        }

        sb.Append("</div>");
    }

    private const string OverlayAndScript = """
        <div id="pub-cal-overlay" style="display:none;position:fixed;inset:0;background:rgba(30,42,51,.45);align-items:center;justify-content:center;z-index:50">
            <div style="background:#fff;border-radius:10px;box-shadow:0 12px 32px rgba(0,0,0,.35);padding:18px 20px;width:340px">
                <div id="pub-cal-overlay-title" style="font-size:15px;font-weight:600;color:#1e2a33;margin-bottom:2px"></div>
                <div id="pub-cal-overlay-subtitle" style="font-size:11px;color:#90a0ab;margin-bottom:10px"></div>
                <div id="pub-cal-overlay-time" style="font-size:12px;color:#1e2a33"></div>
                <div id="pub-cal-overlay-note" style="font-size:11px;color:#a02c21;font-weight:600;margin-top:6px"></div>
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
            })();
        </script>
        """;
}
