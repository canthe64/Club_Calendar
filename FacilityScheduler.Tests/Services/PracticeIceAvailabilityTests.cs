using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// PublicAvailabilityService.GetPracticeIceStartsAsync/GetPracticeIceStartAsync - the times a
/// member can start hosting practice ice: at least PracticeIce:MinOpenSheets (default 3) sheets free
/// for the shortest session, within eligible hours, beyond the lead time, inside the horizon
/// (docs/practice-ice-hosting-design.md §3.1, widened 2026-09-28 to allow sheets already in use and
/// group event holds guests can no longer book). Every test anchors on a day several days out
/// (never "tomorrow") so results don't depend on what time of day the suite runs.
/// </summary>
public class PracticeIceAvailabilityTests
{
    // Five sheets, like the club's - with the default minimum of 3 open, a 3-sheet test facility
    // couldn't show a session running alongside sheets already in use.
    private static readonly string[] SheetLocalParts = ["sheet1", "sheet2", "sheet3", "sheet4", "sheet5"];
    private static readonly string[] Sheets = [.. SheetLocalParts.Select(p => $"{p}@{TestFacility.TenantDomain}")];

    private static (PublicAvailabilityService PublicService, SheetBookingService BookingService, ClubEventService ClubEventService, FacilityConfiguration Facility, SchedulingWindowService Window) Build(PracticeIceOptions? practiceIce = null)
    {
        var h = ServiceHarness.Create(TestFacility.Create(sheetLocalParts: SheetLocalParts, practiceIce: practiceIce));
        return (h.Availability, h.Bookings, h.ClubEvents, h.Facility, h.Window);
    }

