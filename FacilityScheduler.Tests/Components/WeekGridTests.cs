using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Components;

public class WeekGridTests : BunitContext
{
    private static readonly DateTime WeekStart = new(2026, 8, 17); // a Monday

    [Fact]
    public void ClickingAnEmptySlot_FiresOnSlotClickWithTheSlotsDateAndHour_NotOnDayClick()
    {
        DateTime? slotClicked = null;
        var dayClicked = false;
        var cut = Render<WeekGrid>(p => p
            .Add(g => g.WeekStart, WeekStart)
            .Add(g => g.OnSlotClick, (DateTime s) => slotClicked = s)
            .Add(g => g.OnDayClick, (DateTime _) => dayClicked = true));

        // Rendering order matches CalendarStyles.HourRows (0..23) nested inside the day loop, so the
        // 10th empty-slot div (index 9) in Monday's column is the 9am slot.
        var emptySlots = cut.FindAll("div[style*='background:#f6f8f9']");
        emptySlots[9].Click();

        Assert.Equal(WeekStart.Date.AddHours(9), slotClicked);
        Assert.False(dayClicked);
    }

    [Fact]
    public void ClickingTheDayHeader_FiresOnDayClick_NotOnSlotClick()
    {
        DateTime? dayClicked = null;
        var slotClicked = false;
        var cut = Render<WeekGrid>(p => p
            .Add(g => g.WeekStart, WeekStart)
            .Add(g => g.OnSlotClick, (DateTime _) => slotClicked = true)
            .Add(g => g.OnDayClick, (DateTime d) => dayClicked = d));

        cut.FindAll("div").Single(e => e.TextContent.Trim() == "Mon").ParentElement!.Click();

        Assert.Equal(WeekStart.Date, dayClicked);
        Assert.False(slotClicked);
    }

    [Fact]
    public void ClickingABooking_FiresOnBookingClick_NotOnSlotClick()
    {
        var booking = new SheetBooking
        {
            SheetMailbox = "sheet1@club.test",
            Start = WeekStart.AddHours(9),
            End = WeekStart.AddHours(10),
            Category = BookingCategory.Other,
            State = BookingState.Confirmed,
            RenterName = "Test Renter",
        };
        SheetBooking? bookingClicked = null;
        var slotClicked = false;
        var cut = Render<WeekGrid>(p => p
            .Add(g => g.WeekStart, WeekStart)
            .Add(g => g.Bookings, [booking])
            .Add(g => g.OnBookingClick, (SheetBooking b) => bookingClicked = b)
            .Add(g => g.OnSlotClick, (DateTime _) => slotClicked = true));

        // The outer (onclick-bearing) chip div and its inner text div both contain the renter's name
        // in their TextContent - Last() picks the innermost (it appears later in document order),
        // then ParentElement steps back out to the div carrying @onclick.
        cut.FindAll("div").Where(e => e.TextContent.Contains("Test Renter")).Last().ParentElement!.Click();

        Assert.Equal(booking, bookingClicked);
        Assert.False(slotClicked);
    }
}
