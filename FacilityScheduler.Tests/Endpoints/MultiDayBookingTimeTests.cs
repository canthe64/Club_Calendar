using System.Text;
using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>A booking that runs over several days shows both its start and end date wherever its
/// time is spelled out - the public calendar's pop-up (month and week/day paths) and the staff
/// booking details. Live-found 2026-10-21: "Friday, Mar 12 · 9:00AM-3:00PM" for a bonspiel that
/// ended on the Sunday.</summary>
public class MultiDayBookingTimeTests : BunitContext
{
    private static readonly DateTime Friday = new(2027, 3, 12);
    private static readonly DateTime Start = Friday.AddHours(9);
    private static readonly DateTime End = Friday.AddDays(2).AddHours(15);
    private const string Expected = "Friday, Mar 12, 9:00AM - Sunday, Mar 14, 3:00PM";

    private static readonly PublicMonthView View =
        new([new PublicMonthBooking("Open Doubles Spiel", "Bonspiel", Start, End, true, null)], []);

    [Fact]
    public void PublicMonthCell_PopupTimeShowsBothDates()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Friday, Friday, View);

        Assert.Contains($"data-time=\"{Expected}\"", sb.ToString());
    }

    [Fact]
    public void PublicWeekColumn_PopupTimeShowsBothDates()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayColumn(sb, Friday, View, 0, showHeader: true, isMultiDay: true);

        Assert.Contains($"data-time=\"{Expected}\"", sb.ToString());
    }

    [Fact]
    public void StaffBookingDetails_ShowBothDates()
    {
        var cut = Render<BookingDetailModal>(p => p.Add(m => m.Group, new List<SheetBooking>
        {
            new()
            {
                SheetMailbox = "sheet1@example.com", EventId = "evt-1", Category = BookingCategory.Bonspiel,
                State = BookingState.Confirmed, RenterName = "Open Doubles Spiel", Start = Start, End = End
            }
        }));

        Assert.Contains(Expected, cut.Markup);
    }
}
