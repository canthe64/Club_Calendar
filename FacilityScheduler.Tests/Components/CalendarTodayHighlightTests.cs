using Bunit;
using FacilityScheduler.Components.Calendar;

namespace FacilityScheduler.Tests.Components;

/// <summary>Today highlighted on the staff calendar (request 2026-10-07). The public calendar's half
/// is PublicCalendarTodayAndToggleTests.</summary>
public class CalendarTodayHighlightTests : BunitContext
{
    private static readonly DateTime Today = new(2026, 10, 7);

    [Fact]
    public void StaffMonthGrid_HighlightsExactlyToday()
    {
        var cut = Render<MonthGrid>(p => p
            .Add(g => g.AnchorMonth, Today)
            .Add(g => g.Today, Today)
            .Add(g => g.Bookings, [])
            .Add(g => g.ClubEvents, []));

        // Only the date number is highlighted - the cell itself keeps its normal look.
        var highlighted = Assert.Single(cut.FindAll(".cal-today"));
        Assert.Equal("SPAN", highlighted.TagName);
        Assert.Equal("7", highlighted.TextContent.Trim());
        Assert.DoesNotContain(CalendarStyles.TodayBg, cut.Markup);
    }

    [Fact]
    public void StaffWeekGrid_HighlightsTodaysColumnHeading_OnlyInTheWeekContainingIt()
    {
        var thisWeek = Render<WeekGrid>(p => p.Add(g => g.WeekStart, Today.AddDays(-3)).Add(g => g.Today, Today));
        Assert.Contains("Oct 7", Assert.Single(thisWeek.FindAll(".cal-today")).TextContent);

        var nextWeek = Render<WeekGrid>(p => p.Add(g => g.WeekStart, Today.AddDays(4)).Add(g => g.Today, Today));
        Assert.Empty(nextWeek.FindAll(".cal-today"));
    }
}
