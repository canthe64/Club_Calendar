using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The "+ New Event"/"New Off-Ice Event" dropdown's default date, through two live-found rounds.
///
/// Round 1 (2026-09-01, D109): both Start and End were seeded from _anchorDate - whatever date the
/// calendar happened to be scrolled to. Navigate forward a few weeks, open the dropdown, correct only
/// Start, and End was left stranded on the far-future anchor - a silent multi-day/week span. Fixed by
/// dropping the anchor and falling back to a fixed "tomorrow" instead.
///
/// Round 2 (2026-09-01, D110): "tomorrow" solved the span bug but broke the common case it hadn't
/// broken before - staff who deliberately navigate ahead to enter a future event now had to re-navigate
/// forward again after the dialog opened, since "tomorrow" ignored where they'd already scrolled to.
/// This is the fix that landed: period-aware - Today when Today is actually part of the period
/// currently in view, otherwise that period's first day (never any other day in it, since there's no
/// more-correct guess once Today is out of view).
/// </summary>
public class CalendarCreateFormDefaultDateTests : BunitContext
{
    private static (IRenderedComponent<Calendar> Cut, FacilityConfiguration Facility) RenderCalendar(
        BunitContext ctx, DateTime anchor, string view)
    {
        var facility = StaffPageServices.Register(ctx).Facility;
        // Calendar.razor's ?date=/?view= are [SupplyParameterFromQuery] - bUnit refuses to set them as
        // ordinary component parameters and requires navigating the fake NavigationManager instead,
        // exactly as the real router would supply them from a Prev/Next click or a date jump.
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("date", anchor.ToString("yyyy-MM-dd")));
        nav.NavigateTo(nav.GetUriWithQueryParameter("view", view));
        return (ctx.Render<Calendar>(), facility);
    }

    private static void OpenNewEventMenuItem(IRenderedComponent<Calendar> cut, string itemText)
    {
        cut.FindAll("span").First(el => el.TextContent.Contains("+ New Event")).Click();
        cut.FindAll("div").First(el => el.TextContent.Trim() == itemText).Click();
    }

    // The page's own "Jump to date" input (Calendar.razor's header) is also input[type=date] and
    // renders before the modal in DOM order - a plain type-selector query would silently pick that up
    // instead of the modal's Start/End fields (it's bound to _anchorDate, so a mistaken match here
    // would look exactly like the D109 bug this suite exists to catch). Excluded by its aria-label.
    private static List<AngleSharp.Dom.IElement> ModalDateInputs(IRenderedComponent<Calendar> cut) =>
        [.. cut.FindAll("input[type=date]").Where(el => el.GetAttribute("aria-label") != "Jump to date")];

    private static DateTime SeededStartDate(IRenderedComponent<Calendar> cut) =>
        DateTime.Parse(ModalDateInputs(cut)[0].GetAttribute("value")!);

    /// <summary>A date within the anchor's own week (Sunday-Saturday, matching Calendar's WeekStart)
    /// but never Today itself - proves the "Today is in view" branch isn't just trivially echoing
    /// whatever date was passed in.</summary>
    private static DateTime DifferentDayInSameWeekAs(DateTime today)
    {
        var weekStart = today.Date.AddDays(-(int)today.DayOfWeek);
        return weekStart == today ? weekStart.AddDays(1) : weekStart;
    }

    /// <summary>A date within the anchor's own month but never Today itself - same reasoning as
    /// <see cref="DifferentDayInSameWeekAs"/>, for Month view.</summary>
    private static DateTime DifferentDayInSameMonthAs(DateTime today) =>
        new(today.Year, today.Month, today.Day == 1 ? 2 : 1);

    [Fact]
    public void MonthView_ViewingAFutureMonth_DefaultsToTheFirstOfThatMonth()
    {
        // Two months out (not one) and a few days into it, not the 1st - guards against both a
        // month-rollover edge case and against the code merely echoing whatever day was passed.
        var facilityProbe = TestFacility.Create();
        var farMonthAnchor = new DateTime(facilityProbe.Today.Year, facilityProbe.Today.Month, 1).AddMonths(2).AddDays(4);
        var (cut, facility) = RenderCalendar(this, farMonthAnchor, "month");

        OpenNewEventMenuItem(cut, "New Event");

        var expected = new DateTime(farMonthAnchor.Year, farMonthAnchor.Month, 1);
        Assert.Equal(expected, SeededStartDate(cut));
        Assert.NotEqual(facility.Today, SeededStartDate(cut));
    }

    [Fact]
    public void MonthView_ViewingTheCurrentMonth_DefaultsToToday_EvenViewingALaterDayInIt()
    {
        var facilityProbe = TestFacility.Create();
        var sameMonthAnchor = DifferentDayInSameMonthAs(facilityProbe.Today);
        var (cut, facility) = RenderCalendar(this, sameMonthAnchor, "month");

        OpenNewEventMenuItem(cut, "New Event");

        Assert.Equal(facility.Today, SeededStartDate(cut));
    }

    [Fact]
    public void WeekView_ViewingAFutureWeek_DefaultsToThatWeeksFirstDay()
    {
        var facilityProbe = TestFacility.Create();
        // Exactly 3 weeks out - always a different week from Today's, regardless of which weekday
        // Today happens to be.
        var farWeekAnchor = facilityProbe.Today.AddDays(21);
        var (cut, facility) = RenderCalendar(this, farWeekAnchor, "week");

        OpenNewEventMenuItem(cut, "New Event");

        var expectedWeekStart = farWeekAnchor.Date.AddDays(-(int)farWeekAnchor.DayOfWeek);
        Assert.Equal(expectedWeekStart, SeededStartDate(cut));
        Assert.NotEqual(facility.Today, SeededStartDate(cut));
    }

    [Fact]
    public void WeekView_ViewingTheCurrentWeek_DefaultsToToday_EvenViewingALaterDayInIt()
    {
        var facilityProbe = TestFacility.Create();
        var sameWeekAnchor = DifferentDayInSameWeekAs(facilityProbe.Today);
        var (cut, facility) = RenderCalendar(this, sameWeekAnchor, "week");

        OpenNewEventMenuItem(cut, "New Event");

        Assert.Equal(facility.Today, SeededStartDate(cut));
    }

    [Fact]
    public void DayView_AlwaysDefaultsToTheDayBeingViewed()
    {
        // Day view has no wider "period" to reason about separately from the one day shown - the
        // anchor day and the viewed period are the same thing, so it always seeds that exact day,
        // future or not.
        var facilityProbe = TestFacility.Create();
        var futureDay = facilityProbe.Today.AddDays(10);
        var (cut, _) = RenderCalendar(this, futureDay, "day");

        OpenNewEventMenuItem(cut, "New Event");

        Assert.Equal(futureDay.Date, SeededStartDate(cut));
    }

    [Fact]
    public void NewOffIceEvent_FromDropdown_UsesTheSamePeriodAwareDefaultAsNewEvent()
    {
        var facilityProbe = TestFacility.Create();
        var farMonthAnchor = new DateTime(facilityProbe.Today.Year, facilityProbe.Today.Month, 1).AddMonths(2);
        var (cut, _) = RenderCalendar(this, farMonthAnchor, "month");

        OpenNewEventMenuItem(cut, "New Off-Ice Event");

        Assert.Equal(farMonthAnchor, SeededStartDate(cut));
    }

    [Fact]
    public void NewEvent_FromDropdown_StartAndEndDateMatch_SoThereIsNoAccidentalMultiDaySpan()
    {
        // The original reported symptom (D109): Start and End landing on different dates before staff
        // typed anything at all. They must always agree on open, in every scenario above.
        var facilityProbe = TestFacility.Create();
        var farMonthAnchor = new DateTime(facilityProbe.Today.Year, facilityProbe.Today.Month, 1).AddMonths(2).AddDays(4);
        var (cut, _) = RenderCalendar(this, farMonthAnchor, "month");

        OpenNewEventMenuItem(cut, "New Event");

        var dateInputs = ModalDateInputs(cut);
        var startValue = dateInputs[0].GetAttribute("value");
        var endValue = dateInputs[1].GetAttribute("value");
        Assert.Equal(startValue, endValue);
    }
}
