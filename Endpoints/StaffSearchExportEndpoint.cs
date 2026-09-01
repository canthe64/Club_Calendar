using FacilityScheduler.Domain.Search;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// CSV export of the staff event search - a plain Minimal API endpoint, not a Blazor page, for the
/// same reason as <c>SettingsLogsEndpoint</c>'s log download: a file download is a real HTTP
/// response, and the interactive-server circuit is the wrong place for one. Staff-only, explicitly
/// bound rather than relying on the global fallback policy - same belt-and-suspenders reasoning
/// <c>SettingsLogsEndpoint</c> already uses (architecture doc §8/D74 records that how ASP.NET Core
/// resolves FallbackPolicy against a Minimal API endpoint's own metadata has never been confirmed
/// against a real non-staff sign-in).
///
/// Stateless by design: it re-parses the query and re-runs the exact same fetch/match/group/sort
/// path <c>EventSearch.razor</c> uses (<see cref="SearchResultsBuilder"/>), rather than trying to
/// snapshot whatever the circuit currently has in memory. That's what makes the URL shareable and
/// keeps this endpoint from silently disagreeing with the matcher if either one ever changes without
/// the other. It hits <c>SheetBookingService</c>'s 30-second view cache, so exporting immediately
/// after running the same search on screen costs no extra Graph calls.
///
/// Deliberately exports every match, with no cap - <c>EventSearch.MaxRenderedRows</c> is a render-
/// cost limit for the live page, not a real result limit, and a silently truncated export is worse
/// than a silently truncated screen: nothing on paper says a document was cut short.
/// </summary>
public static class StaffSearchExportEndpoint
{
    public static void MapStaffSearchExportEndpoint(this WebApplication app)
    {
        app.MapGet("/search/export.csv", async (
            string? q,
            string? start,
            string? end,
            SheetBookingService bookingService,
            ClubEventService clubEventService,
            FacilityConfiguration facility,
            CancellationToken ct) =>
        {
            var query = SearchQueryParser.Parse(q);
            if (query.IsEmpty)
            {
                return Results.BadRequest("No search terms were given - add a search term before exporting.");
            }

            var today = facility.Today;
            var (rangeStart, rangeEnd, _) = SearchRange.Resolve(ParseDate(start), ParseDate(end), today);

            // rangeEnd is the last INCLUDED day; the fetch needs the exclusive upper bound - identical
            // to EventSearch.razor's own RunSearch, so the two can never disagree about what "the last
            // day" means.
            var fetchEnd = rangeEnd.AddDays(1);
            var bookings = await bookingService.GetBookingsForAllSheetsAsync(rangeStart, fetchEnd, ct);
            var clubEvents = await clubEventService.GetEventsAsync(rangeStart, fetchEnd, ct);

            var result = SearchResultsBuilder.Build(bookings, clubEvents, query, today);
            var csvBytes = SearchResultsCsv.Build(result, bookings);

            return Results.File(csvBytes, SearchResultsCsv.ContentType, SearchResultsCsv.FileNameFor(today));
        })
        .RequireAuthorization(StaffAuthorizationPolicies.StaffOnly);
    }

    internal static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var d) ? d : null;
}
