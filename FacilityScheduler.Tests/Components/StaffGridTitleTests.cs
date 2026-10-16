using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>Every staff calendar view titles a booking through CalendarStyles.BookingDisplayTitle.
/// Day view used to keep its own copy of the rule, which would have kept showing a member's
/// practice ice request as the bare host name after the shared rule was fixed (2026-10-09).</summary>
public class StaffGridTitleTests : BunitContext
{
    private static readonly DateTime Day = new(2026, 10, 15); // a Thursday

    private static readonly SheetBooking MemberRequest = new()
    {
        SheetMailbox = TestFacility.SheetMailboxes[0],
        EventId = "evt-1",
        BookingGroupId = Guid.NewGuid(),
        Category = BookingCategory.PracticeIce,
        State = BookingState.Confirmed,
        RenterName = "David Donnan (Guest)",
        RenterEmail = "david@example.com",
        Start = Day.AddHours(7.5),
        End = Day.AddHours(9)
    };

    [Theory]
    [InlineData("month")]
    [InlineData("week")]
    [InlineData("day")]
    public void AMemberPracticeIceRequest_IsTitledWithItsHost(string view)
    {
        StaffPageServices.Register(this);
        List<SheetBooking> bookings = [MemberRequest];

        string markup = view switch
        {
            "month" => Render<MonthGrid>(p => p.Add(g => g.AnchorMonth, Day).Add(g => g.Bookings, bookings).Add(g => g.ClubEvents, [])).Markup,
            "week" => Render<WeekGrid>(p => p.Add(g => g.WeekStart, Day.AddDays(-3)).Add(g => g.Bookings, bookings)).Markup,
            _ => Render<DayGrid>(p => p.Add(g => g.Date, Day).Add(g => g.Bookings, bookings).Add(g => g.ClubEvents, [])).Markup,
        };

        Assert.Contains("Practice Ice Hosted by David Donnan (Guest)", markup);
    }
}
