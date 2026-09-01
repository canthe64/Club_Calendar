namespace FacilityScheduler.Domain.Search;

/// <summary>
/// The one place a parsed <see cref="SearchQuery"/> turns into the rows a caller actually shows -
/// matching, deduplicating a multi-sheet booking down to one row, and splitting into
/// upcoming/past. Extracted out of <c>EventSearch.razor</c> (originally its own private
/// <c>ApplyResults</c>) when the CSV export needed the identical result set: without this, the
/// screen and the export would each carry their own copy of the match/group/sort logic, and the
/// two would silently drift the first time either one changed - exactly the class of bug this
/// codebase's own doc comments repeatedly call out (see BookingDraft/ClubEventDraft's shared
/// Minutes-relative-to-own-Date convention for a real instance of it).
///
/// Deliberately does NOT apply any render/row cap - that's a presentation concern
/// (<c>EventSearch.MaxRenderedRows</c>), not a search-result concern, and the CSV export in
/// particular must not silently truncate a document nothing on paper says was cut short.
/// </summary>
internal static class SearchResultsBuilder
{
    internal sealed record ResultRow(DateTime Start, DateTime End, SheetBooking? Booking, ClubEvent? ClubEvent);

    internal readonly record struct Result(
        List<ResultRow> Upcoming,
        List<ResultRow> Past,
        int OnIceMatchCount,
        int OffIceMatchCount);

    internal static Result Build(IEnumerable<SheetBooking> allBookings, IEnumerable<ClubEvent> allClubEvents, SearchQuery query, DateTime today)
    {
        var bookingRows = allBookings
            .Where(b => EventSearchMatcher.Matches(b, query))
            .GroupBy(CalendarStyles.BookingGroupKey)
            .Select(g => g.First())
            .Select(b => new ResultRow(b.Start, b.End, b, null));

        var clubEventRows = allClubEvents
            .Where(ce => EventSearchMatcher.Matches(ce, query))
            .Select(ce => new ResultRow(ce.Start, ce.End, null, ce));

        var allRows = bookingRows.Concat(clubEventRows).ToList();
        var onIceCount = allRows.Count(r => r.Booking is not null);
        var offIceCount = allRows.Count(r => r.ClubEvent is not null);

        var upcoming = allRows.Where(r => r.End.Date >= today.Date)
            .OrderBy(r => r.Start).ThenBy(RowTitle).ThenBy(RowSheetKey)
            .ToList();
        var past = allRows.Where(r => r.End.Date < today.Date)
            .OrderByDescending(r => r.Start).ThenBy(RowTitle).ThenBy(RowSheetKey)
            .ToList();

        return new Result(upcoming, past, onIceCount, offIceCount);
    }

    private static string RowTitle(ResultRow r) =>
        r.Booking is { } b ? CalendarStyles.BookingDisplayTitle(b) : r.ClubEvent!.Title;

    private static string RowSheetKey(ResultRow r) => r.Booking?.SheetMailbox ?? string.Empty;
}
