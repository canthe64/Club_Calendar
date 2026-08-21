using FacilityScheduler;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests;

public class CalendarStylesTests
{
    private static readonly DateTime Start = new(2026, 8, 21);
    private static readonly DateTime End = new(2026, 8, 23);

    [Theory]
    [InlineData(2026, 8, 20, false)] // day before
    [InlineData(2026, 8, 21, true)]  // first day
    [InlineData(2026, 8, 22, true)]  // middle day
    [InlineData(2026, 8, 23, true)]  // last day
    [InlineData(2026, 8, 24, false)] // day after
    public void OccursOnDay_MultiDaySpan_MatchesOnlyDaysWithinRange(int year, int month, int day, bool expected)
    {
        Assert.Equal(expected, CalendarStyles.OccursOnDay(Start, End, new DateTime(year, month, day)));
    }

    [Fact]
    public void OccursOnDay_SameDaySpan_MatchesOnlyThatDay()
    {
        var day = new DateTime(2026, 8, 21);
        Assert.True(CalendarStyles.OccursOnDay(day, day, day));
        Assert.False(CalendarStyles.OccursOnDay(day, day, day.AddDays(1)));
    }

    [Fact]
    public void ContinuationMarks_SameDaySpan_NeverShowsEitherMark()
    {
        var day = new DateTime(2026, 8, 21);
        var (before, after) = CalendarStyles.ContinuationMarks(day, day, day);
        Assert.False(before);
        Assert.False(after);
    }

    [Fact]
    public void ContinuationMarks_FirstDayOfSpan_ShowsOnlyAfter()
    {
        var (before, after) = CalendarStyles.ContinuationMarks(Start, End, Start);
        Assert.False(before);
        Assert.True(after);
    }

    [Fact]
    public void ContinuationMarks_MiddleDayOfSpan_ShowsBothMarks()
    {
        var (before, after) = CalendarStyles.ContinuationMarks(Start, End, Start.AddDays(1));
        Assert.True(before);
        Assert.True(after);
    }

    [Fact]
    public void ContinuationMarks_LastDayOfSpan_ShowsOnlyBefore()
    {
        var (before, after) = CalendarStyles.ContinuationMarks(Start, End, End);
        Assert.True(before);
        Assert.False(after);
    }

    private static SheetBooking Booking(string sheet, Guid groupId, string? eventId = null, DateTime? start = null, DateTime? end = null) => new()
    {
        SheetMailbox = sheet,
        EventId = eventId ?? Guid.NewGuid().ToString(),
        BookingGroupId = groupId,
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        Start = start ?? Start,
        End = end ?? Start.AddHours(2)
    };

    [Fact]
    public void BookingGroupKey_ThreeSheetsOfOneBooking_ShareTheSameKey()
    {
        var groupId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 21, 18, 0, 0);
        var end = start.AddHours(2);
        var sheet1 = Booking("sheet1@example.com", groupId, start: start, end: end);
        var sheet2 = Booking("sheet2@example.com", groupId, start: start, end: end);
        var sheet3 = Booking("sheet3@example.com", groupId, start: start, end: end);

        Assert.Equal(CalendarStyles.BookingGroupKey(sheet1), CalendarStyles.BookingGroupKey(sheet2));
        Assert.Equal(CalendarStyles.BookingGroupKey(sheet1), CalendarStyles.BookingGroupKey(sheet3));
    }

    [Fact]
    public void BookingGroupKey_TwoUnrelatedEmptyGuidBookingsAtTheSameTime_DoNotShareAKey()
    {
        var start = new DateTime(2026, 8, 21, 18, 0, 0);
        var end = start.AddHours(2);
        var a = Booking("sheet1@example.com", Guid.Empty, eventId: "eventA", start: start, end: end);
        var b = Booking("sheet1@example.com", Guid.Empty, eventId: "eventB", start: start, end: end);

        Assert.NotEqual(CalendarStyles.BookingGroupKey(a), CalendarStyles.BookingGroupKey(b));
    }

    [Fact]
    public void BookingGroupKey_TwoOccurrencesOfOneSeriesOnDifferentDates_DoNotShareAKey()
    {
        var groupId = Guid.NewGuid();
        var week1 = Booking("sheet1@example.com", groupId, start: new DateTime(2026, 8, 21, 18, 0, 0), end: new DateTime(2026, 8, 21, 20, 0, 0));
        var week2 = Booking("sheet1@example.com", groupId, start: new DateTime(2026, 8, 28, 18, 0, 0), end: new DateTime(2026, 8, 28, 20, 0, 0));

        Assert.NotEqual(CalendarStyles.BookingGroupKey(week1), CalendarStyles.BookingGroupKey(week2));
    }

    [Fact]
    public void SiblingGroup_ReturnsAllSheetsOfTheClickedBookingFromAMixedList()
    {
        var groupId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 21, 18, 0, 0);
        var end = start.AddHours(2);
        var clicked = Booking("sheet1@example.com", groupId, start: start, end: end);
        var sibling = Booking("sheet2@example.com", groupId, start: start, end: end);
        var unrelated = Booking("sheet3@example.com", Guid.NewGuid(), start: start, end: end);

        var group = CalendarStyles.SiblingGroup([clicked, sibling, unrelated], clicked);

        Assert.Equal(2, group.Count);
        Assert.Contains(clicked, group);
        Assert.Contains(sibling, group);
    }

    [Fact]
    public void SiblingGroup_EmptyGuidBooking_ReturnsOnlyItselfEvenWhenAnotherSharesItsTime()
    {
        var start = new DateTime(2026, 8, 21, 18, 0, 0);
        var end = start.AddHours(2);
        var clicked = Booking("sheet1@example.com", Guid.Empty, eventId: "eventA", start: start, end: end);
        var coincidental = Booking("sheet1@example.com", Guid.Empty, eventId: "eventB", start: start, end: end);

        var group = CalendarStyles.SiblingGroup([clicked, coincidental], clicked);

        Assert.Single(group);
        Assert.Same(clicked, group[0]);
    }

    [Fact]
    public void BookingDisplayTitle_RenterNamePresent_WinsOverCategoryLabel()
    {
        var booking = Booking("sheet1@example.com", Guid.NewGuid());
        booking.RenterName = "Smith Wedding";

        Assert.Equal("Smith Wedding", CalendarStyles.BookingDisplayTitle(booking));
    }

    [Fact]
    public void BookingDisplayTitle_BlankRenterName_FallsBackToCategoryLabel()
    {
        var booking = Booking("sheet1@example.com", Guid.NewGuid());
        booking.RenterName = "   ";

        Assert.Equal(CalendarStyles.CategoryLabel(BookingCategory.League), CalendarStyles.BookingDisplayTitle(booking));
    }
}
