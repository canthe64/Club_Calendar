using System.Net;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// A public, anonymous search page - "find me a window where at least N sheets are open for a
/// group event" - delivering the R6 "≥N sheets available" view the architecture doc originally
/// marked backlogged (deprioritized during the initial build), now built as its own dedicated page
/// rather than folded into the public calendar, per raised feedback. Same architectural rule as
/// every other anonymous surface (architecture doc D15): a plain Minimal API endpoint, hand-building
/// its own HTML, never a Blazor component sharing the staff app's authenticated circuit.
/// </summary>
public static class PublicSearchEndpoint
{
    private const int MaxRangeDays = 60;

    public static void MapPublicSearchEndpoint(this WebApplication app)
    {
        app.MapGet("/public/search", async (string? start, string? end, int? sheets, PublicAvailabilityService service, FacilityConfiguration facility, CancellationToken ct) =>
        {
            var maxSheets = Math.Max(1, facility.SheetMailboxes.Length);
            var (rangeStart, rangeEnd) = ParseRange(start, end, facility.Today);
            var minSheets = Math.Clamp(sheets ?? 1, 1, maxSheets);

            // rangeEnd is the last INCLUDED day for display/form purposes; the actual data fetch
            // needs the exclusive boundary one day past it.
            var windows = await service.GetConcurrentAvailabilityAsync(rangeStart, rangeEnd.AddDays(1), minSheets, ct);

            var html = RenderPage(rangeStart, rangeEnd, minSheets, maxSheets, windows);
            return Results.Content(html, "text/html; charset=utf-8");
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    // Same clamping rationale as PublicCalendarEndpoint's ?month=/?date= - bounds the anonymous
    // cache-key/Graph-fanout surface. The 60-day cap on the span itself is the search's own
    // requirement (a search this wide across every sheet is meaningfully more expensive per request
    // than a single month/week/day view).
    internal static (DateTime Start, DateTime End) ParseRange(string? start, string? end, DateTime today)
    {
        var minAllowed = today.AddYears(-1);
        var maxAllowed = today.AddYears(2);

        var parsedStart = DateTime.TryParse(start, out var s) ? s.Date : today;
        parsedStart = parsedStart < minAllowed ? minAllowed : parsedStart > maxAllowed ? maxAllowed : parsedStart;

        var parsedEnd = DateTime.TryParse(end, out var e) ? e.Date : parsedStart.AddDays(7);
        var maxSpanEnd = parsedStart.AddDays(MaxRangeDays);
        parsedEnd = parsedEnd < parsedStart ? parsedStart : parsedEnd > maxSpanEnd ? maxSpanEnd : parsedEnd;
        parsedEnd = parsedEnd > maxAllowed ? maxAllowed : parsedEnd;

        return (parsedStart, parsedEnd);
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    // internal, not private - reached directly by PublicSearchEndpointTests (D60's precedent), so
    // this hand-built page's copy is testable without a full ASP.NET Core host.
    internal static string RenderPage(DateTime start, DateTime end, int minSheets, int maxSheets, List<PublicAvailabilityWindow> windows)
    {
        var sb = new StringBuilder();

        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Search Available Ice</title>
            <link rel="icon" type="image/svg+xml" href="/favicon.svg">
            </head>
            <body style="font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:0;color:#1e2a33">
            <header style="background:#1e2a33;color:#fff;padding:10px 24px;font-weight:600;font-size:14px;display:flex;align-items:center;gap:16px">
                <span>GCC Ice &amp; Event Calendar</span>
                <a href="/public/calendar" style="color:#a9c7e4;font-size:12px;font-weight:600;text-decoration:none;margin-left:auto">&#8249; Back to calendar</a>
            </header>
            <div style="padding:16px 24px;max-width:800px">
            """);

        sb.Append($"""
            <div style="font-size:16px;font-weight:600;color:#1e2a33;margin-bottom:4px">Search Available Ice</div>
            <div style="font-size:13px;color:#90a0ab;margin-bottom:16px">Find dates and times with enough open sheets for a group event.</div>

            <form method="get" style="display:flex;gap:12px;align-items:end;flex-wrap:wrap;background:#f6f8f9;border:1px solid #e7ecef;border-radius:8px;padding:14px;margin-bottom:20px;font-size:12px">
                <div>
                    <div style="font-weight:600;color:#1e2a33;margin-bottom:4px">From</div>
                    <input type="date" name="start" value="{start:yyyy-MM-dd}" style="border:1px solid #d7dfe5;border-radius:6px;padding:6px 9px;font-size:12px">
                </div>
                <div>
                    <div style="font-weight:600;color:#1e2a33;margin-bottom:4px">To <span style="font-weight:400;color:#90a0ab">(within {MaxRangeDays} days of From)</span></div>
                    <input type="date" name="end" value="{end:yyyy-MM-dd}" style="border:1px solid #d7dfe5;border-radius:6px;padding:6px 9px;font-size:12px">
                </div>
                <div>
                    <div style="font-weight:600;color:#1e2a33;margin-bottom:4px">Minimum sheets needed</div>
                    <select name="sheets" style="border:1px solid #d7dfe5;border-radius:6px;padding:6px 9px;font-size:12px">
                        {SheetOptions(minSheets, maxSheets)}
                    </select>
                </div>
                <button type="submit" style="background:#c62f2f;color:#fff;border:none;padding:8px 18px;border-radius:6px;font-weight:600;font-size:12px;cursor:pointer">Search</button>
            </form>

            <div style="font-size:12.5px;color:#5a7183;margin-bottom:16px">
                To confirm availability and inquire about a group event, visit
                <a href="https://curlingseattle.org/group-events" target="_blank" rel="noopener" style="color:#2d5f8a;font-weight:600">curlingseattle.org/group-events</a>.
            </div>
            """);

        sb.Append(RenderResults(start, end, minSheets, windows));

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string SheetOptions(int minSheets, int maxSheets)
    {
        var sb = new StringBuilder();
        for (var n = 1; n <= maxSheets; n++)
        {
            var selected = n == minSheets ? " selected" : "";
            var label = n == 1 ? "1 sheet" : $"{n} sheets";
            sb.Append($"""<option value="{n}"{selected}>{label}</option>""");
        }
        return sb.ToString();
    }

    private static string RenderResults(DateTime start, DateTime end, int minSheets, List<PublicAvailabilityWindow> windows)
    {
        var sheetWord = minSheets == 1 ? "sheet" : "sheets";

        if (windows.Count == 0)
        {
            return $"""
                <div style="font-size:12px;color:#90a0ab;border:1px dashed #d7dfe5;border-radius:8px;padding:20px;text-align:center">
                    No windows found with at least {minSheets} {H(sheetWord)} available between {H(start.ToString("MMM d"))} and {H(end.ToString("MMM d, yyyy"))}.
                </div>
                """;
        }

        var sb = new StringBuilder();
        sb.Append($"""
            <div style="font-size:13px;color:#5a7183;margin-bottom:8px;font-weight:600">{windows.Count} window{(windows.Count == 1 ? "" : "s")} found with at least {minSheets} {H(sheetWord)} available:</div>
            <div style="display:flex;flex-direction:column;gap:6px">
            """);

        foreach (var w in windows)
        {
            var dayLink = $"/public/calendar?view=day&date={w.Start:yyyy-MM-dd}";
            var sameDay = w.Start.Date == w.End.Date;
            var timeText = sameDay
                ? $"{w.Start:dddd, MMM d} · {w.Start:h:mmtt}-{w.End:h:mmtt}"
                : $"{w.Start:MMM d, h:mmtt} - {w.End:MMM d, h:mmtt}";

            sb.Append($"""
                <a href="{dayLink}" style="display:block;border:1px solid #e7ecef;border-radius:6px;padding:10px 14px;font-size:12px;color:#1e2a33;text-decoration:none;background:#fff">{H(timeText)}</a>
                """);
        }

        sb.Append("</div>");
        return sb.ToString();
    }
}
