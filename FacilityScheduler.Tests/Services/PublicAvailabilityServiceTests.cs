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

    [Fact]
    public async Task PracticeIceBooking_TitleNamesTheHost()
    {
        // The host is named publicly by design (docs/practice-ice-hosting-design.md §3.6), but
        // never as a bare RenterName the way League/Bonspiel titles work - "Practice Ice" must
        // always be part of the title so the session reads as open-to-everyone, not a private booking.
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = "Jane Curler", RenterEmail = "jane@example.com"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        var booking = Assert.Single(view.Bookings);
        Assert.Equal("Practice Ice - Hosted by Jane Curler", booking.Title);
    }

    [Fact]
    public async Task PracticeIceBooking_NoRenterName_TitleIsJustPracticeIce()
    {
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        var booking = Assert.Single(view.Bookings);
        Assert.Equal("Practice Ice", booking.Title);
    }

    [Fact]
    public async Task PracticeIceBooking_RenterNameLooksLikeAnEmail_TitleNeverExposesTheRawAddress()
    {
        // Guards against a sign-in claim shape that supplies a UPN/email instead of a real display
        // name (live-found 2026-08-09) - the public title must fall back to the safe label rather
        // than publish what looks like an email address.
        var (publicService, bookingService, facility) = Build();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = "jane@example.com"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        var booking = Assert.Single(view.Bookings);
        Assert.Equal("Practice Ice", booking.Title);
    }
}
