using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

public class PracticeIceRequestServiceTests
{
    private const string HostName = "Jane Curler";
    private const string HostEmail = "jane@example.com";

    private static (PracticeIceRequestService RequestService, SheetBookingService BookingService, PublicAvailabilityService Availability, FacilityConfiguration Facility, FakeGraphMailGateway Mail, SchedulingWindowService Window)
        Build(PracticeIceOptions? practiceIce = null) => Build(out _, practiceIce);

    private static (PracticeIceRequestService RequestService, SheetBookingService BookingService, PublicAvailabilityService Availability, FacilityConfiguration Facility, FakeGraphMailGateway Mail, SchedulingWindowService Window)
        Build(out FakeGraphEventGateway gateway, PracticeIceOptions? practiceIce = null, string[]? sheetLocalParts = null)
    {
        var facility = TestFacility.Create(sheetLocalParts: sheetLocalParts, practiceIce: practiceIce);
        gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var availability = new PublicAvailabilityService(bookingService, clubEventService, cache, facility, viewCache, window);
        var mail = new FakeGraphMailGateway();
        var requestService = new PracticeIceRequestService(bookingService, availability, mail, facility, appLog,
            new MemberBookingCancellationService(bookingService, mail, facility, appLog));
        return (requestService, bookingService, availability, facility, mail, window);
    }

