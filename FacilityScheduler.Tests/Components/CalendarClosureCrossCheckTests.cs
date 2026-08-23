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

    // An all-day club event stores End as the INCLUSIVE last day at midnight (ClubEventService's
    // all-day wire translation, and ClubEventDraft.End when IsAllDay). So a single-day all-day
    // closure has Start == End == that day's midnight, and the half-open overlap test above can
    // never see it as covering any time later that day.
    //
    // These two tests pin the CURRENT behavior, which is a live bug: all-day is the DEFAULT for a
    // club event (ClubEvent.IsAllDay = true), so the most natural way to record "we're closed
    // Tuesday" does not block Tuesday bookings at all. PublicAvailabilityService already compensates
    // for exactly this (`ce.IsAllDay ? ce.End.Date.AddDays(1) : ce.End`); this path never did.
    // Deliberately NOT fixed here - step 0 is a zero-behavior-change extraction, and the fix is a
    // real behavior change that needs its own decision. Flagged to the user.
    [Fact]
    public void ActiveClosures_SingleDayAllDayClosure_DoesNotBlockThatDay_KnownBug()
    {
        var closures = Calendar.ActiveClosures(
            [Closure(Day, Day, isAllDay: true)], Day.AddHours(18), Day.AddHours(20));

        Assert.Empty(closures);
    }

    [Fact]
    public void ActiveClosures_MultiDayAllDayClosure_DoesNotBlockItsFinalDay_KnownBug()
    {
        // Aug 24-25 all-day: End is Aug 25 midnight, so Aug 25 evening reads as after it.
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddDays(-1), Day, isAllDay: true)], Day.AddHours(18), Day.AddHours(20));

        Assert.Empty(closures);
    }

    [Fact]
    public void ActiveClosures_MultiDayAllDayClosure_StillBlocksItsEarlierDays()
    {
        // The same Aug 24-25 closure does block Aug 24, since Aug 25 midnight is after Aug 24 evening.
        var closures = Calendar.ActiveClosures(
            [Closure(Day.AddDays(-1), Day, isAllDay: true)], Day.AddDays(-1).AddHours(18), Day.AddDays(-1).AddHours(20));

        Assert.Single(closures);
    }
}
