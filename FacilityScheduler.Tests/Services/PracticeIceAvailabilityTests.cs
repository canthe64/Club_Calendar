using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// PublicAvailabilityService.GetPracticeIceWindowsAsync/FindPracticeIceWindowContainingAsync -
/// the "free of any activity on every sheet, within eligible hours, beyond the lead time, inside
/// the horizon" computation (docs/practice-ice-hosting-design.md §3.1). Every test anchors on a day
/// several days out (never "tomorrow") so results don't depend on what time of day the suite happens
/// to run - a day 5+ out is safely beyond even the largest lead time used here regardless of the
/// current wall-clock time.
/// </summary>
public class PracticeIceAvailabilityTests
{
    private static (PublicAvailabilityService PublicService, SheetBookingService BookingService, ClubEventService ClubEventService, FacilityConfiguration Facility, SchedulingWindowService Window) Build(PracticeIceOptions? practiceIce = null)
    {
        var facility = TestFacility.Create(practiceIce: practiceIce);
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var publicService = new PublicAvailabilityService(bookingService, clubEventService, cache, facility, viewCache, window);
        return (publicService, bookingService, clubEventService, facility, window);
    }

    [Fact]
    public async Task NoActivityAnywhere_WholeEligibleHoursWindowIsOffered()
    {
        var (publicService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        var windows = await publicService.GetPracticeIceWindowsAsync();
        var dayWindows = windows.Where(w => w.Start.Date == day).ToList();

        var window = Assert.Single(dayWindows);
        Assert.Equal(day.AddHours(6), window.Start);
        Assert.Equal(day.AddHours(22), window.End);
    }

    [Fact]
    public async Task ConfirmedBookingOnOneSheet_BlocksThatTimeAcrossEverySheet()
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(10),
            End = day.AddHours(12),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "League Game"
        }, "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();
        var dayWindows = windows.Where(w => w.Start.Date == day).OrderBy(w => w.Start).ToList();

        Assert.Equal(2, dayWindows.Count);
        Assert.Equal((day.AddHours(6), day.AddHours(10)), (dayWindows[0].Start, dayWindows[0].End));
        Assert.Equal((day.AddHours(12), day.AddHours(22)), (dayWindows[1].Start, dayWindows[1].End));
    }

    [Fact]
    public async Task UnsoldGroupEventHold_BlocksJustLikeAConfirmedBooking()
    {
        // The clarification behind this design: group events take priority even as an unsold hold -
        // practice ice is only offered when NOTHING is planned, confirmed or not.
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(10),
            End = day.AddHours(12),
            Category = BookingCategory.GroupEvent,
            State = BookingState.Hold
        }, "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();
        var dayWindows = windows.Where(w => w.Start.Date == day).OrderBy(w => w.Start).ToList();

        Assert.Equal(2, dayWindows.Count);
        Assert.DoesNotContain(dayWindows, w => w.Start < day.AddHours(12) && w.End > day.AddHours(10));
    }

    [Fact]
    public async Task IceBlockingClubEvent_ClosesTheWholeDay()
    {
        var (publicService, _, clubEventService, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "Facility Closure",
            Category = ClubEventCategory.Closure,
            Start = day,
            End = day,
            IsAllDay = true,
            MarksSheetsUnavailable = true
        }, "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();

        Assert.DoesNotContain(windows, w => w.Start.Date == day);
    }

    [Fact]
    public async Task NonBlockingClubEvent_DoesNotAffectAvailability()
    {
        var (publicService, _, clubEventService, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "Bonspiel Announcement",
            Category = ClubEventCategory.OutOfTownBonspiels,
            Start = day,
            End = day,
            IsAllDay = true,
            MarksSheetsUnavailable = false
        }, "tester");

        var windows = await publicService.GetPracticeIceWindowsAsync();
        var dayWindows = windows.Where(w => w.Start.Date == day).ToList();

        var window = Assert.Single(dayWindows);
        Assert.Equal(day.AddHours(6), window.Start);
        Assert.Equal(day.AddHours(22), window.End);
    }

    [Fact]
    public async Task CustomEligibleHours_ClipsToConfiguredWindow()
    {
        var (publicService, _, _, facility, _) = Build(new PracticeIceOptions
        {
            EligibleStartHour = 8,
            EligibleEndHour = 20,
            ApproverDistributionEmail = "approvers@test.onmicrosoft.com",
            MailerMailbox = "mailer@test.onmicrosoft.com"
        });
        var day = facility.Today.AddDays(5);

        var windows = await publicService.GetPracticeIceWindowsAsync();
        var dayWindows = windows.Where(w => w.Start.Date == day).ToList();

        var window = Assert.Single(dayWindows);
        Assert.Equal(day.AddHours(8), window.Start);
        Assert.Equal(day.AddHours(20), window.End);
    }

    [Fact]
    public async Task NoWindowStartsBeforeTheConfiguredLeadTime()
    {
        var (publicService, _, _, facility, _) = Build();
        var before = facility.Now;

        var windows = await publicService.GetPracticeIceWindowsAsync();

        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.True(w.Start >= before.AddHours(facility.PracticeIceMinLeadHours)));
    }

    [Fact]
    public async Task NoWindowExtendsBeyondTheConfiguredHorizon()
    {
        var (publicService, _, _, facility, _) = Build();
        var before = facility.Now;

        var windows = await publicService.GetPracticeIceWindowsAsync();

        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.True(w.End <= before.AddDays(facility.PracticeIceMaxHorizonDays).AddMinutes(1)));
    }

    [Fact]
    public async Task GapShorterThanMinSessionLength_IsNotOffered()
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        // Every sheet booked 6:00-13:00 and 13:30-22:00, leaving exactly a 30-minute gap - below
        // PracticeIceRules.MinSessionMinutes (60), so it shouldn't be offered at all.
        foreach (var sheet in TestFacility.SheetMailboxes)
        {
            await bookingService.CreateConfirmedAsync(new SheetBooking
            {
                SheetMailbox = sheet, Start = day.AddHours(6), End = day.AddHours(13),
                Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Morning"
            }, "tester");
            await bookingService.CreateConfirmedAsync(new SheetBooking
            {
                SheetMailbox = sheet, Start = day.AddHours(13.5), End = day.AddHours(22),
                Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Evening"
            }, "tester");
        }

        var windows = await publicService.GetPracticeIceWindowsAsync();

        Assert.DoesNotContain(windows, w => w.Start.Date == day);
    }

    [Fact]
    public async Task FindPracticeIceWindowContainingAsync_MatchesAnOpenTime_AndMissesTimeOutsideEligibleHours()
    {
        var (publicService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        var found = await publicService.FindPracticeIceWindowContainingAsync(day.AddHours(10));
        Assert.NotNull(found);
        Assert.Equal(day.AddHours(6), found.Start);
        Assert.Equal(day.AddHours(22), found.End);

        var missed = await publicService.FindPracticeIceWindowContainingAsync(day.AddHours(23));
        Assert.Null(missed);
    }
}
