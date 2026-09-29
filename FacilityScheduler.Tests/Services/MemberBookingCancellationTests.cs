using System.Security.Claims;
using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// Members cancelling their own make-up games and practice ice from the link in their booking emails
/// (operator request 2026-09-29): sign-in required, only the booking's own account, any time before
/// it starts, and never on merely opening the link. Five sheets, like the club's.
/// </summary>
public class MemberBookingCancellationTests : BunitContext
{
    private const string Name = "Jane Curler";
    private const string Email = "jane@example.com";
    private static readonly string[] SheetLocalParts = ["sheet1", "sheet2", "sheet3", "sheet4", "sheet5"];
    private static readonly string[] Sheets = [.. SheetLocalParts.Select(p => $"{p}@{TestFacility.TenantDomain}")];

    private sealed record Harness(
        MemberBookingCancellationService Cancellation, MakeUpGameService MakeUpGames, PracticeIceRequestService PracticeIce,
        SheetBookingService Bookings, FacilityConfiguration Facility, FakeGraphMailGateway Mail, AppLogService Log);

    private static Harness Build(string? publicBaseUrl = TestFacility.PublicBaseUrl)
    {
        var facility = TestFacility.Create(sheetLocalParts: SheetLocalParts, publicBaseUrl: publicBaseUrl);
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var log = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(log, viewCache);
        var bookings = new SheetBookingService(gateway, cache, facility, log, viewCache, window);
        var clubEvents = new ClubEventService(gateway, cache, facility, log, viewCache);
        var availability = new PublicAvailabilityService(bookings, clubEvents, cache, facility, viewCache, window);
        var mail = new FakeGraphMailGateway();
        var cancellation = new MemberBookingCancellationService(bookings, mail, facility, log);
        return new Harness(cancellation,
            new MakeUpGameService(bookings, availability, mail, facility, log, cancellation),
            new PracticeIceRequestService(bookings, availability, mail, facility, log, cancellation),
            bookings, facility, mail, log);
    }

