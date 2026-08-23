using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Components;

/// <summary>Covers Calendar.ActiveClosures - the closure cross-check that D13 was narrowed to allow
/// (§4.4: a club event flagged MarksSheetsUnavailable blocks new sheet bookings). It lives on the page
/// rather than in either service because the two services are deliberately decoupled, which is also why
/// it had no coverage until now.</summary>
public class CalendarClosureCrossCheckTests
{
    private static readonly DateTime Day = new(2026, 8, 25);

    private static ClubEvent Closure(DateTime start, DateTime end, bool marksUnavailable = true, bool isAllDay = false) => new()
    {
        Title = "Test closure",
        Category = ClubEventCategory.Closure,
        Start = start,
        End = end,
        IsAllDay = isAllDay,
        MarksSheetsUnavailable = marksUnavailable,
    };

    [Fact]
    public void ActiveClosures_TimedClosureOverlappingTheBooking_Conflicts()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddHours(17), Day.AddHours(21))], Day.AddHours(18), Day.AddHours(20));

        Assert.Single(closures);
    }

    [Fact]
    public void ActiveClosures_ClubEventNotMarkedUnavailable_NeverConflicts()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddHours(17), Day.AddHours(21), marksUnavailable: false)], Day.AddHours(18), Day.AddHours(20));

        Assert.Empty(closures);
    }

    [Fact]
    public void ActiveClosures_ClosureEndingExactlyWhenTheBookingStarts_DoesNotConflict()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddHours(14), Day.AddHours(18))], Day.AddHours(18), Day.AddHours(20));

        Assert.Empty(closures);
    }

    [Fact]
    public void ActiveClosures_ClosureStartingExactlyWhenTheBookingEnds_DoesNotConflict()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddHours(20), Day.AddHours(23))], Day.AddHours(18), Day.AddHours(20));

        Assert.Empty(closures);
    }

    [Fact]
    public void ActiveClosures_ClosureFullyInsideTheBooking_Conflicts()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddHours(18).AddMinutes(30), Day.AddHours(19))], Day.AddHours(18), Day.AddHours(20));

        Assert.Single(closures);
    }

    [Fact]
    public void ActiveClosures_OnlyTheFlaggedEventsAreReturned()
    {
        ClubEvent[] events =
        [
            Closure(Day.AddHours(17), Day.AddHours(21), marksUnavailable: false),
            Closure(Day.AddHours(17), Day.AddHours(21)),
        ];

        var closures = Calendar.ActiveClosures(events, Day.AddHours(18), Day.AddHours(20));

        Assert.Single(closures);
        Assert.True(closures[0].MarksSheetsUnavailable);
    }

    // An all-day club event stores End as the INCLUSIVE last day at midnight, so a single-day all-day
    // closure has Start == End == that day's midnight. Comparing a booking against that raw End made
    // the closure block nothing at all on its own day - and since IsAllDay defaults to true, that was
    // the most natural way to record a closure. Fixed 2026-08-23 by comparing against
    // ClubEvent.ExclusiveEnd. These three tests are the regression guard.
    [Fact]
    public void ActiveClosures_SingleDayAllDayClosure_BlocksBookingsThatDay()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day, Day, isAllDay: true)], Day.AddHours(18), Day.AddHours(20));

        Assert.Single(closures);
    }

    [Fact]
    public void ActiveClosures_MultiDayAllDayClosure_BlocksItsFinalDay()
    {
        // Aug 24-25 all-day: End is Aug 25 midnight, but it actually runs through Aug 25.
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddDays(-1), Day, isAllDay: true)], Day.AddHours(18), Day.AddHours(20));

        Assert.Single(closures);
    }

    [Fact]
    public void ActiveClosures_MultiDayAllDayClosure_BlocksItsEarlierDays()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddDays(-1), Day, isAllDay: true)], Day.AddDays(-1).AddHours(18), Day.AddDays(-1).AddHours(20));

        Assert.Single(closures);
    }

    [Fact]
    public void ActiveClosures_AllDayClosure_DoesNotBlockTheDayAfterItEnds()
    {
        // The exclusive end is Aug 26 midnight, so Aug 26 itself is clear - the fix must not
        // over-correct into blocking a day the closure never covered.
        var closures = Calendar.ActiveClosures(
            [Closure(Day, Day, isAllDay: true)], Day.AddDays(1).AddHours(18), Day.AddDays(1).AddHours(20));

        Assert.Empty(closures);
    }
}
