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
        if (query.IsEmpty || query.Kind == SearchKindFilter.ClubEventOnly)
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
        if (query.IsEmpty || query.Kind == SearchKindFilter.BookingOnly)
        {
            return false;
        }

        if (query.ClubCategories is { } categories && !categories.Contains(clubEvent.Category))
        {
            return false;
        }

        if (query.Days is { } days && !OccursOnAnyWeekday(clubEvent.Start, clubEvent.End, days))
        {
            return false;
        }

        return MatchesTitleTerms(clubEvent.Title, query.TitleTerms);
    }

    /// <summary>Whether an item spanning [start, end] - inclusive by date, matching
    /// <see cref="CalendarStyles.OccursOnDay"/>'s own convention - touches any of the given weekdays.
    /// Iterates the whole inclusive range rather than checking Start/End's own DayOfWeek in
    /// isolation: an all-day club event's End is inclusive (a Fri-Sun event's End.DayOfWeek is
    /// Sunday, but Saturday only shows up by walking the days between), and a booking or event
    /// spanning a week or more must match every weekday it touches, not just its first and last.</summary>
    internal static bool OccursOnAnyWeekday(DateTime start, DateTime end, HashSet<DayOfWeek> days)
    {
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (days.Contains(day.DayOfWeek))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTitleTerms(string title, IReadOnlyList<string> terms) =>
        terms.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
}
