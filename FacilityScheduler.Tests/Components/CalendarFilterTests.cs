using Bunit;
using FacilityScheduler;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>Covers the two-group SHOW row that replaced six on-ice chips plus a single off-ice
/// on/off checkbox. The off-ice half is new capability - club events were previously all-or-nothing.</summary>
public class CalendarFilterTests : BunitContext
{
    private IRenderedComponent<Calendar> RenderCalendar()
    {
        StaffPageServices.Register(this);
        return Render<Calendar>();
    }

    private static void ClickChip(IRenderedComponent<Calendar> cut, string label) =>
        cut.FindAll("span").First(s => s.TextContent.Trim() == label).Click();

    [Fact]
    public void ShowRow_RendersBothGroupHeadings()
    {
        var cut = RenderCalendar();

        Assert.Contains("ON ICE", cut.Markup);
        Assert.Contains("OFF ICE", cut.Markup);
        // The old single-checkbox control is gone.
        Assert.DoesNotContain("Show Off-Ice Events", cut.Markup);
    }

    [Fact]
    public void ShowRow_RendersEveryOnIceCategoryChip()
    {
        var cut = RenderCalendar();

        foreach (var cat in CalendarStyles.SheetCategories)
        {
            Assert.Contains(CalendarStyles.CategoryLabel(cat), cut.Markup);
        }
    }

    [Fact]
    public void ShowRow_RendersEveryOffIceCategoryChip()
    {
        var cut = RenderCalendar();

        foreach (var cat in CalendarStyles.ClubEventCategories)
        {
            Assert.Contains(CalendarStyles.ClubEventCategoryLabel(cat), cut.Markup);
        }
    }

    [Fact]
    public void ShowRow_EverythingStartsSelected()
    {
        var cut = RenderCalendar();

        // Both group links read "None", which is only true when each group has a selection.
        Assert.Equal(2, cut.FindAll("span").Count(s => s.TextContent.Trim() == "None"));
    }

    [Fact]
    public void ClickingAnOffIceGroupNoneLink_ThenAll_RoundTripsTheSelection()
    {
        var cut = RenderCalendar();

        // Second "None" is the off-ice group's (markup order: on-ice row, then off-ice row).
        cut.FindAll("span").Where(s => s.TextContent.Trim() == "None").Last().Click();
        Assert.Contains("All", cut.FindAll("span").Select(s => s.TextContent.Trim()));

        cut.FindAll("span").First(s => s.TextContent.Trim() == "All").Click();
        Assert.Equal(2, cut.FindAll("span").Count(s => s.TextContent.Trim() == "None"));
    }

    [Fact]
    public void ClickingAnOnIceChip_TogglesOnlyThatCategory()
    {
        var cut = RenderCalendar();
        var label = CalendarStyles.CategoryLabel(BookingCategory.League);
        var colour = CalendarStyles.CategoryColor(BookingCategory.League);

        // Selected chips are filled with the category colour; deselected ones are white.
        Assert.Contains($"background:{colour}", cut.Markup);

        ClickChip(cut, label);

        var chip = cut.FindAll("span").First(s => s.TextContent.Trim() == label);
        Assert.Contains("background:#fff", chip.GetAttribute("style"));
    }

    [Fact]
    public void ClickingAnOffIceChip_TogglesOnlyThatCategory()
    {
        var cut = RenderCalendar();
        var label = CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Meetings);

        ClickChip(cut, label);

        var toggled = cut.FindAll("span").First(s => s.TextContent.Trim() == label);
        Assert.Contains("background:#fff", toggled.GetAttribute("style"));

        // Its neighbour is untouched.
        var otherLabel = CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Closure);
        var other = cut.FindAll("span").First(s => s.TextContent.Trim() == otherLabel);
        Assert.Contains($"background:{CalendarStyles.ClubEventCategoryColor(ClubEventCategory.Closure)}", other.GetAttribute("style"));
    }

    [Fact]
    public void OnIceAndOffIceGroups_ToggleIndependently()
    {
        var cut = RenderCalendar();

        // Clear on-ice only; the off-ice group must still report a selection.
        cut.FindAll("span").First(s => s.TextContent.Trim() == "None").Click();

        var links = cut.FindAll("span").Where(s => s.TextContent.Trim() is "All" or "None").ToList();
        Assert.Equal("All", links[0].TextContent.Trim());
        Assert.Equal("None", links[1].TextContent.Trim());
    }

    [Fact]
    public void BothOtherChips_RenderUnderTheirOwnGroupHeading()
    {
        // Both families contain "Other" - the group headings are what tells them apart, so both must
        // actually be present rather than one silently shadowing the other.
        var cut = RenderCalendar();

        Assert.Equal(2, cut.FindAll("span").Count(s => s.TextContent.Trim() == "Other"));
    }
}
