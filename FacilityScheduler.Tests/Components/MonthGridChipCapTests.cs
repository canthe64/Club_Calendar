using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// Staff feedback, 2026-09-03: a Month-view day with exactly 4 items showed 3 chips plus a "+1 more"
/// link, even though the link costs the same vertical space in the cell the 4th chip itself would
/// have - clicking it just revealed one thing, for no real saving. CalendarStylesTests covers the
/// extracted rule (CalendarStyles.VisibleChipCount) directly; these tests cover MonthGrid actually
/// applying it end to end.
/// </summary>
public class MonthGridChipCapTests : BunitContext
{
    private static readonly DateTime Day = new(2026, 9, 8); // a Tuesday, safely mid-month either way

    private IRenderedComponent<MonthGrid> RenderGrid(List<SheetBooking> bookings)
    {
        StaffPageServices.Register(this);
        return Render<MonthGrid>(p => p
            .Add(g => g.AnchorMonth, Day)
            .Add(g => g.Bookings, bookings)
            .Add(g => g.ClubEvents, []));
    }

    private static SheetBooking Booking(string title, int hour) => new()
    {
        SheetMailbox = TestFacility.SheetMailboxes[0],
        EventId = Guid.NewGuid().ToString(),
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = title,
        Start = Day.AddHours(hour),
        End = Day.AddHours(hour + 1)
    };

    [Fact]
    public void ExactlyFourItemsOnADay_ShowsAllFourChips_NoMoreLink()
    {
        var bookings = Enumerable.Range(0, 4).Select(i => Booking($"Booking{i}", 8 + i)).ToList();

        var cut = RenderGrid(bookings);

        foreach (var b in bookings)
        {
            Assert.Contains(b.RenterName!, cut.Markup);
        }
        Assert.DoesNotContain("more", cut.Markup);
    }

    [Fact]
    public void FiveItemsOnADay_ShowsThreeChipsPlusAMoreLinkForTheRemainingTwo()
    {
        var bookings = Enumerable.Range(0, 5).Select(i => Booking($"Booking{i}", 8 + i)).ToList();

        var cut = RenderGrid(bookings);

        Assert.Contains("Booking0", cut.Markup);
        Assert.Contains("Booking1", cut.Markup);
        Assert.Contains("Booking2", cut.Markup);
        Assert.DoesNotContain("Booking3", cut.Markup);
        Assert.DoesNotContain("Booking4", cut.Markup);
        Assert.Contains("+2 more", cut.Markup);
    }

    [Fact]
    public void ClickingMore_OnFiveItems_RevealsEveryRemainingChip()
    {
        var bookings = Enumerable.Range(0, 5).Select(i => Booking($"Booking{i}", 8 + i)).ToList();
        var cut = RenderGrid(bookings);

        // The day-cell div itself also "contains" the word "more" via its descendant's TextContent -
        // match the link's own exact text instead of an ancestor that happens to include it too.
        cut.FindAll("div").First(el => el.TextContent.Trim() == "+2 more").Click();

        foreach (var b in bookings)
        {
            Assert.Contains(b.RenterName!, cut.Markup);
        }
        Assert.DoesNotContain("more", cut.Markup);
    }

    [Fact]
    public void ExactlyThreeItemsOnADay_ShowsAllThree_NoMoreLink()
    {
        // The at-the-cap boundary - never needed a link either, before or after this fix.
        var bookings = Enumerable.Range(0, 3).Select(i => Booking($"Booking{i}", 8 + i)).ToList();

        var cut = RenderGrid(bookings);

        foreach (var b in bookings)
        {
            Assert.Contains(b.RenterName!, cut.Markup);
        }
        Assert.DoesNotContain("more", cut.Markup);
    }
}
