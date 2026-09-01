using FacilityScheduler.Domain;
using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

public class EventSearchMatcherTests
{
    private static SheetBooking Booking(
        string? renterName = null, string? renterPhone = null, string? renterEmail = null, string? notes = null,
        BookingCategory category = BookingCategory.League, DateTime? start = null, DateTime? end = null) => new()
    {
        SheetMailbox = "sheet1@example.com",
        Category = category,
        State = BookingState.Confirmed,
        RenterName = renterName,
        RenterPhone = renterPhone,
        RenterEmail = renterEmail,
        Notes = notes,
        Start = start ?? new DateTime(2026, 8, 22, 18, 0, 0), // a Saturday
        End = end ?? new DateTime(2026, 8, 22, 20, 0, 0)
    };

    private static ClubEvent ClubEvent(
        string title = "Fall Bonspiel", ClubEventCategory category = ClubEventCategory.OutOfTownBonspiels,
        DateTime? start = null, DateTime? end = null, bool isAllDay = true) => new()
    {
        Title = title,
        Category = category,
        IsAllDay = isAllDay,
        Start = start ?? new DateTime(2026, 8, 21), // Friday
        End = end ?? new DateTime(2026, 8, 23) // Sunday, inclusive
    };

    [Fact]
    public void Matches_Booking_BlankRenterName_FallsBackToCategoryLabelForTitleMatching()
    {
        var booking = Booking(renterName: null, category: BookingCategory.PracticeIce);
        var query = SearchQueryParser.Parse("practice");

        Assert.True(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_MidWordSubstring_Matches()
    {
        var booking = Booking(renterName: "Bonspiel Weekend");
        var query = SearchQueryParser.Parse("spiel");

        Assert.True(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_TwoBareWords_MatchRegardlessOfOrderInTitle()
    {
        var booking = Booking(renterName: "Smith Family Wedding");
        var query = SearchQueryParser.Parse("wedding smith");

        Assert.True(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_RenterPhone_IsNeverSearched()
    {
        var booking = Booking(renterName: "Someone Else", renterPhone: "555-1234");
        var query = SearchQueryParser.Parse("555-1234");

        Assert.False(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_RenterEmail_IsNeverSearched()
    {
        var booking = Booking(renterName: "Someone Else", renterEmail: "person@example.com");
        var query = SearchQueryParser.Parse("person");

        Assert.False(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_Notes_IsNeverSearched()
    {
        var booking = Booking(renterName: "Someone Else", notes: "zamboni broke down");
        var query = SearchQueryParser.Parse("zamboni");

        Assert.False(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_Booking_DaySaturday_MatchesASaturdayBooking()
    {
        var booking = Booking(start: new DateTime(2026, 8, 22, 18, 0, 0), end: new DateTime(2026, 8, 22, 20, 0, 0));
        var query = SearchQueryParser.Parse("day:saturday");

        Assert.True(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_ClubEvent_DaySaturday_MatchesAnAllDayFridayToSundayEvent()
    {
        // The inclusive-End all-day club event must be walked day-by-day, not checked only at its
        // Start/End DayOfWeek, or the Saturday in the middle drops off entirely.
        var ce = ClubEvent(start: new DateTime(2026, 8, 21), end: new DateTime(2026, 8, 23));
        var query = SearchQueryParser.Parse("day:saturday");

        Assert.True(EventSearchMatcher.Matches(ce, query));
    }

    [Fact]
    public void Matches_Booking_EndingExactlyAtMidnight_DoesNotMatchTheFollowingWeekday()
    {
        // 10PM Tuesday -> 12AM Wednesday: the booking has zero real duration on Wednesday, so a
        // day:wednesday search must not match it (the same exact-midnight boundary bug D107 fixed
        // in CalendarStyles.OccursOnDay). It must still match day:tuesday.
        var booking = Booking(
            start: new DateTime(2026, 8, 25, 22, 0, 0), // Tuesday
            end: new DateTime(2026, 8, 26, 0, 0, 0)); // exactly midnight, Wednesday

        Assert.False(EventSearchMatcher.Matches(booking, SearchQueryParser.Parse("day:wednesday")));
        Assert.True(EventSearchMatcher.Matches(booking, SearchQueryParser.Parse("day:tuesday")));
    }

    [Fact]
    public void Matches_ClubEvent_DaySunday_StillMatchesAnAllDayFridayToSundayEvent()
    {
        // Guard against over-correcting the midnight fix: an all-day club event's End is the
        // INCLUSIVE last day at midnight, so a Fri-Sun event genuinely covers Sunday. The club
        // event call site passes ExclusiveEnd for exactly this reason.
        var ce = ClubEvent(start: new DateTime(2026, 8, 21), end: new DateTime(2026, 8, 23));

        Assert.True(EventSearchMatcher.Matches(ce, SearchQueryParser.Parse("day:sunday")));
    }

    [Fact]
    public void OccursOnAnyWeekday_SpanOfSevenOrMoreDays_MatchesEveryWeekday()
    {
        var start = new DateTime(2026, 8, 17); // Monday
        var end = start.AddDays(7);

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.True(EventSearchMatcher.OccursOnAnyWeekday(start, end, [day]));
        }
    }

    [Fact]
    public void Matches_ClubOnlyCategory_ExcludesEveryBooking()
    {
        var booking = Booking(category: BookingCategory.Bonspiel);
        var query = SearchQueryParser.Parse("category:outoftownbonspiels");

        Assert.False(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_BookingOnlyCategory_ExcludesEveryClubEvent()
    {
        var ce = ClubEvent(category: ClubEventCategory.Closure);
        var query = SearchQueryParser.Parse("category:league");

        Assert.False(EventSearchMatcher.Matches(ce, query));
    }

    [Fact]
    public void Matches_TypeBooking_ExcludesATitleMatchingClubEvent()
    {
        var ce = ClubEvent(title: "Junior League Night");
        var query = SearchQueryParser.Parse("type:booking junior");

        Assert.False(EventSearchMatcher.Matches(ce, query));
    }

    [Fact]
    public void Matches_TypeClubEvent_ExcludesATitleMatchingBooking()
    {
        var booking = Booking(renterName: "Junior League Night");
        var query = SearchQueryParser.Parse("type:clubevent junior");

        Assert.False(EventSearchMatcher.Matches(booking, query));
    }

    [Fact]
    public void Matches_EmptyQuery_MatchesNothing()
    {
        var booking = Booking();
        var ce = ClubEvent();
        var query = SearchQueryParser.Parse("");

        Assert.False(EventSearchMatcher.Matches(booking, query));
        Assert.False(EventSearchMatcher.Matches(ce, query));
    }
}
