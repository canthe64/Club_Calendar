using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

public class PracticeIceRequestServiceTests
{
    private const string HostName = "Jane Curler";
    private const string HostEmail = "jane@example.com";

    private static (PracticeIceRequestService RequestService, SheetBookingService BookingService, PublicAvailabilityService Availability, FacilityConfiguration Facility, FakeGraphMailGateway Mail)
        Build(PracticeIceOptions? practiceIce = null)
    {
        var facility = TestFacility.Create(practiceIce: practiceIce);
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog);
        var availability = new PublicAvailabilityService(bookingService, clubEventService, cache, facility);
        var mail = new FakeGraphMailGateway();
        var requestService = new PracticeIceRequestService(bookingService, availability, mail, facility, appLog);
        return (requestService, bookingService, availability, facility, mail);
    }

    [Fact]
    public async Task Submit_MailNotConfigured_IsBlockedAndCreatesNothing()
    {
        var (requestService, bookingService, _, facility, mail) = Build(practiceIce: new PracticeIceOptions());
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
        var (requestService, _, _, facility, mail) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, HostEmail, certified: false, notes: null);

        Assert.False(result.IsSuccess);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Submit_MissingHostEmail_IsRejected()
    {
        var (requestService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(10), 60, HostName, "", certified: true, notes: null);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Submit_StartOutsideEligibleHours_IsRejected()
    {
        var (requestService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        var result = await requestService.SubmitAsync(day.AddHours(23), 60, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_DurationLongerThanTheOpenWindow_IsRejected()
    {
        var (requestService, _, _, facility, _) = Build();
        var day = facility.Today.AddDays(5);

        // The default eligible window is 6:00-22:00 (960 minutes) - 990 doesn't fit.
        var result = await requestService.SubmitAsync(day.AddHours(6), 990, HostName, HostEmail, certified: true, notes: null);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_Success_CreatesHoldAcrossEverySheetAndNotifiesApprovers()
    {
        var (requestService, bookingService, _, facility, mail) = Build();
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

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(facility.PracticeIceMailerMailbox, sent.From);
        Assert.Equal(facility.PracticeIceApproverEmail, sent.To);
        Assert.Equal(HostEmail, sent.ReplyTo);
    }

    [Fact]
    public async Task Submit_LosesARaceToAConflictingBooking_ReturnsConflictAndSendsNoMail()
    {
        var (requestService, bookingService, availability, facility, mail) = Build();
        var day = facility.Today.AddDays(5);
        var start = day.AddHours(10);

        // Primes the 60s availability cache as "free" before the conflicting booking exists -
        // the same staleness CreateAcrossSheetsAsync's own live, locked check (never cached) is
        // relied on to catch (§4.3).
        Assert.NotNull(await availability.FindPracticeIceWindowContainingAsync(start));

        await bookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = start, End = start.AddHours(1),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Late add"
        }, "tester");

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
        var (requestService, bookingService, _, facility, mail) = Build();
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
        var (requestService, bookingService, _, facility, mail) = Build();
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
        var (requestService, bookingService, _, facility, mail) = Build();
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
        var (requestService, bookingService, _, facility, _) = Build();
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
        var (requestService, bookingService, _, facility, mail) = Build();
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
    }

    [Fact]
    public async Task ApproveAsync_UnknownGroup_ReturnsFailure()
    {
        var (requestService, _, _, _, _) = Build();

        var result = await requestService.ApproveAsync(Guid.NewGuid(), "staff-user");

        Assert.False(result.Success);
        Assert.False(result.NotificationSent);
    }

    [Fact]
    public async Task DeclineAsync_EmptyReason_Throws()
    {
        var (requestService, _, _, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => requestService.DeclineAsync(Guid.NewGuid(), "  ", "staff-user"));
    }

    [Fact]
    public async Task DeclineAsync_RemovesTheHoldAndNotifiesTheVolunteerWithTheReason()
    {
        var (requestService, bookingService, _, facility, mail) = Build();
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
    }
}
