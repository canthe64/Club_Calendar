namespace FacilityScheduler.Domain.Search;

/// <summary>
/// Whether a single <see cref="SheetBooking"/>/<see cref="ClubEvent"/> satisfies a parsed <see
/// cref="SearchQuery"/>. Pure, no clock - day-of-week matching is against the item's own
/// Start/End, not "today."
///
/// Deliberately narrow on what bare-word/quoted-phrase terms can match: only the display title
/// (<see cref="CalendarStyles.BookingDisplayTitle"/> for a booking, <see cref="ClubEvent.Title"/>
/// for a club event) - never <see cref="SheetBooking.RenterPhone"/>, <see
/// cref="SheetBooking.RenterEmail"/>, or <see cref="SheetBooking.Notes"/>. That's a scope decision,
/// not an oversight: staff can see all of that in the modal they open from a result, but it was
/// never meant to be searchable text (see the plan's "why can't I search the phone number" note).
/// </summary>
internal static class EventSearchMatcher
{
    public static bool Matches(SheetBooking booking, SearchQuery query)
    {
        if (query.IsEmpty || query.Kind == SearchKindFilter.OffIceOnly)
        {
            return false;
        }

        if (query.BookingCategories is { } categories && !categories.Contains(booking.Category))
        {
            return false;
        }

        if (query.Days is { } days && !OccursOnAnyWeekday(booking.Start, booking.End, days))
        {
            return false;
        }

        return MatchesTitleTerms(CalendarStyles.BookingDisplayTitle(booking), query.TitleTerms);
    }

    public static bool Matches(ClubEvent clubEvent, SearchQuery query)
    {
        if (query.IsEmpty || query.Kind == SearchKindFilter.OnIceOnly)
        {
            return false;
        }

        if (query.ClubCategories is { } categories && !categories.Contains(clubEvent.Category))
        {
            return false;
        }

        if (query.Days is { } days && !OccursOnAnyWeekday(clubEvent.Start, clubEvent.ExclusiveEnd, days))
        {
            return false;
        }

        return MatchesTitleTerms(clubEvent.Title, query.TitleTerms);
    }

    /// <summary>Whether an item spanning [start, end) touches any of the given weekdays. Walks every
    /// calendar day the range could plausibly cover, but a day only counts when
    /// <see cref="CalendarStyles.OccursOnDay"/> agrees the item genuinely occupies it - so the same
    /// half-open-interval rule applies here as everywhere else (D107): a booking or timed event
    /// ending exactly at midnight has zero real duration on the following day and must not match its
    /// weekday, while a booking ending at 11:59PM still matches only its own day. Callers pass a real
    /// exclusive instant for <paramref name="end"/> - a booking's own End, or a club event's
    /// <see cref="ClubEvent.ExclusiveEnd"/> (never its raw inclusive-last-day End). Walking the range
    /// rather than checking Start/End's DayOfWeek in isolation still matters: a Fri-Sun all-day club
    /// event covers Saturday, which shows up only by walking the days between, and an item spanning a
    /// week or more must match every weekday it touches, not just its first and last.</summary>
    internal static bool OccursOnAnyWeekday(DateTime start, DateTime end, HashSet<DayOfWeek> days)
    {
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (days.Contains(day.DayOfWeek) && CalendarStyles.OccursOnDay(start, end, day))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTitleTerms(string title, IReadOnlyList<string> terms) =>
        terms.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
}
