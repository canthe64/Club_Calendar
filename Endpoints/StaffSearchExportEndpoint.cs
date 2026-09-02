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
///
/// <c>season=1</c> (added 2026-09-02, D111) mirrors the search page's own "Search entire season"
/// checkbox: it ignores <c>start</c>/<c>end</c> entirely and re-resolves the operator's currently
/// configured season live via <see cref="SchedulingWindowService"/>, rather than trusting whatever
/// dates the page happened to pass - the season could have changed (or been cleared) between the
/// on-screen search and the export click, and re-reading it here is what keeps this endpoint's
/// existing "stateless, re-derives everything itself" design honest for this path too.
///
/// <c>season</c> is bound as <c>string?</c>, not <c>bool?</c> - a live-found bug (2026-09-03): Minimal
/// API's built-in binding for a genuinely <c>bool</c>-typed parameter parses via
/// <see cref="bool.TryParse(string?, out bool)"/>, which accepts only "true"/"false", not "1" - so
/// <c>?season=1</c> (what <c>EventSearch.razor</c>'s ExportUrl actually sends) failed to bind, and a
/// binding failure on a value that WAS present returns <c>400 Bad Request</c> with an empty body
/// regardless of the parameter's nullability. `UseStatusCodePagesWithReExecute` (Program.cs) then
/// re-executes any empty-body 4xx/5xx response to `/not-found`, so what actually reached the browser
/// was NotFound.razor's "the content you are looking for does not exist" - indistinguishable from a
/// real 404 unless you're looking at the actual response status code. `string?` plus a presence check
/// (<see cref="ParseSeason"/>) is this codebase's own established pattern for exactly this shape of
/// query flag - see `PublicCalendarEndpoint.ParseFilter`'s `filtered`/`showClubEvents` parameters -
/// specifically because it sidesteps framework `TryParse` conventions a hidden-form-field value like
/// "1" was never guaranteed to satisfy.
/// </summary>
public static class StaffSearchExportEndpoint
{
    public static void MapStaffSearchExportEndpoint(this WebApplication app)
    {
        app.MapGet("/search/export.csv", async (
            string? q,
            string? start,
            string? end,
            string? season,
            SheetBookingService bookingService,
            ClubEventService clubEventService,
            FacilityConfiguration facility,
            SchedulingWindowService window,
            CancellationToken ct) =>
        {
            var query = SearchQueryParser.Parse(q);
            if (query.IsEmpty)
            {
                return Results.BadRequest("No search terms were given - add a search term before exporting.");
            }

            var today = facility.Today;
            var resolved = ResolveExportRange(ParseSeason(season), ParseDate(start), ParseDate(end), window.SeasonStartDate, window.SeasonEndDate, today);
            if (resolved.Error is not null)
            {
                return Results.BadRequest(resolved.Error);
            }

            // rangeEnd is the last INCLUDED day; the fetch needs the exclusive upper bound - identical
            // to EventSearch.razor's own RunSearch, so the two can never disagree about what "the last
            // day" means.
            var fetchEnd = resolved.End.AddDays(1);
            var bookings = await bookingService.GetBookingsForAllSheetsAsync(resolved.Start, fetchEnd, ct);
            var clubEvents = await clubEventService.GetEventsAsync(resolved.Start, fetchEnd, ct);

            var result = SearchResultsBuilder.Build(bookings, clubEvents, query, today);
            var csvBytes = SearchResultsCsv.Build(result, bookings);

            return Results.File(csvBytes, SearchResultsCsv.ContentType, SearchResultsCsv.FileNameFor(today));
        })
        .RequireAuthorization(StaffAuthorizationPolicies.StaffOnly);
    }

    internal static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var d) ? d : null;

    // Presence-based, not a "true"/"false"/"1" value comparison - matches ParseFilter's own
    // filtered/showClubEvents convention (PublicCalendarEndpoint) and, more importantly, tolerates
    // whatever literal string a caller sends ("1", "true", "yes") rather than silently requiring one
    // specific spelling the way bool-typed binding did.
    internal static bool ParseSeason(string? value) => !string.IsNullOrEmpty(value);

    /// <summary>Picks and runs the right <c>SearchRange</c> resolution for this request - factored out
    /// as a pure function (D60 precedent, same as <see cref="ParseDate"/>) so the season-vs-explicit-
    /// range branching is directly testable without a full ASP.NET Core host. <c>Error</c> non-null
    /// means season export was requested but no season (or only half of one) is configured; the caller
    /// returns it as a 400 rather than silently falling back to some other range.</summary>
    internal static (DateTime Start, DateTime End, string? Warning, string? Error) ResolveExportRange(
        bool season, DateTime? start, DateTime? end, DateTime? seasonStart, DateTime? seasonEnd, DateTime today)
    {
        if (season)
        {
            if (seasonStart is null || seasonEnd is null)
            {
                return (default, default, null, "No booking season is configured - set both a start and end date on the Settings page before exporting the entire season.");
            }

            var (seasonRangeStart, seasonRangeEnd, seasonWarning) = SearchRange.ResolveSeason(seasonStart.Value, seasonEnd.Value, today);
            return (seasonRangeStart, seasonRangeEnd, seasonWarning, null);
        }

        var (rangeStart, rangeEnd, warning) = SearchRange.Resolve(start, end, today);
        return (rangeStart, rangeEnd, warning, null);
    }
}
