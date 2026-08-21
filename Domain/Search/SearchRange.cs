namespace FacilityScheduler.Domain.Search;

/// <summary>
/// Resolves the two `&lt;input type="date"&gt;` values on the search page into the actual window to
/// fetch from Graph - defaulting, bounding, and span-capping, with <paramref name="today"/> injected
/// so this stays a pure function (mirrors <c>PublicSearchEndpoint.ParseRange</c>).
///
/// The clamp applies only to the fetch, never to the input controls themselves - the standing
/// no-silent-date-mutation rule. Whenever the actual searched window differs from what was typed,
/// <c>Warning</c> is non-null and names both the requested and the actual dates; the page's job is
/// to show that banner while leaving the date inputs displaying exactly what the staff member typed.
///
/// One range applies to both bookings and Club Events - deliberately not decoupled even though
/// bookings are the expensive half (Club Events have no recurring-series expansion cost regardless
/// of range width). Two different "how far was actually searched" answers for one search box would
/// be a confusing result to explain, not a real usability win.
///
/// <see cref="MaxSpanDays"/> matches <c>PublicSearchEndpoint.MaxRangeDays</c>'s existing 60-day cap -
/// live-tested 2026-08-21 against the real tenant: <c>calendarView</c>'s cost with recurring series
/// scales with the width of the requested range itself (Graph expands every occurrence across the
/// whole window on every call), not just with page/round-trip count - a 396-day default that this
/// class originally used took 90+ seconds and wasn't a request-count problem `$top` could fix. 60
/// days was already the established safe width for exactly this kind of wide sheet-booking read.
/// </summary>
internal static class SearchRange
{
    internal const int DefaultLookbackDays = 14;
    internal const int DefaultLookaheadDays = 46;
    internal const int MaxSpanDays = 60;

    public static (DateTime Start, DateTime End, string? Warning) Resolve(DateTime? start, DateTime? end, DateTime today)
    {
        var requestedStart = start?.Date ?? today.AddDays(-DefaultLookbackDays);
        var requestedEnd = end?.Date ?? today.AddDays(DefaultLookaheadDays);

        var minAllowed = today.AddYears(-1);
        var maxAllowed = today.AddYears(2);

        var boundedStart = Clamp(requestedStart, minAllowed, maxAllowed);
        var boundedEnd = Clamp(requestedEnd, minAllowed, maxAllowed);

        // Warned about, never silently swapped - reordering Start/End would search a window the
        // staff member never asked for and never typed.
        if (boundedEnd < boundedStart)
        {
            var warning = $"Your end date ({FormatDate(requestedEnd)}) is before your start date ({FormatDate(requestedStart)}), so only {FormatDate(boundedStart)} was searched.";
            return (boundedStart, boundedStart, warning);
        }

        var maxSpanEnd = boundedStart.AddDays(MaxSpanDays);
        if (boundedEnd > maxSpanEnd)
        {
            var warning = $"Searched {FormatDate(boundedStart)} - {FormatDate(maxSpanEnd)}. That's the {MaxSpanDays}-day maximum; your end date of {FormatDate(requestedEnd)} wasn't reached.";
            return (boundedStart, maxSpanEnd, warning);
        }

        if (boundedStart != requestedStart || boundedEnd != requestedEnd)
        {
            var warning = $"Searched {FormatDate(boundedStart)} - {FormatDate(boundedEnd)}. Dates are limited to {FormatDate(minAllowed)} through {FormatDate(maxAllowed)}.";
            return (boundedStart, boundedEnd, warning);
        }

        return (boundedStart, boundedEnd, null);
    }

    private static DateTime Clamp(DateTime value, DateTime min, DateTime max) =>
        value < min ? min : value > max ? max : value;

    private static string FormatDate(DateTime d) => d.ToString("MMM d, yyyy");
}
