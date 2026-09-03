using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The public calendar's own version of MonthGridChipCapTests - staff feedback, 2026-09-03: a day with
/// exactly one item over the visible cap showed a "+1 more" link instead of that one item, for no real
/// space saving. CalendarStylesTests covers the extracted rule (CalendarStyles.VisibleChipCount)
/// directly; these cover AppendDayCell actually applying it to bookings.
/// </summary>
public class PublicCalendarMonthChipCapTests
{
    private static readonly DateTime Day = new(2026, 9, 8);

    private static PublicMonthBooking Booking(string title) =>
        new(title, "League", Day.AddHours(18), Day.AddHours(19), true);

    [Fact]
    public void ExactlyFourBookingsOnADay_RendersAllFourChips_NoMoreLink()
    {
        var bookings = Enumerable.Range(0, 4).Select(i => Booking($"Booking{i}")).ToList();
        var view = new PublicMonthView(bookings, []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        var markup = sb.ToString();
        foreach (var b in bookings)
        {
            Assert.Contains(b.Title, markup);
        }
        Assert.DoesNotContain("more", markup);
    }

    [Fact]
    public void FiveBookingsOnADay_RendersThreeChipsPlusAMoreLinkForTheRemainingTwo()
    {
        var bookings = Enumerable.Range(0, 5).Select(i => Booking($"Booking{i}")).ToList();
        var view = new PublicMonthView(bookings, []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        var markup = sb.ToString();
        Assert.Contains("+2 more", markup);
    }

    [Fact]
    public void TwoClubEventsPlusTwoBookings_FourTotalItems_AllVisible_NoMoreLink()
    {
        // The visible-booking budget is computed as (3 - club event count) per-cell, not against the
        // combined total the way the staff calendar's MonthGrid does it - this pins that the two
        // approaches still agree on the case that matters: exactly one item over a bare "3" cap must
        // never be hidden behind a link.
        var clubEvents = new List<PublicClubEventLabel>
        {
            new("Board Meeting", ClubEventCategory.Meetings, Day, Day, true, false),
            new("Ice Plant Maintenance", ClubEventCategory.Closure, Day, Day, true, true)
        };
        var bookings = new List<PublicMonthBooking> { Booking("League A"), Booking("League B") };
        var view = new PublicMonthView(bookings, clubEvents);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        var markup = sb.ToString();
        Assert.Contains("League A", markup);
        Assert.Contains("League B", markup);
        Assert.DoesNotContain("more", markup);
    }
}
