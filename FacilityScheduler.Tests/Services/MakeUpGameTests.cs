using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// Make-up games (staff request 2026-09-28): a two-hour slot where some sheet is free for the whole
/// two hours and another sheet has a confirmed booking for the whole two hours, booked auto-approved
/// as League on the highest-numbered free sheet. Five sheets, like the club's. Days are anchored
/// several days out so results don't depend on when the suite runs.
/// </summary>
public class MakeUpGameTests
{
    private const string Name = "Jane Curler";
    private const string Email = "jane@example.com";
    private static readonly string[] SheetLocalParts = ["sheet1", "sheet2", "sheet3", "sheet4", "sheet5"];
    private static readonly string[] Sheets = [.. SheetLocalParts.Select(p => $"{p}@{TestFacility.TenantDomain}")];

    private sealed record Harness(MakeUpGameService Service, PublicAvailabilityService Availability, SheetBookingService Bookings,
        FacilityConfiguration Facility, FakeGraphMailGateway Mail);

    private static Harness Build(PracticeIceOptions? practiceIce = null)
    {
        var facility = TestFacility.Create(sheetLocalParts: SheetLocalParts, practiceIce: practiceIce);
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookings = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEvents = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var availability = new PublicAvailabilityService(bookings, clubEvents, cache, facility, viewCache, window);
        var mail = new FakeGraphMailGateway();
        return new Harness(new MakeUpGameService(bookings, availability, mail, facility, appLog), availability, bookings, facility, mail);
    }

