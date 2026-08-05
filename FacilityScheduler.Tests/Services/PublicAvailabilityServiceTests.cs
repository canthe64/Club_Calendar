using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

public class PublicAvailabilityServiceTests
{
    private static (PublicAvailabilityService PublicService, SheetBookingService BookingService, FacilityConfiguration Facility) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog);
        var publicService = new PublicAvailabilityService(bookingService, clubEventService, cache, facility);
        return (publicService, bookingService, facility);
    }

    [Fact]
    public async Task SingleSheetBooking_TitleHasNoSheetCountSuffix()
    {
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Solo League"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        var booking = Assert.Single(view.Bookings);
        Assert.Equal("Solo League", booking.Title);
    }

    [Fact]
    public async Task MultiSheetBooking_ShowsSheetCountSuffixExactlyOnce()
    {
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);
        var sheets = TestFacility.SheetMailboxes; // 3 sheets

        var result = await bookingService.CreateAcrossSheetsAsync(sheets, new SheetBooking
        {
            SheetMailbox = "",
            Start = day.AddHours(10),
            End = day.AddHours(12),
            Category = BookingCategory.Bonspiel,
            State = BookingState.Confirmed,
            RenterName = "Big Bonspiel"
        }, "tester");
        Assert.True(result.IsSuccess);

        var view = await publicService.GetDayViewAsync(day);

        var booking = Assert.Single(view.Bookings); // not one chip per sheet
        Assert.Equal($"Big Bonspiel · {sheets.Length} sheets", booking.Title);
    }

    [Fact]
    public async Task DifferentUnrelatedBookings_AreNotMergedTogether()
    {
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);
        var sheets = TestFacility.SheetMailboxes;

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheets[0], Start = day.AddHours(9), End = day.AddHours(10),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Team A"
        }, "tester");
        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheets[1], Start = day.AddHours(14), End = day.AddHours(15),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Team B"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Equal(2, view.Bookings.Count);
        Assert.Contains(view.Bookings, b => b.Title == "Team A");
        Assert.Contains(view.Bookings, b => b.Title == "Team B");
    }
}
