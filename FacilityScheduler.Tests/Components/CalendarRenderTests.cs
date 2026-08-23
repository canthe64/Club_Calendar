using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>First render coverage for Calendar.razor. It had none before the unified-event refactor,
/// which rewrites this page substantially - these are the smoke tests that make that rewrite visible
/// if it breaks the page's basic structure, not a full behavioral suite.</summary>
public class CalendarRenderTests : BunitContext
{
    [Fact]
    public void Render_ShowsTheToolbarAndTheShowFilterRow()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        var markup = cut.Markup;
        // The single "SHOW" heading became two group headings when off-ice gained per-category
        // filtering; CalendarFilterTests covers the groups themselves.
        Assert.Contains("ON ICE", markup);
        Assert.Contains("OFF ICE", markup);
        Assert.Contains("Today", markup);
        Assert.Contains("Month", markup);
        Assert.Contains("Week", markup);
        Assert.Contains("Day", markup);
    }

    [Fact]
    public void Render_DefaultsToMonthView()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        // The month grid renders a weekday header row; the hourly grids don't.
        Assert.Contains("Mon", cut.Markup);
        Assert.Contains("Sun", cut.Markup);
    }

    [Fact]
    public void Render_ShowsEveryOnIceCategoryChip()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        foreach (var cat in CalendarStyles.SheetCategories)
        {
            Assert.Contains(CalendarStyles.CategoryLabel(cat), cut.Markup);
        }
    }

    [Fact]
    public void Render_ShowsTheNewMenuButtonAndTheOffIceEventsLink()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        Assert.Contains("New", cut.Markup);
        Assert.Contains("/club-events", cut.Markup);
        Assert.Contains("/search", cut.Markup);
    }
}