    private static async Task Book(SheetBookingService bookings, string sheet, DateTime start, DateTime end,
        BookingCategory category = BookingCategory.League, BookingState state = BookingState.Confirmed) =>
        Assert.True((await bookings.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = end, Category = category, State = state, RenterName = "Existing"
        }, "tester")).IsSuccess);

    private static async Task<List<MakeUpGameOption>> OnDay(Harness h, DateTime day) =>
        (await h.Availability.GetMakeUpGameOptionsAsync()).Where(o => o.Start.Date == day).ToList();

    // ---- Availability ---------------------------------------------------------------------------

    [Fact]
    public async Task NothingConfirmedAnywhere_OffersNothing()
    {
        // Requesters may not be qualified to open the club - someone already running ice is required.
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21), BookingCategory.GroupEvent, BookingState.Hold);

        Assert.Empty(await OnDay(h, day));
    }

    [Fact]
    public async Task ConfirmedLeague_OffersExactlyTheStartsItCoversForTheFullTwoHours_OnTheHighestFreeSheet()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21.5));

        var options = await OnDay(h, day);

        Assert.Equal([day.AddHours(19), day.AddHours(19.5)], options.Select(o => o.Start));
        Assert.All(options, o =>
        {
            Assert.Equal(Sheets[4], o.SheetMailbox);
            Assert.Equal(o.Start.AddHours(2), o.End);
        });
    }

    [Fact]
    public async Task HighestNumberedSheets_InUse_GameGoesOnTheHighestOneStillFree()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[4], day.AddHours(19), day.AddHours(21));
        await Book(h.Bookings, Sheets[3], day.AddHours(19), day.AddHours(21));

        var option = Assert.Single(await OnDay(h, day));

        Assert.Equal(Sheets[2], option.SheetMailbox);
    }

    [Fact]
    public async Task EverySheetInUse_OffersNothing()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        foreach (var sheet in Sheets)
        {
            await Book(h.Bookings, sheet, day.AddHours(19), day.AddHours(21));
        }

        Assert.Empty(await OnDay(h, day));
    }

    [Fact]
    public async Task GroupEventHoldGuestsCanNoLongerBook_CountsAsFree()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));
        await Book(h.Bookings, Sheets[4], day.AddHours(18), day.AddHours(22), BookingCategory.GroupEvent, BookingState.Hold);

        Assert.Equal(Sheets[4], Assert.Single(await OnDay(h, day)).SheetMailbox);
    }

    [Fact]
    public async Task GroupEventHoldGuestsCanStillBook_Blocks()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(10);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));
        await Book(h.Bookings, Sheets[4], day.AddHours(18), day.AddHours(22), BookingCategory.GroupEvent, BookingState.Hold);

        Assert.Equal(Sheets[3], Assert.Single(await OnDay(h, day)).SheetMailbox);
    }

    [Fact]
    public async Task StartAtTheEligibleEndHourOrLater_IsNotOffered_ButAGameMayRunPastIt()
    {
        var h = Build(); // eligible hours 6-22
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(20), day.AddDays(1).AddHours(1));

        var starts = (await OnDay(h, day)).Select(o => o.Start).ToList();

        Assert.Contains(day.AddHours(21.5), starts); // ends 23:30, past the 22:00 eligible end
        Assert.DoesNotContain(day.AddHours(22), starts);
    }

    // ---- Booking --------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_BooksAConfirmedLeagueGame_NamedForTheRequester_AndEmailsStaffAndRequester()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));

        var result = await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.StaffNotified && result.RequesterNotified);
        var game = Assert.Single(await h.Bookings.GetBookingsAsync(Sheets[4], day, day.AddDays(1)));
        Assert.Equal((BookingCategory.League, BookingState.Confirmed), (game.Category, game.State));
        Assert.Equal((day.AddHours(19), day.AddHours(21)), (game.Start, game.End));
        Assert.Equal("Make-Up Game Requested by Jane Curler", game.RenterName);
        Assert.Equal(Email, game.RenterEmail);

        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.Contains(h.Mail.Sent, m => m.To == h.Facility.PracticeIceApproverEmail && m.ReplyTo == Email);
        Assert.Contains(h.Mail.Sent, m => m.To == Email && m.ReplyTo == h.Facility.PracticeIceApproverEmail);
        Assert.All(h.Mail.Sent, m => Assert.Contains("Sheet 5", m.Body));
    }

    [Fact]
    public async Task Submit_PublicCalendarShowsTheRequesterNamedTitle()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));
        Assert.True((await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true)).IsSuccess);

        var view = await h.Availability.GetDayViewAsync(day);

        Assert.Contains(view.Bookings, b => b.Title == "Make-Up Game Requested by Jane Curler");
    }

    [Fact]
    public async Task Submit_TakesAndTrimsAGroupEventHoldGuestsCanNoLongerBook()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));
        await Book(h.Bookings, Sheets[4], day.AddHours(17), day.AddHours(21), BookingCategory.GroupEvent, BookingState.Hold);

        Assert.True((await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true)).IsSuccess);

        var onSheet = (await h.Bookings.GetBookingsAsync(Sheets[4], day, day.AddDays(1))).OrderBy(b => b.Start).ToList();
        Assert.Equal([(day.AddHours(17), day.AddHours(19)), (day.AddHours(19), day.AddHours(21))], onSheet.Select(b => (b.Start, b.End)));
        Assert.Equal(BookingState.Hold, onSheet[0].State);
        Assert.Equal(BookingCategory.League, onSheet[1].Category);
    }

    [Theory]
    [InlineData("jane@example.com")] // a sign-in with no display name falls back to the UPN
    [InlineData("  ")]
    public void BookingTitle_WithoutAUsableName_IsJustMakeUpGame(string name) =>
        Assert.Equal("Make-Up Game", MakeUpGameRules.BookingTitle(name));

    [Fact]
    public async Task Submit_NotAcknowledged_IsRejected_AndBooksNothing()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));

        var result = await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: false);

        Assert.False(result.IsSuccess);
        Assert.Empty(await h.Bookings.GetBookingsAsync(Sheets[4], day, day.AddDays(1)));
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task Submit_MailNotConfigured_IsRejected()
    {
        var h = Build(new PracticeIceOptions());
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));

        var result = await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true);

        Assert.False(result.IsSuccess);
        Assert.Empty(await h.Bookings.GetBookingsAsync(Sheets[4], day, day.AddDays(1)));
    }

    [Fact]
    public async Task Submit_TimeNotOffered_IsRejected()
    {
        var h = Build();
        var day = h.Facility.Today.AddDays(5); // nothing confirmed, so nothing offered

        var result = await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsConflict);
    }

    [Fact]
    public async Task Submit_MailFails_StillBooks_AndReportsItWasNotSent()
    {
        var h = Build();
        h.Mail.ThrowOnSend = true;
        var day = h.Facility.Today.AddDays(5);
        await Book(h.Bookings, Sheets[0], day.AddHours(19), day.AddHours(21));

        var result = await h.Service.SubmitAsync(day.AddHours(19), Name, Email, acknowledged: true);

        Assert.True(result.IsSuccess);
        Assert.False(result.StaffNotified);
        Assert.Single(await h.Bookings.GetBookingsAsync(Sheets[4], day, day.AddDays(1)));
    }

    // ---- Public listing page --------------------------------------------------------------------

    [Fact]
    public void ListingPage_LinksEachTimeToTheRequestPage_WithItsSheet()
    {
        var facility = TestFacility.Create(sheetLocalParts: SheetLocalParts);
        var start = facility.Today.AddDays(5).AddHours(19);

        var html = MakeUpGamePublicEndpoint.RenderPage(facility, [new MakeUpGameOption(start, start.AddHours(2), Sheets[4])]);

        Assert.Contains($"""<a href="/make-up-game/request?start={start:yyyy-MM-ddTHH:mm}" """, html);
        Assert.Contains("Sheet 5", html);
    }

    [Fact]
    public void ListingPage_NoTimes_SaysSo()
    {
        var html = MakeUpGamePublicEndpoint.RenderPage(TestFacility.Create(sheetLocalParts: SheetLocalParts), []);

        Assert.Contains("No make-up game times right now", html);
    }
}
