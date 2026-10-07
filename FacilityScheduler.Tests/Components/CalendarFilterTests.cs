using AngleSharp.Dom;
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

    private static IElement Chip(IRenderedComponent<Calendar> cut, string label) =>
        cut.FindAll("[aria-pressed]").First(s => s.TextContent.Trim() == label);

    private static void ClickChip(IRenderedComponent<Calendar> cut, string label) => Chip(cut, label).Click();

    private static bool IsSelected(IElement chip) => chip.GetAttribute("aria-pressed") == "true";

    [Fact]
    public void ShowRow_RendersBothGroupHeadings()
    {
        var cut = RenderCalendar();

        Assert.Contains("ON ICE", cut.Markup);
        Assert.Contains("OFF ICE", cut.Markup);
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
        var otherLabel = CalendarStyles.CategoryLabel(BookingCategory.GroupEvent);
        Assert.True(IsSelected(Chip(cut, label)));

        ClickChip(cut, label);

        Assert.False(IsSelected(Chip(cut, label)));
        Assert.True(IsSelected(Chip(cut, otherLabel)));
    }

    [Fact]
    public void ClickingAnOffIceChip_TogglesOnlyThatCategory()
    {
        var cut = RenderCalendar();
        var label = CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Meetings);

        ClickChip(cut, label);

        Assert.False(IsSelected(Chip(cut, label)));
        // Its neighbour is untouched.
        Assert.True(IsSelected(Chip(cut, CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Closure))));
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

    [Fact]
    public async Task DeselectingAChip_HidesThatCategorysEvents_AndNothingElse()
    {
        var services = StaffPageServices.Register(this);
        var day = services.Facility.Today;
        Assert.True((await services.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(19), End = day.AddHours(21),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Tuesday League"
        })).IsSuccess);
        Assert.True((await services.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[1], Start = day.AddHours(19), End = day.AddHours(21),
            Category = BookingCategory.GroupEvent, State = BookingState.Confirmed, RenterName = "Smith Wedding"
        })).IsSuccess);
        await services.ClubEvents.CreateAsync(new ClubEvent
        {
            Title = "Board Meeting", Category = ClubEventCategory.Meetings, Start = day, End = day, IsAllDay = true
        }, "tester");

        var cut = Render<Calendar>();
        cut.WaitForAssertion(() => Assert.Contains("Tuesday League", cut.Markup));
        Assert.Contains("Smith Wedding", cut.Markup);
        Assert.Contains("Board Meeting", cut.Markup);

        ClickChip(cut, CalendarStyles.CategoryLabel(BookingCategory.League));
        Assert.DoesNotContain("Tuesday League", cut.Markup);
        Assert.Contains("Smith Wedding", cut.Markup);
        Assert.Contains("Board Meeting", cut.Markup);

        ClickChip(cut, CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Meetings));
        Assert.DoesNotContain("Board Meeting", cut.Markup);
        Assert.Contains("Smith Wedding", cut.Markup);
    }
}
