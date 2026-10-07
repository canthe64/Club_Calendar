using AngleSharp.Dom;
using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// DayGrid's club-event band and hour rails. First coverage for this component at all, which is how
/// the bug these pin got in: timed club events were each rendered as their own full-width
/// <c>left:0; right:0</c> absolute overlay inside the hourly grid, so two overlapping ones painted
/// over each other AND over every booking in every sheet column beneath them (live-found
/// 2026-08-27, from a day with a 6 PM orientation running under a 7 PM social).
///
/// Day view's columns are sheets; a club event belongs to no sheet, so it has no honest column to
/// occupy. Timed club events are rows in the band above the grid now, with a thin rail in its own
/// strip carrying when they run - which is what the inline placement (D19) was there to convey.
/// </summary>
public class DayGridClubEventTests : BunitContext
{
    private static readonly DateTime Day = new(2026, 8, 27);

    private IRenderedComponent<DayGrid> RenderGrid(List<ClubEvent> clubEvents, List<SheetBooking>? bookings = null)
    {
        StaffPageServices.Register(this);
        return Render<DayGrid>(p => p
            .Add(g => g.Date, Day)
            .Add(g => g.ClubEvents, clubEvents)
            .Add(g => g.Bookings, bookings ?? []));
    }

    private static ClubEvent Timed(string title, int startHour, int endHour, ClubEventCategory category = ClubEventCategory.Activities) => new()
    {
        Title = title,
        Category = category,
        IsAllDay = false,
        Start = Day.AddHours(startHour),
        End = Day.AddHours(endHour)
    };

    private static SheetBooking Booking(string sheet, string title, int startHour, int endHour) => new()
    {
        SheetMailbox = sheet,
        EventId = Guid.NewGuid().ToString(),
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = title,
        Start = Day.AddHours(startHour),
        End = Day.AddHours(endHour)
    };

    /// <summary>Anything absolutely positioned across the grid's full width - the exact shape the old
    /// overlay used, and the shape that made one club event able to hide another.</summary>
    private static bool IsFullWidthOverlay(IElement el)
    {
        var style = el.GetAttribute("style") ?? "";
        return style.Contains("position:absolute")
            && style.Contains("left:0")
            && style.Contains("right:0");
    }

    [Fact]
    public void TimedClubEvents_AreNeverFullWidthOverlays()
    {
        var cut = RenderGrid([
            Timed("New Member Orientation", 18, 21),
            Timed("UPSTAIRS: Member Social Activity", 19, 21)
        ]);

        Assert.DoesNotContain(cut.FindAll("div"), IsFullWidthOverlay);
    }

    [Fact]
    public void ABookingUnderATimedClubEvent_IsStillRendered()
    {
        // The half of the bug that hid real data: four bookings sat under the band and only showed
        // through because the overlay happened to be at opacity .92.
        var cut = RenderGrid(
            [Timed("New Member Orientation", 18, 21)],
            [Booking(TestFacility.SheetMailboxes[1], "Pebbling Class", 18, 19)]);

        Assert.Contains("Pebbling Class", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("div"), IsFullWidthOverlay);
    }

    [Fact]
    public void ConcurrentTimedClubEvents_GetTheirOwnRailLanes()
    {
        var cut = RenderGrid([
            Timed("New Member Orientation", 18, 21),
            Timed("UPSTAIRS: Member Social Activity", 19, 21)
        ]);

        // Lane 0 sits at the strip's left edge, lane 1 one rail-plus-gap over. Two concurrent events
        // occupying the same offset would mean one rail drawn on top of the other.
        var rails = RailOffsets(cut);
        Assert.Equal(2, rails.Count);
        Assert.Equal(rails.Count, rails.Distinct().Count());
    }

    [Fact]
    public void SequentialTimedClubEvents_ShareOneRailLane()
    {
        // Not concurrent, so there's nothing to sit beside - both belong at the strip's left edge
        // rather than permanently widening the strip for every later event of the day.
        var cut = RenderGrid([
            Timed("Morning Meeting", 9, 10),
            Timed("Evening Social", 19, 21)
        ]);

        Assert.Equal(["0"], RailOffsets(cut).Distinct());
    }

    // The rails are the only absolutely-positioned, non-interactive 3px-radius boxes in the grid;
    // matching on that keeps the assertions independent of the surrounding markup's shape.
    private static List<string> RailStyles(IRenderedComponent<DayGrid> cut) =>
    [
        .. cut.FindAll("div")
            .Select(el => el.GetAttribute("style") ?? "")
            .Where(s => s.Contains("position:absolute") && s.Contains("pointer-events:none") && s.Contains("border-radius:3px"))
    ];

    private static List<string> RailOffsets(IRenderedComponent<DayGrid> cut) =>
        [.. RailStyles(cut).Select(s => s.Split("left:")[1].Split("px")[0])];
}