    private static Task Book(SheetBookingService bookingService, string sheet, DateTime start, DateTime end,
        BookingCategory category = BookingCategory.League, BookingState state = BookingState.Confirmed) =>
        bookingService.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = end, Category = category, State = state, RenterName = "Existing"
        }, "tester");

    private static List<PracticeIceStartOption> OnDay(List<PracticeIceStartOption> options, DateTime day) =>
        options.Where(o => o.Start.Date == day).OrderBy(o => o.Start).ToList();

    [Fact]
    public async Task NoActivityAnywhere_EveryHalfHourFromEligibleStartToAnHourBeforeEligibleEnd_OnEverySheet()
    {
        var (publicService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        var options = OnDay(await publicService.GetPracticeIceStartsAsync(), day);

        Assert.Equal(day.AddHours(6), options[0].Start);
        Assert.Equal(day.AddHours(21), options[^1].Start); // the last start a 60-minute session still fits
        Assert.Equal(31, options.Count);
        Assert.All(options, o =>
        {
            Assert.Equal(Sheets.Length, o.Sheets.Count);
            Assert.All(o.Sheets, s => Assert.Equal(day.AddHours(22), s.FreeUntil));
        });
    }

    [Fact]
    public async Task BookingOnOneSheet_LeavesTheOtherSheetsOffered()
    {
        // Before 2026-09-28 any booking on any sheet blocked practice ice club-wide.
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        await Book(bookingService, Sheets[0], day.AddHours(10), day.AddHours(12));

        var at10 = await publicService.GetPracticeIceStartAsync(day.AddHours(10));

        Assert.NotNull(at10);
        Assert.Equal(Sheets.Skip(1), at10.Sheets.Select(s => s.SheetMailbox));
    }

    [Fact]
    public async Task TwoSheetsInUse_StillOffered_ThreeSheetsInUse_NotOffered()
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        var sheets = Sheets;
        await Book(bookingService, sheets[0], day.AddHours(10), day.AddHours(12));
        await Book(bookingService, sheets[1], day.AddHours(10), day.AddHours(12));
        await Book(bookingService, sheets[2], day.AddHours(14), day.AddHours(16));
        await Book(bookingService, sheets[3], day.AddHours(14), day.AddHours(16));
        await Book(bookingService, sheets[4], day.AddHours(14), day.AddHours(16));

        Assert.Equal(3, (await publicService.GetPracticeIceStartAsync(day.AddHours(10)))!.Sheets.Count);
        Assert.Null(await publicService.GetPracticeIceStartAsync(day.AddHours(14)));
    }

    [Fact]
    public async Task ConfiguredMinimumOfEverySheet_RestoresTheWholeClubRule()
    {
        var (publicService, bookingService, _, facility, _) = Build(new PracticeIceOptions
        {
            MinOpenSheets = 5,
            ApproverDistributionEmail = "approvers@test.onmicrosoft.com",
            MailerMailbox = "mailer@test.onmicrosoft.com"
        });
        var day = facility.Today.AddDays(5);
        await Book(bookingService, Sheets[0], day.AddHours(10), day.AddHours(12));

        Assert.Null(await publicService.GetPracticeIceStartAsync(day.AddHours(10)));
        Assert.NotNull(await publicService.GetPracticeIceStartAsync(day.AddHours(12)));
    }

    [Fact]
    public async Task SheetFreeFor_LessThanTheShortestSession_IsLeftOutOfThatStart()
    {
        // Sheet 1 is free 10:00-10:30 only - it can't take part in any session starting at 10:00.
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        await Book(bookingService, Sheets[0], day.AddHours(10.5), day.AddHours(13));

        var at10 = await publicService.GetPracticeIceStartAsync(day.AddHours(10));

        Assert.DoesNotContain(at10!.Sheets, s => s.SheetMailbox == Sheets[0]);
    }

    [Fact]
    public async Task StartOption_LongerSessionsCoverFewerSheets_AndStopAtTheMinimum()
    {
        // Free until: sheet1 11:00, sheet2 12:00, sheets 3-5 22:00.
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        var sheets = Sheets;
        await Book(bookingService, sheets[0], day.AddHours(11), day.AddHours(22));
        await Book(bookingService, sheets[1], day.AddHours(12), day.AddHours(22));

        var at10 = (await publicService.GetPracticeIceStartAsync(day.AddHours(10)))!;

        Assert.Equal(5, at10.SheetsFor(60).Count);
        Assert.Equal(4, at10.SheetsFor(120).Count);
        Assert.Equal(3, at10.SheetsFor(180).Count);
        Assert.Equal(12 * 60, at10.MaxDurationMinutes(minSheets: 3)); // 10:00-22:00 on sheets 3-5
        Assert.Equal(120, at10.MaxDurationMinutes(minSheets: 4));
    }

    [Fact]
    public async Task GroupEventHold_InsideTheReleaseWindow_CountsAsFree()
    {
        // Guests book group events at least a week out, so a hold inside that week can't sell.
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        await Book(bookingService, Sheets[0], day.AddHours(10), day.AddHours(12), BookingCategory.GroupEvent, BookingState.Hold);

        var at10 = await publicService.GetPracticeIceStartAsync(day.AddHours(10));

        Assert.Equal(Sheets.Length, at10!.Sheets.Count);
    }

    [Fact]
    public async Task GroupEventHold_BeyondTheReleaseWindow_StillBlocksItsSheet()
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(10);
        await Book(bookingService, Sheets[0], day.AddHours(10), day.AddHours(12), BookingCategory.GroupEvent, BookingState.Hold);

        var at10 = await publicService.GetPracticeIceStartAsync(day.AddHours(10));

        Assert.DoesNotContain(at10!.Sheets, s => s.SheetMailbox == Sheets[0]);
    }

    [Theory]
    [InlineData(BookingCategory.GroupEvent, BookingState.Confirmed)] // a sold group event
    [InlineData(BookingCategory.PracticeIce, BookingState.Hold)]    // another member's pending request
    public async Task EveryOtherBookingInsideTheReleaseWindow_StillBlocksItsSheet(BookingCategory category, BookingState state)
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);
        await Book(bookingService, Sheets[0], day.AddHours(10), day.AddHours(12), category, state);

        var at10 = await publicService.GetPracticeIceStartAsync(day.AddHours(10));

        Assert.DoesNotContain(at10!.Sheets, s => s.SheetMailbox == Sheets[0]);
    }

    [Fact]
    public void MemberFreeTime_HoldStraddlingTheReleaseCutoff_IsFreeOnlyUpToTheCutoff()
    {
        var day = new DateTime(2026, 10, 1);
        var cutoff = day.AddHours(11);
        var hold = new SheetBooking
        {
            SheetMailbox = "sheet1@x", Start = day.AddHours(10), End = day.AddHours(13),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        };

        var free = PublicAvailabilityService.MemberFreeTime(["sheet1@x"], [hold], [], day, day.AddDays(1), cutoff);

        Assert.Equal([(day, day.AddHours(11)), (day.AddHours(13), day.AddDays(1))], free["sheet1@x"]);
    }

    [Fact]
    public async Task IceBlockingClubEvent_ClosesTheWholeDay()
    {
        var (publicService, _, clubEventService, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "Facility Closure", Category = ClubEventCategory.Closure,
            Start = day, End = day, IsAllDay = true, MarksSheetsUnavailable = true
        }, "tester");

        Assert.Empty(OnDay(await publicService.GetPracticeIceStartsAsync(), day));
    }

    [Fact]
    public async Task NonBlockingClubEvent_DoesNotAffectAvailability()
    {
        var (publicService, _, clubEventService, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        await clubEventService.CreateAsync(new ClubEvent
        {
            Title = "Bonspiel Announcement", Category = ClubEventCategory.OutOfTownBonspiels,
            Start = day, End = day, IsAllDay = true, MarksSheetsUnavailable = false
        }, "tester");

        Assert.Equal(31, OnDay(await publicService.GetPracticeIceStartsAsync(), day).Count);
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

        var options = OnDay(await publicService.GetPracticeIceStartsAsync(), day);

        Assert.Equal(day.AddHours(8), options[0].Start);
        Assert.Equal(day.AddHours(19), options[^1].Start);
        Assert.All(options[0].Sheets, s => Assert.Equal(day.AddHours(20), s.FreeUntil));
    }

    [Fact]
    public async Task NoStartBeforeTheConfiguredLeadTime()
    {
        var (publicService, _, _, facility, _) = Build();
        var before = facility.Now;

        var options = await publicService.GetPracticeIceStartsAsync();

        Assert.NotEmpty(options);
        Assert.All(options, o => Assert.True(o.Start >= before.AddHours(facility.PracticeIceMinLeadHours)));
    }

    [Fact]
    public async Task NothingExtendsBeyondTheConfiguredHorizon()
    {
        var (publicService, _, _, facility, _) = Build();
        var before = facility.Now;

        var options = await publicService.GetPracticeIceStartsAsync();

        Assert.NotEmpty(options);
        Assert.All(options.SelectMany(o => o.Sheets), s => Assert.True(s.FreeUntil <= before.AddDays(facility.PracticeIceMaxHorizonDays).AddMinutes(1)));
    }

    [Fact]
    public async Task GapShorterThanMinSessionLength_IsNotOffered()
    {
        var (publicService, bookingService, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        // Every sheet booked 6:00-13:00 and 13:30-22:00, leaving exactly a 30-minute gap - below
        // PracticeIceRules.MinSessionMinutes (60), so it shouldn't be offered at all.
        foreach (var sheet in Sheets)
        {
            await Book(bookingService, sheet, day.AddHours(6), day.AddHours(13));
            await Book(bookingService, sheet, day.AddHours(13.5), day.AddHours(22));
        }

        Assert.Empty(OnDay(await publicService.GetPracticeIceStartsAsync(), day));
    }

    [Fact]
    public async Task GetPracticeIceStartAsync_OffGridOrOutsideEligibleHours_IsNull()
    {
        var (publicService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        Assert.NotNull(await publicService.GetPracticeIceStartAsync(day.AddHours(10)));
        Assert.Null(await publicService.GetPracticeIceStartAsync(day.AddHours(10).AddMinutes(15)));
        Assert.Null(await publicService.GetPracticeIceStartAsync(day.AddHours(23)));
    }
}
