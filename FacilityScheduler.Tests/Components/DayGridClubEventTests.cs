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
    public void TwoOverlappingTimedClubEvents_BothRenderTheirOwnTitle()
    {
        var cut = RenderGrid([
            Timed("New Member Orientation", 18, 21),
            Timed("UPSTAIRS: Member Social Activity", 19, 21)
        ]);

        Assert.Contains("New Member Orientation", cut.Markup);
        Assert.Contains("UPSTAIRS: Member Social Activity", cut.Markup);
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

    [Fact]
    public void AllDayAndTimedClubEvents_ShareTheOneBand()
    {
        var allDay = new ClubEvent
        {
            Title = "Fall Bonspiel",
            Category = ClubEventCategory.Competitions,
            IsAllDay = true,
            Start = Day,
            End = Day
        };

        var cut = RenderGrid([allDay, Timed("Evening Social", 19, 21)]);

        Assert.Contains("Fall Bonspiel", cut.Markup);
        Assert.Contains("Evening Social", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARailIsAlwaysItsBandRowsColour(bool marksSheetsUnavailable)
    {
        // The rail's only job is to point at one row of the band, so any colour it doesn't share with
        // that row breaks the tie. A closure is the case that nearly got this wrong: the deleted
        // overlay painted itself red (#a02c21) while the band row it replaced stayed the category
        // colour, and the red came along in the first draft of the rail.
        var ce = Timed("Ice Plant Maintenance", 18, 21, ClubEventCategory.Closure);
        ce.MarksSheetsUnavailable = marksSheetsUnavailable;
        var cut = RenderGrid([ce]);

        var expected = CalendarStyles.ClubEventCategoryColor(ClubEventCategory.Closure);
        Assert.Single(RailFills(cut));
        Assert.Equal(expected, RailFills(cut)[0]);
        Assert.Contains($"background:{expected}", cut.Markup);
    }

    [Fact]
    public void NoTimedClubEvents_RendersNoRailStrip()
    {
        // The strip is 22px of horizontal space; an ordinary day with nothing off-ice shouldn't pay
        // for it.
        var cut = RenderGrid([]);

        Assert.Empty(RailOffsets(cut));
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

    private static List<string> RailFills(IRenderedComponent<DayGrid> cut) =>
        [.. RailStyles(cut).Select(s => s.Split("background:")[1].Split(';')[0].Trim())];
}