    [Fact]
    public async Task Submit_MailNotConfigured_IsBlockedAndCreatesNothing()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build(practiceIce: new PracticeIceOptions());
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
        Assert.NotNull(result.Message);
        Assert.Empty(mail.Sent);
        Assert.Empty(await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1)));
    }

    [Fact]
    public async Task Submit_NotCertified_IsRejected()
    {
        var (requestService, _, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: false, notes: null);

        Assert.False(result.IsSuccess);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Submit_MissingHostEmail_IsRejected()
    {
        var (requestService, _, _, facility, _, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, "", certified: true, notes: null);

        Assert.False(result.IsSuccess);
    }

    // ---- Per-member pending-request cap (code review S6) ---------------------------------------

    private static PracticeIceOptions MailConfiguredOptions(int maxPendingRequestsPerMember) => new()
    {
        ApproverDistributionEmail = "approvers@test.onmicrosoft.com",
        MailerMailbox = "mailer@test.onmicrosoft.com",
        MaxPendingRequestsPerMember = maxPendingRequestsPerMember
    };

    [Fact]
    public async Task Submit_AtTheCap_IsRejectedWithAnExplicitMessage_AndWritesNothing()
    {
        // Every successful submission writes a hold across every sheet with no auto-expiration
        // (§2.2) - without a cap, one member could accumulate an unbounded number of them.
        var (requestService, _, _, facility, _, _) = Build(practiceIce: MailConfiguredOptions(1));
        var day = facility.Today.AddDays(5);
        var first = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);
        Assert.True(first.IsSuccess);

        var second = await requestService.SubmitAsync(day.AddHours(14), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(second.IsSuccess);
        Assert.False(second.IsConflict);
        Assert.Contains("pending request", second.Message);
        var pending = await requestService.GetPendingAsync();
        Assert.Single(pending); // the rejected submission didn't write a second hold
    }

    [Fact]
    public async Task Submit_AtTheCap_DoesNotBlockADifferentHost()
    {
        var (requestService, _, _, facility, _, _) = Build(practiceIce: MailConfiguredOptions(1));
        var day = facility.Today.AddDays(5);
        await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        var otherHost = await requestService.SubmitAsync(day.AddHours(14), 60, "Other Host", "other@example.com", certified: true, notes: null);

        Assert.True(otherHost.IsSuccess);
    }

    [Fact]
    public async Task Submit_BelowTheCap_Succeeds()
    {
        var (requestService, _, _, facility, _, _) = Build(practiceIce: MailConfiguredOptions(2));
        var day = facility.Today.AddDays(5);
        await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        var second = await requestService.SubmitAsync(day.AddHours(14), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task Submit_StartOutsideEligibleHours_IsRejected()
    {
        var (requestService, _, _, facility, _, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(23), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_DurationLongerThanTheOpenWindow_IsRejected()
    {
        var (requestService, _, _, facility, _, _) = Build();
        var day = facility.Today.AddDays(5);

        // The default eligible window is 6:00-22:00 (960 minutes) - 990 doesn't fit.
        var result = await requestService.SubmitAsync(day.AddHours(6), 990, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_NotesLongerThanTheCap_IsRejectedBeforeAnythingIsWritten()
    {
        // The one free-text field a non-staff member can write into the system of record. Unbounded,
        // it can overflow the extended-property blob and fail the Graph write partway through a
        // five-sheet create - rejecting up front is both a better message and avoids that path.
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail,
            certified: true, notes: new string('x', PracticeIceRules.MaxNotesLength + 1));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
        Assert.Empty(mail.Sent);
        Assert.Empty(await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1)));
    }

    [Fact]
    public async Task Submit_NotesExactlyAtTheCap_IsAccepted()
    {
        var (requestService, _, _, facility, _, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail,
            certified: true, notes: new string('x', PracticeIceRules.MaxNotesLength));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Submit_InvalidatesThePublicWindowCache_SoTheSlotStopsBeingOffered()
    {
        // The public cache used to expire on TTL only, on the reasoning that nothing wrote through
        // it - practice ice made that false, so a just-claimed slot kept showing as free.
        var (requestService, _, availability, facility, _, _) = Build();
        var day = facility.Today.AddDays(5);
        var start = day.AddHours(10);

        // Prime the cache while the slot is genuinely free.
        Assert.NotNull(await availability.GetPracticeIceStartAsync(start));

        Assert.True((await requestService.SubmitAsync(start, 60, HostName, HostEmail, certified: true, notes: null)).IsSuccess);

        // Without invalidation this still returns the pre-submit option at 10:00.
        Assert.Null(await availability.GetPracticeIceStartAsync(start));
    }

    [Fact]
    public async Task Submit_Success_CreatesHoldAcrossEverySheetAndNotifiesApprovers()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5);
        var start = day.AddHours(10);

        var result = await requestService.SubmitAsync(start, 60, HostName, HostEmail, certified: true, notes: "First time hosting");

        Assert.True(result.IsSuccess);

        var bookings = await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1));
        Assert.Equal(TestFacility.SheetMailboxes.Length, bookings.Count);
        Assert.All(bookings, b =>
        {
            Assert.Equal(BookingCategory.PracticeIce, b.Category);
            Assert.Equal(BookingState.Hold, b.State);
            Assert.Equal(HostName, b.RenterName);
            Assert.Equal(HostEmail, b.RenterEmail);
        });
        Assert.Single(bookings.Select(b => b.BookingGroupId).Distinct());

        Assert.Equal(2, mail.Sent.Count);
        var sent = Assert.Single(mail.Sent, m => m.To == facility.PracticeIceApproverEmail);
        Assert.Equal(facility.PracticeIceMailerMailbox, sent.From);
        Assert.Equal(HostEmail, sent.ReplyTo);

        // The host's own receipt carries the link to withdraw the request (2026-09-29).
        var receipt = Assert.Single(mail.Sent, m => m.To == HostEmail);
        Assert.Contains("waiting for approval", receipt.Body);
        Assert.Contains($"{TestFacility.PublicBaseUrl}/my-booking/cancel?id={bookings[0].BookingGroupId}", receipt.Body);
    }

    [Fact]
    public async Task Submit_LosesARaceToAConflictingBooking_ReturnsConflictAndSendsNoMail()
    {
        // The courtesy availability check reads a cached view; CreateAcrossSheetsAsync's own live,
        // per-sheet-locked check is what actually guarantees no double-booking (§4.3). To exercise
        // that second line of defence the conflicting event is written straight into the fake
        // gateway - an out-of-band write the app never saw, exactly the case §5.4.3 already had to
        // harden the public open-slot computation against. (Writing it through bookingService would
        // now invalidate the public cache, so the courtesy check would catch it first and this path
        // would never be reached.)
        var (requestService, _, availability, facility, mail, _) = Build(out var gateway);
        var day = facility.Today.AddDays(5);
        var start = day.AddHours(10);

        Assert.NotNull(await availability.GetPracticeIceStartAsync(start));

        gateway.Seed(TestFacility.SheetMailboxes[0], new Microsoft.Graph.Models.Event
        {
            Subject = "Late add",
            Start = TestFacility.Dtz(start),
            End = TestFacility.Dtz(start.AddHours(1)),
            ShowAs = Microsoft.Graph.Models.FreeBusyStatus.Busy,
            Categories = [BookingCategory.League.ToString()]
        });

        var result = await requestService.SubmitAsync(start, 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Submit_MailSendFails_StillCreatesTheHoldAndReportsNotificationNotSent()
    {
        // Regression test for a live-found bug (2026-08-09): a failed notification email must not
        // make an already-successful booking write look like a failure - the write happened, and
        // is what SubmitAsync's caller should be told, with the notification failure surfaced
        // separately rather than as an unhandled exception.
        var (requestService, bookingService, _, facility, mail, _) = Build();
        mail.ThrowOnSend = true;
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.True(result.IsSuccess);
        Assert.False(result.NotificationSent);
        Assert.Equal(TestFacility.SheetMailboxes.Length, (await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1))).Count);
    }

    [Fact]
    public async Task ApproveAsync_MailSendFails_StillConfirmsTheGroup()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5).AddHours(10);

        var created = await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = day, End = day.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = HostName, RenterEmail = HostEmail
        }, "tester");
        mail.ThrowOnSend = true;

        var result = await requestService.ApproveAsync(created.Bookings[0].BookingGroupId, "staff-user");

        // Regression test for a live-found bug (2026-08-09): the approval itself succeeding must
        // not be conflated with the notification succeeding - both are asserted separately here.
        Assert.True(result.Success);
        Assert.False(result.NotificationSent);
        Assert.All(await bookingService.GetBookingsForAllSheetsAsync(day.Date, day.Date.AddDays(1)), b => Assert.Equal(BookingState.Confirmed, b.State));
    }

    [Fact]
    public async Task DeclineAsync_MailSendFails_StillCancelsTheGroup()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5).AddHours(10);

        var created = await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = day, End = day.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = HostName, RenterEmail = HostEmail
        }, "tester");
        mail.ThrowOnSend = true;

        var result = await requestService.DeclineAsync(created.Bookings[0].BookingGroupId, "Test cleanup", "staff-user");

        Assert.True(result.Success);
        Assert.False(result.NotificationSent);
        Assert.Empty(await bookingService.GetBookingsForAllSheetsAsync(day.Date, day.Date.AddDays(1)));
    }

    [Fact]
    public async Task GetPendingAsync_OnlyIncludesPracticeIceHolds_OrderedByUpcomingStart()
    {
        var (requestService, bookingService, _, facility, _, _) = Build();
        var soon = facility.Today.AddDays(2).AddHours(10);
        var later = facility.Today.AddDays(6).AddHours(10);

        await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = later, End = later.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = "Later Host", RenterEmail = "later@example.com"
        }, "tester");
        await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = soon, End = soon.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = "Soon Host", RenterEmail = "soon@example.com"
        }, "tester");
        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = soon.AddHours(3), End = soon.AddHours(4),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Unrelated League"
        }, "tester");

        var pending = await requestService.GetPendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal("Soon Host", pending[0].HostName);
        Assert.Equal(TestFacility.SheetMailboxes.Length, pending[0].SheetCount);
        Assert.Equal("Later Host", pending[1].HostName);
    }

    [Fact]
    public async Task ApproveAsync_ConfirmsEveryMemberAndNotifiesTheVolunteer()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5).AddHours(10);

        var created = await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = day, End = day.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = HostName, RenterEmail = HostEmail
        }, "tester");
        var groupId = created.Bookings[0].BookingGroupId;

        var result = await requestService.ApproveAsync(groupId, "staff-user");

        Assert.True(result.Success);
        Assert.True(result.NotificationSent);
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(day.Date, day.Date.AddDays(1));
        Assert.All(bookings, b => Assert.Equal(BookingState.Confirmed, b.State));

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(HostEmail, sent.To);
        Assert.Contains("approved", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/my-booking/cancel?id={groupId}", sent.Body);
    }

    [Fact]
    public async Task ApproveAsync_UnknownGroup_ReturnsFailure()
    {
        var (requestService, _, _, _, _, _) = Build();

        var result = await requestService.ApproveAsync(Guid.NewGuid(), "staff-user");

        Assert.False(result.Success);
        Assert.False(result.NotificationSent);
    }

    [Fact]
    public async Task DeclineAsync_EmptyReason_Throws()
    {
        var (requestService, _, _, _, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => requestService.DeclineAsync(Guid.NewGuid(), "  ", "staff-user"));
    }

    [Fact]
    public async Task DeclineAsync_RemovesTheHoldAndNotifiesTheVolunteerWithTheReason()
    {
        var (requestService, bookingService, _, facility, mail, _) = Build();
        var day = facility.Today.AddDays(5).AddHours(10);

        var created = await bookingService.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = day, End = day.AddHours(1),
            Category = BookingCategory.PracticeIce, State = BookingState.Hold, RenterName = HostName, RenterEmail = HostEmail
        }, "tester");
        var groupId = created.Bookings[0].BookingGroupId;

        var result = await requestService.DeclineAsync(groupId, "Ice needed for maintenance that day.", "staff-user");

        Assert.True(result.Success);
        Assert.True(result.NotificationSent);
        Assert.Empty(await bookingService.GetBookingsForAllSheetsAsync(day.Date, day.Date.AddDays(1)));

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(HostEmail, sent.To);
        Assert.Contains("Ice needed for maintenance", sent.Body);
        Assert.DoesNotContain("/my-booking/cancel", sent.Body); // nothing left to cancel
    }

    // ---- Partial-club sessions and released group event holds (staff request 2026-09-28) --------

    // Five sheets, like the club's - the default minimum of 3 open would leave a 3-sheet test
    // facility no room for a session alongside sheets already in use.
    private static readonly string[] FiveSheetLocalParts = ["sheet1", "sheet2", "sheet3", "sheet4", "sheet5"];
    private static readonly string[] FiveSheets = [.. FiveSheetLocalParts.Select(p => $"{p}@{TestFacility.TenantDomain}")];

    private static (PracticeIceRequestService RequestService, SheetBookingService BookingService, PublicAvailabilityService Availability, FacilityConfiguration Facility, FakeGraphMailGateway Mail, SchedulingWindowService Window)
        BuildFiveSheets() => Build(out _, sheetLocalParts: FiveSheetLocalParts);

    private static async Task BookLeague(SheetBookingService bookingService, string sheet, DateTime start, DateTime end) =>
        Assert.True((await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = end, Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "League"
        }, "tester")).IsSuccess);

    [Fact]
    public async Task Submit_TwoSheetsInUse_CreatesTheSessionOnTheThreeFreeSheetsOnly()
    {
        var (requestService, bookingService, _, facility, mail, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(5);
        var sheets = FiveSheets;
        await BookLeague(bookingService, sheets[0], day.AddHours(9), day.AddHours(13));
        await BookLeague(bookingService, sheets[1], day.AddHours(9), day.AddHours(13));

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.True(result.IsSuccess);
        var practice = (await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1)))
            .Where(b => b.Category == BookingCategory.PracticeIce).ToList();
        Assert.Equal(sheets.Skip(2).OrderBy(s => s), practice.Select(b => b.SheetMailbox).OrderBy(s => s));
        Assert.Contains("Sheets 3, 4 and 5", Assert.Single(mail.Sent, m => m.To == facility.PracticeIceApproverEmail).Body);
    }

    [Fact]
    public async Task Submit_ThreeSheetsInUse_IsRejected_BelowTheMinimumOpenSheets()
    {
        var (requestService, bookingService, _, facility, _, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(5);
        foreach (var sheet in FiveSheets.Take(3))
        {
            await BookLeague(bookingService, sheet, day.AddHours(9), day.AddHours(13));
        }

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_LongerSession_CoversOnlyTheSheetsFreeForTheWholeLength()
    {
        // Sheet 1 is free only until 11:00; a 2-hour session from 10:00 runs on sheets 2-5.
        var (requestService, bookingService, _, facility, _, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(5);
        var sheets = FiveSheets;
        await BookLeague(bookingService, sheets[0], day.AddHours(11), day.AddHours(13));

        var result = await requestService.SubmitAsync(day.AddHours(10), 120, HostName, HostEmail, certified: true, notes: null);

        Assert.True(result.IsSuccess);
        var practice = (await bookingService.GetBookingsForAllSheetsAsync(day, day.AddDays(1)))
            .Where(b => b.Category == BookingCategory.PracticeIce).Select(b => b.SheetMailbox).OrderBy(s => s);
        Assert.Equal(sheets.Skip(1).OrderBy(s => s), practice);
    }

    [Fact]
    public async Task Submit_OverAGroupEventHoldGuestsCanNoLongerBook_TakesAndTrimsTheHold()
    {
        var (requestService, bookingService, _, facility, _, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(5); // inside the 7-day release window
        var sheet = FiveSheets[0];
        Assert.True((await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = day.AddHours(9), End = day.AddHours(14), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester")).IsSuccess);

        var result = await requestService.SubmitAsync(day.AddHours(10), 120, HostName, HostEmail, certified: true, notes: null);

        Assert.True(result.IsSuccess);
        var onSheet = (await bookingService.GetBookingsAsync(sheet, day, day.AddDays(1))).OrderBy(b => b.Start).ToList();
        Assert.Contains(onSheet, b => b.Category == BookingCategory.PracticeIce && b.Start == day.AddHours(10));
        // The hold is trimmed around the session: 9-10 and 12-14 are left open for group events.
        var leftovers = onSheet.Where(b => b.Category == BookingCategory.GroupEvent).Select(b => (b.Start, b.End)).ToList();
        Assert.Equal([(day.AddHours(9), day.AddHours(10)), (day.AddHours(12), day.AddHours(14))], leftovers);
    }

    [Fact]
    public async Task Submit_OverAGroupEventHoldGuestsCanStillBook_DoesNotTakeIt()
    {
        var (requestService, bookingService, _, facility, _, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(10); // beyond the 7-day release window
        var sheet = FiveSheets[0];
        Assert.True((await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = day.AddHours(9), End = day.AddHours(14), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester")).IsSuccess);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.True(result.IsSuccess); // still runs on the other four sheets
        var hold = Assert.Single(await bookingService.GetBookingsAsync(sheet, day, day.AddDays(1)));
        Assert.Equal((BookingCategory.GroupEvent, day.AddHours(9), day.AddHours(14)), (hold.Category, hold.Start, hold.End));
    }

    [Fact]
    public async Task Decline_AfterTakingAGroupEventHold_DoesNotRestoreIt()
    {
        var (requestService, bookingService, _, facility, _, _) = BuildFiveSheets();
        var day = facility.Today.AddDays(5);
        var sheet = FiveSheets[0];
        await bookingService.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = day.AddHours(10), End = day.AddHours(12), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");
        Assert.True((await requestService.SubmitAsync(day.AddHours(10), 120, HostName, HostEmail, certified: true, notes: null)).IsSuccess);

        var request = Assert.Single(await requestService.GetPendingAsync());
        await requestService.DeclineAsync(request.BookingGroupId, "Not this time", "staff");

        Assert.Empty(await bookingService.GetBookingsAsync(sheet, day, day.AddDays(1)));
    }
}
