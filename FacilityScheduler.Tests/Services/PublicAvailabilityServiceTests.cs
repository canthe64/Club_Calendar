using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

public class PublicAvailabilityServiceTests
{
    private static (PublicAvailabilityService PublicService, SheetBookingService BookingService, FacilityConfiguration Facility, SchedulingWindowService Window) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var publicService = new PublicAvailabilityService(bookingService, clubEventService, cache, facility, viewCache, window);
        return (publicService, bookingService, facility, window);
    }

    [Fact]
    public async Task SingleSheetBooking_TitleHasNoSheetCountSuffix()
    {
        var (publicService, bookingService, facility, _) = Build();
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
        var (publicService, bookingService, facility, _) = Build();
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
        var (publicService, bookingService, facility, _) = Build();
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
        var (publicService, bookingService, facility, _) = Build();
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
        var (publicService, bookingService, facility, _) = Build();
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
        var (publicService, bookingService, facility, _) = Build();
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

    // --- Publish cutoff: /public/calendar + the JSON widget only, not search or practice ice ---

    [Fact]
    public async Task Cutoff_HidesABookingFromTheCalendarViewAfterTheCutoffDate()
    {
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(10);
        await window.SetPublicCutoffAsync(facility.Today.AddDays(5), "tester");

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Late Season Game"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Empty(view.Bookings);
    }

    [Fact]
    public async Task Cutoff_ABookingStartingExactlyOnTheCutoffDate_StillShows()
    {
        // The operator's straddling-event rule: visible if it starts on or before the cutoff.
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(5);
        await window.SetPublicCutoffAsync(day, "tester");

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "On The Cutoff"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Single(view.Bookings);
    }

    [Fact]
    public async Task Cutoff_HidesAnOpenSlotFromTheJsonWidget()
    {
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(10);
        await window.SetPublicCutoffAsync(facility.Today.AddDays(5), "tester");

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(20),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        var response = await publicService.GetAvailabilityAsync(requestedDays: 30);

        Assert.Empty(response.SheetSlots);
    }

    [Fact]
    public async Task Cutoff_DoesNotAffectSearch()
    {
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(10);
        await window.SetPublicCutoffAsync(facility.Today.AddDays(5), "tester");

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(20),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        var windows = await publicService.GetConcurrentAvailabilityAsync(facility.Today, day.AddDays(1), minSheets: 1);

        Assert.NotEmpty(windows);
    }

    [Fact]
    public async Task Cutoff_DoesNotAffectPracticeIce()
    {
        var (publicService, _, facility, window) = Build();
        await window.SetPublicCutoffAsync(facility.Today.AddDays(1), "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();

        // Default facility has no bookings at all - practice ice should offer plenty of windows
        // well past the cutoff, since the cutoff doesn't apply to this surface.
        Assert.Contains(windows, w => w.Start.Date > facility.Today.AddDays(1));
    }

    // --- Season window: search + JSON widget + practice ice, not the calendar view ---

    [Fact]
    public async Task Season_HidesAnOpenSlotBeforeTheSeasonStarts()
    {
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(5);
        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(20),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        var response = await publicService.GetAvailabilityAsync(requestedDays: 30);

        Assert.Empty(response.SheetSlots);
    }

    [Fact]
    public async Task Season_HidesAnOpenSlotFromSearchToo()
    {
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(5);
        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(20),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        var windows = await publicService.GetConcurrentAvailabilityAsync(facility.Today, day.AddDays(1), minSheets: 1);

        Assert.Empty(windows);
    }

    [Fact]
    public async Task Season_DoesNotAffectTheCalendarView_AlreadyExistingBookingsStillShow()
    {
        // Once season enforcement is live, nothing new can be created off-season anyway - this
        // covers a booking that predates the season being configured at all.
        var (publicService, bookingService, facility, window) = Build();
        var day = facility.Today.AddDays(5);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(19),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Pre-existing"
        }, "tester");

        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Single(view.Bookings);
    }

    [Fact]
    public async Task Season_ClipsThePracticeIceWindowListing()
    {
        var (publicService, _, facility, window) = Build();
        var seasonEnd = facility.Today.AddDays(10);
        await window.SetSeasonWindowAsync(null, seasonEnd, "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();

        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.True(w.End <= seasonEnd.Date.AddDays(1)));
    }

    [Fact]
    public async Task Season_UnconfiguredMeansNoRestriction()
    {
        var (publicService, bookingService, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(18), End = day.AddHours(20),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        var response = await publicService.GetAvailabilityAsync(requestedDays: 30);

        Assert.Single(response.SheetSlots);
    }

    // ---- Public Notes exposure (D108, staff feedback 2026-08-27) ----------------------------

    private static (PublicAvailabilityService PublicService, SheetBookingService BookingService, ClubEventService ClubEventService, FacilityConfiguration Facility) BuildWithClubEvents()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var publicService = new PublicAvailabilityService(bookingService, clubEventService, cache, facility, viewCache, window);
        return (publicService, bookingService, clubEventService, facility);
    }

    [Fact]
    public async Task StaffTypedBookingNote_IsIncludedPublicly()
    {
        var (publicService, bookingService, _, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League",
            Notes = "Bring your own broom - house brooms unavailable tonight."
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Equal("Bring your own broom - house brooms unavailable tonight.", Assert.Single(view.Bookings).Notes);
    }

    [Fact]
    public async Task BreelyOriginatedBookingNote_IsNeverExposedPublicly()
    {
        // The exact case D108 was written to guard: BuildNotes' fixed template (never staff-reviewed)
        // must never reach the public calendar, same reasoning and same signal (ExternalBookingId)
        // already used to suppress the real customer name in the title (D52).
        var (publicService, bookingService, _, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);

        await bookingService.ForceCreateConfirmedAsync(TestFacility.SheetMailboxes[0], new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.GroupEvent,
            State = BookingState.Confirmed,
            RenterName = "Jane Customer",
            Notes = "Booked via Breely (Try Curling Group Reservation). Admin: https://breely.example/admin/123",
            ExternalBookingId = "breely:123"
        });

        var view = await publicService.GetDayViewAsync(day);

        Assert.Null(Assert.Single(view.Bookings).Notes);
    }

    [Fact]
    public async Task StaffTypedClubEventNote_IsIncludedPublicly()
    {
        var (publicService, _, clubEventService, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "East Lot Closure",
            Category = ClubEventCategory.Closure,
            Start = day,
            End = day,
            IsAllDay = true,
            Notes = "East parking lot closed for resurfacing - use the main lot entrance.",
            BookedBy = "staff@example.com"
        }, "staff@example.com");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Equal("East parking lot closed for resurfacing - use the main lot entrance.", Assert.Single(view.ClubEvents).Notes);
    }

    [Fact]
    public async Task BreelyTriageMarkerNote_IsNeverExposedPublicly()
    {
        // The concrete leak this gate exists to close: FlagNeedsTriageAsync's "⚠ Web booking needs
        // review" marker embeds the customer's real name (Breely's client_full_name) plus an internal
        // admin URL directly in Notes - reached through a Club Event, not a booking, so it has no
        // ExternalBookingId to key off; BookedBy is the only reliable "the webhook wrote this, no
        // staff reviewed it" signal ClubEvent carries.
        var (publicService, _, clubEventService, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "⚠ Web booking needs review",
            Category = ClubEventCategory.Other,
            Start = day,
            End = day,
            IsAllDay = true,
            Notes = "Breely booking abc123 (Jane Customer, 6:00PM-7:00PM) didn't match any open hold on any sheet - booked directly onto Sheet 1. Verify manually and reassign if needed. Admin: https://breely.example/admin/abc123",
            BookedBy = BreelyBookingProcessor.BookedByLabel
        }, BreelyBookingProcessor.BookedByLabel);

        var view = await publicService.GetDayViewAsync(day);

        Assert.Null(Assert.Single(view.ClubEvents).Notes);
    }

    [Fact]
    public async Task BlankNote_StaysNull_NotAnEmptyString()
    {
        var (publicService, bookingService, _, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League"
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        Assert.Null(Assert.Single(view.Bookings).Notes);
    }

    [Fact]
    public async Task LongNote_IsTruncatedRatherThanShownInFull()
    {
        var (publicService, bookingService, _, facility) = BuildWithClubEvents();
        var day = facility.Today.AddDays(1);
        var longNote = new string('x', 400);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League",
            Notes = longNote
        }, "tester");

        var view = await publicService.GetDayViewAsync(day);

        var publicNote = Assert.Single(view.Bookings).Notes;
        Assert.NotNull(publicNote);
        Assert.True(publicNote!.Length < longNote.Length, "a 400-char note must be shortened, not shown in full");
    }
}