    private static async Task<Guid> BookMakeUpGame(Harness h, DateTime day)
    {
        Assert.True((await h.Bookings.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = Sheets[0], Start = day.AddHours(19), End = day.AddHours(21),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "League"
        }, "tester")).IsSuccess);
        Assert.True((await h.MakeUpGames.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true)).IsSuccess);
        return GroupOn(h, Sheets[4], day);
    }

    private static async Task<Guid> RequestPracticeIce(Harness h, DateTime day)
    {
        Assert.True((await h.PracticeIce.SubmitAsync(day.AddHours(10), 60, Name, Email, certified: true, notes: null)).IsSuccess);
        return GroupOn(h, Sheets[0], day);
    }

    private static Guid GroupOn(Harness h, string sheet, DateTime day) =>
        h.Bookings.GetBookingsAsync(sheet, day, day.AddDays(1)).Result
            .Single(b => !string.IsNullOrWhiteSpace(b.RenterEmail)).BookingGroupId;

    private static async Task<int> MemberBookingsOn(Harness h, DateTime day) =>
        (await h.Bookings.GetBookingsForAllSheetsAsync(day, day.AddDays(1))).Count(b => !string.IsNullOrWhiteSpace(b.RenterEmail));

    // ---- Links in emails -------------------------------------------------------------------------

    [Fact]
    public async Task MakeUpGameConfirmation_CarriesTheCancelLink_ForThatBooking()
    {
        var h = Build();
        var groupId = await BookMakeUpGame(h, h.Facility.Today.AddDays(5));

        var toMember = Assert.Single(h.Mail.Sent, m => m.To == Email);
        Assert.Contains($"Need to cancel? Cancel this booking: {TestFacility.PublicBaseUrl}/my-booking/cancel?id={groupId}", toMember.Body);
    }

    [Fact]
    public async Task NoPublicBaseUrl_EmailGoesOutWithoutALink_AndTheGapIsLogged()
    {
        var h = Build(publicBaseUrl: null);
        await BookMakeUpGame(h, h.Facility.Today.AddDays(5));

        Assert.DoesNotContain("/my-booking/cancel", Assert.Single(h.Mail.Sent, m => m.To == Email).Body);
        Assert.Contains(await h.Log.TailAsync(50), l => l.Contains("CancelLinkOmitted"));
    }

    // ---- Cancelling -----------------------------------------------------------------------------

    [Fact]
    public async Task MakeUpGame_OwnerCancels_BookingIsRemoved_AndCalendarTeamAndMemberAreEmailed()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var groupId = await BookMakeUpGame(h, day);
        h.Mail.Sent.Clear();

        var lookup = await h.Cancellation.FindAsync(groupId, Email);
        Assert.Equal(MemberBookingStatus.Cancellable, lookup.Status);
        Assert.Equal(MemberBookingKind.MakeUpGame, lookup.Details!.Kind);

        var result = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.True(result.IsCancelled);
        Assert.True(result.StaffNotified && result.MemberNotified);
        Assert.Equal(0, await MemberBookingsOn(h, day));
        Assert.Contains(h.Mail.Sent, m => m.To == h.Facility.PracticeIceApproverEmail && m.Body.Contains("Jane Curler") && m.Body.Contains("make-up game"));
        Assert.Contains(h.Mail.Sent, m => m.To == Email && m.Subject == "Your make-up game is cancelled");
        Assert.Contains(await h.Log.TailAsync(50), l => l.Contains("MemberBookingCancelled"));
    }

    [Fact]
    public async Task PendingPracticeIceRequest_OwnerCancels_EverySheetOfTheRequestIsRemoved()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var groupId = await RequestPracticeIce(h, day);

        var result = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.True(result.IsCancelled);
        Assert.Equal("practice ice request", result.Cancelled!.Description);
        Assert.Equal(0, await MemberBookingsOn(h, day));
        Assert.Empty(await h.PracticeIce.GetPendingAsync()); // gone from the staff approvals queue too
    }

    [Fact]
    public async Task ApprovedPracticeIce_OwnerCancels_IsRemoved()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var groupId = await RequestPracticeIce(h, day);
        Assert.True((await h.PracticeIce.ApproveAsync(groupId, "staff")).Success);

        var result = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.True(result.IsCancelled);
        Assert.Equal("practice ice", result.Cancelled!.Description);
        Assert.Equal(0, await MemberBookingsOn(h, day));
    }

    [Fact]
    public async Task OwnershipMatch_IgnoresEmailCase()
    {
        var h = Build();
        var groupId = await BookMakeUpGame(h, h.Facility.Today.AddDays(5));

        Assert.Equal(MemberBookingStatus.Cancellable, (await h.Cancellation.FindAsync(groupId, "JANE@Example.com")).Status);
    }

    [Theory]
    [InlineData("someone.else@example.com")]
    [InlineData("")]
    public async Task SomeoneElsesBooking_IsNotYours_RevealsNothing_AndCancelsNothing(string signedInEmail)
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var groupId = await BookMakeUpGame(h, day);
        h.Mail.Sent.Clear();

        var lookup = await h.Cancellation.FindAsync(groupId, signedInEmail);
        var result = await h.Cancellation.CancelAsync(groupId, "Someone Else", signedInEmail);

        Assert.Equal(MemberBookingStatus.NotYours, lookup.Status);
        Assert.Null(lookup.Details);
        Assert.False(result.IsCancelled);
        Assert.Equal(1, await MemberBookingsOn(h, day));
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task StaffMadeBooking_IsNotReachable()
    {
        // Staff-created practice ice / League never carries a member email - the page can't touch it,
        // and doesn't reveal it exists.
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var created = await h.Bookings.CreateAcrossSheetsAsync([Sheets[0]], new SheetBooking
        {
            SheetMailbox = "", Start = day.AddHours(10), End = day.AddHours(11),
            Category = BookingCategory.PracticeIce, State = BookingState.Confirmed, RenterName = "Staff practice"
        }, "staff");

        Assert.Equal(MemberBookingStatus.NotFound, (await h.Cancellation.FindAsync(created.Bookings[0].BookingGroupId, Email)).Status);
    }

    [Fact]
    public async Task AlreadyStarted_CannotBeCancelled()
    {
        var h = Build();
        var start = h.Facility.Now.AddMinutes(-30);
        var created = await h.Bookings.CreateAcrossSheetsAsync([Sheets[0]], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(2), Category = BookingCategory.PracticeIce,
            State = BookingState.Confirmed, RenterName = Name, RenterEmail = Email
        }, Name);
        var groupId = created.Bookings[0].BookingGroupId;

        var result = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.Equal(MemberBookingStatus.Started, result.Status);
        Assert.False(result.IsCancelled);
        Assert.NotEmpty(await h.Bookings.GetBookingsAsync(Sheets[0], start.AddHours(-1), start.AddHours(3)));
    }

    [Theory]
    [InlineData(false)] // a random id
    [InlineData(true)]  // Guid.Empty
    public async Task UnknownBooking_IsNotFound(bool empty)
    {
        var h = Build();

        var lookup = await h.Cancellation.FindAsync(empty ? Guid.Empty : Guid.NewGuid(), Email);

        Assert.Equal(MemberBookingStatus.NotFound, lookup.Status);
    }

    [Fact]
    public async Task CancelledTwice_SecondTimeIsNotFound()
    {
        var h = Build();
        var groupId = await BookMakeUpGame(h, h.Facility.Today.AddDays(5));
        Assert.True((await h.Cancellation.CancelAsync(groupId, Name, Email)).IsCancelled);

        var again = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.Equal(MemberBookingStatus.NotFound, again.Status);
    }

    [Fact]
    public async Task MailFails_StillCancels_AndReportsIt()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        var groupId = await BookMakeUpGame(h, day);
        h.Mail.ThrowOnSend = true;

        var result = await h.Cancellation.CancelAsync(groupId, Name, Email);

        Assert.True(result.IsCancelled);
        Assert.False(result.StaffNotified);
        Assert.Equal(0, await MemberBookingsOn(h, day));
    }

    // ---- Configuration and identity ---------------------------------------------------------------

    [Fact]
    public void PublicBaseUrl_TrailingSlashIsTrimmed_AndAMalformedValueFailsAtStartup()
    {
        Assert.Equal("https://calendar.example.org", TestFacility.Create(publicBaseUrl: "https://calendar.example.org/").PublicBaseUrl);
        Assert.Null(TestFacility.Create(publicBaseUrl: " ").PublicBaseUrl);
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(publicBaseUrl: "calendar.example.org"));
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(publicBaseUrl: "ftp://calendar.example.org"));
    }

    [Fact]
    public void MemberIdentity_PrefersTheNameClaim_AndPreferredUsernameForEmail()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("name", "Jane Curler"),
            new Claim("preferred_username", "jane@example.com")
        ], "TestAuth", "preferred_username", "roles"));

        Assert.Equal(("Jane Curler", "jane@example.com"), user.MemberIdentity());
    }

    // ---- The page ---------------------------------------------------------------------------------

    private Harness RenderSetup(string signedInEmail)
    {
        var h = Build();
        Services.AddSingleton(h.Cancellation);
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider { UserName = signedInEmail });
        return h;
    }

    private IRenderedComponent<MyBookingCancel> RenderPage(Guid groupId)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/my-booking/cancel?id={groupId}");
        return Render<MyBookingCancel>();
    }

    [Fact]
    public async Task Page_OpeningTheLink_ShowsTheBooking_ButCancelsNothing()
    {
        // Mail scanners open every link in a message - opening must never cancel.
        var h = RenderSetup(Email);
        var day = h.Facility.Today.AddDays(5);
        var groupId = await BookMakeUpGame(h, day);

        var cut = RenderPage(groupId);

        cut.WaitForAssertion(() => Assert.Contains("Cancel this booking", cut.Markup));
        Assert.Contains("Make-up game", cut.Markup);
        Assert.Equal(1, await MemberBookingsOn(h, day));
    }

    [Fact]
    public async Task Page_ClickingCancel_CancelsAndConfirms()
    {
        var h = RenderSetup(Email);
        var day = h.Facility.Today.AddDays(5);
        var groupId = await BookMakeUpGame(h, day);
        var cut = RenderPage(groupId);
        cut.WaitForAssertion(() => Assert.Contains("Cancel this booking", cut.Markup));

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("is cancelled", cut.Markup));
        Assert.Equal(0, await MemberBookingsOn(h, day));
    }

    [Fact]
    public async Task Page_SignedInAsSomeoneElse_OffersNoButton()
    {
        var h = RenderSetup("someone.else@example.com");
        var groupId = await BookMakeUpGame(h, h.Facility.Today.AddDays(5));

        var cut = RenderPage(groupId);

        cut.WaitForAssertion(() => Assert.Contains("made by a different account", cut.Markup));
        Assert.Empty(cut.FindAll("button"));
    }
}
