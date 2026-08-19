using FacilityScheduler;

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
}
