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

    // ---- Time-of-day picker: the two option lists and the split/combine helpers ----------------

    [Fact]
    public void TimeOptionsMinutes_CoversTheWholeDayOnQuarterHours()
    {
        Assert.Equal(97, CalendarStyles.TimeOptionsMinutes.Length);
        Assert.Equal(0, CalendarStyles.TimeOptionsMinutes[0]);
        Assert.Equal(24 * 60, CalendarStyles.TimeOptionsMinutes[^1]);
        Assert.All(CalendarStyles.TimeOptionsMinutes, m => Assert.Equal(0, m % 15));
    }

    // The whole point of the split: the longest list a staff member scrolls is 25, not 97.
    [Fact]
    public void HourOptionsMinutes_IsEveryHourPlusEndOfDayMidnight()
    {
        Assert.Equal(25, CalendarStyles.HourOptionsMinutes.Length);
        Assert.All(CalendarStyles.HourOptionsMinutes, m => Assert.Equal(0, m % 60));
        Assert.Equal(24 * 60, CalendarStyles.HourOptionsMinutes[^1]);
        Assert.Equal("Midnight", CalendarStyles.FormatMinutes(CalendarStyles.HourOptionsMinutes[^1]));
    }

    // Every combination the two dropdowns can produce has to be a value the drafts consider legal,
    // or the picker can write a time that no picker can then display.
    [Fact]
    public void EveryHourAndQuarterCombination_IsAValidStoredValue()
    {
        foreach (var h in CalendarStyles.HourOptionsMinutes)
        {
            foreach (var q in CalendarStyles.QuarterOptions)
            {
                var total = h >= 24 * 60 ? 24 * 60 : h + q;
                Assert.Contains(total, CalendarStyles.TimeOptionsMinutes);
            }
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]           // midnight at the start of the day
    [InlineData(1095, 1080, 15)]    // 6:15 PM
    [InlineData(1425, 1380, 45)]    // 11:45 PM, the last real quarter
    [InlineData(1440, 1440, 0)]     // midnight at the end of the day keeps its own hour
    public void HourAndQuarterParts_SplitATotalBackIntoTheTwoControls(int total, int hour, int quarter)
    {
        Assert.Equal(hour, CalendarStyles.HourPartOf(total));
        Assert.Equal(quarter, CalendarStyles.QuarterPartOf(total));
    }

    [Theory]
    [InlineData(1095, 1095)]  // already on the grid - untouched
    [InlineData(1087, 1080)]  // 6:07 PM rounds down
    [InlineData(1088, 1095)]  // 6:08 PM rounds up
    [InlineData(1090, 1095)]  // an exact tie rounds up
    [InlineData(1110, 1110)]  // 6:30 PM, an old half-hour value, is still on the grid
    [InlineData(1433, 1440)]  // 11:53 PM rounds to end-of-day, never wrapping to 0
    [InlineData(1440, 1440)]  // end-of-day is a fixed point
    public void SnapToQuarter_PutsAnOffGridTimeOnTheNearestQuarter(int input, int expected)
    {
        Assert.Equal(expected, CalendarStyles.SnapToQuarter(input));
    }

    [Fact]
    public void SnapToQuarter_AlwaysLandsOnAnOptionThePickerCanDisplay()
    {
        for (var m = 0; m <= 24 * 60; m++)
        {
            Assert.Contains(CalendarStyles.SnapToQuarter(m), CalendarStyles.TimeOptionsMinutes);
        }
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

    [Fact]
    public void TruncateForConflictDisplay_ShortTitle_ReturnedUnchanged()
    {
        Assert.Equal("Smith Wedding", CalendarStyles.TruncateForConflictDisplay("Smith Wedding"));
    }

    [Fact]
    public void TruncateForConflictDisplay_TitleLongerThanTheCap_TrimsAndAppendsAnEllipsis()
    {
        var title = new string('a', 60);

        var result = CalendarStyles.TruncateForConflictDisplay(title, maxChars: 40);

        Assert.Equal(41, result.Length); // 40 kept chars + the ellipsis
        Assert.EndsWith("…", result);
        Assert.StartsWith(new string('a', 40), result);
    }

    [Fact]
    public void TruncateForConflictDisplay_NullTitle_ReturnsEmptyString()
    {
        Assert.Equal("", CalendarStyles.TruncateForConflictDisplay(null));
    }

    // The toolbar date label's width floor stops the nav controls next to it shifting while you step
    // through dates. These pin the two properties that matter: each view gets enough room for the
    // widest string its own format can produce, and the three are ordered by how long those strings
    // get - so a future format change that outgrows its box shows up here rather than as controls
    // that quietly start moving again.
    [Theory]
    [InlineData("Month", 132)]
    [InlineData("Week", 182)]
    [InlineData("Day", 260)]
    public void AnchorLabelMinWidth_MatchesTheMeasuredWidthForEachView(string view, int expected)
    {
        Assert.Equal(expected, CalendarStyles.AnchorLabelMinWidthPx(view));
    }

    [Theory]
    [InlineData("month")]
    [InlineData("MONTH")]
    [InlineData("Day")]
    public void AnchorLabelMinWidth_IsCaseInsensitive(string view)
    {
        // Callers pass their own ViewMode.ToString() ("Month"), while the URLs both calendars build
        // use the lowercase form - both have to resolve to the same box.
        Assert.Equal(CalendarStyles.AnchorLabelMinWidthPx(view.ToLowerInvariant()), CalendarStyles.AnchorLabelMinWidthPx(view));
    }

    [Fact]
    public void AnchorLabelMinWidth_UnknownView_FallsBackToTheMonthWidth()
    {
        // Month is the narrowest and the default view, so an unrecognized name degrades to a small
        // box that still renders in full (min-width is a floor, not a clip) rather than a dead gap.
        Assert.Equal(CalendarStyles.AnchorLabelMinWidthPx("Month"), CalendarStyles.AnchorLabelMinWidthPx("agenda"));
    }

    [Fact]
    public void AnchorLabelMinWidth_GrowsWithHowLongEachViewsLabelGets()
    {
        Assert.True(CalendarStyles.AnchorLabelMinWidthPx("Month") < CalendarStyles.AnchorLabelMinWidthPx("Week"));
        Assert.True(CalendarStyles.AnchorLabelMinWidthPx("Week") < CalendarStyles.AnchorLabelMinWidthPx("Day"));
    }
}
