using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

/// <summary>Log lines are written for a person reading the file (2026-10-24): a short booking tag
/// shared by every line about one booking, sheet numbers instead of mailbox addresses, details before
/// the ~150-character Exchange event ids, and cancellations that say what was cancelled. The old
/// layout made tracing one practice ice session through a day's log close to impossible.</summary>
public class AppLogLineFormatTests
{
    private static readonly string[] Sheets = TestFacility.SheetMailboxes;

    [Fact]
    public async Task ABookingLine_ReadsBookingSheetsDetails_ThenTheExchangeIds()
    {
        var appLog = TestAppLog.Create(out _);
        var groupId = Guid.Parse("3f9a1c2e-0000-0000-0000-000000000000");

        await appLog.LogActionAsync("BookingUpdated", "kevin", "id-a,id-b,id-c", string.Join(",", Sheets[2], Sheets[0], Sheets[1]),
            "PracticeIce Confirmed, 10/24/2026 19:30-10/24/2026 21:30, Lani Dubberstein (Guest)", booking: groupId);

        var line = Assert.Single(await appLog.TailAsync(1));
        Assert.EndsWith(
            "action=BookingUpdated actor=kevin booking=3f9a1c2e sheets=1-3 details=PracticeIce Confirmed, 10/24/2026 19:30-10/24/2026 21:30, Lani Dubberstein (Guest) eventId=id-a,id-b,id-c",
            line);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 5 }, "1-3,5")]
    [InlineData(new[] { 4 }, "4")]
    [InlineData(new[] { 5, 2, 3 }, "2-3,5")]
    public void SheetNumbers_CollapseIntoRanges(int[] sheets, string expected) =>
        Assert.Equal(expected, AppLogService.SheetRanges(sheets));

    [Fact]
    public async Task AMailboxThatIsNotASheet_IsWrittenAsIs()
    {
        var appLog = TestAppLog.Create(out _);

        await appLog.LogActionAsync("ClubEventCreated", "kevin", "id-a", TestFacility.ClubEventsMailbox);

        Assert.Contains($"sheet={TestFacility.ClubEventsMailbox}", Assert.Single(await appLog.TailAsync(1)));
    }

    [Fact]
    public async Task EveryLineAboutOneBooking_SharesItsTag_AndTheCancellationSaysWhatWasCancelled()
    {
        var h = ServiceHarness.Create(appLog: TestAppLog.Create(out _));
        var day = h.Facility.Today.AddDays(2);
        var created = await h.Bookings.CreateAcrossSheetsAsync([Sheets[0], Sheets[1]], new SheetBooking
        {
            SheetMailbox = "", Start = day.AddHours(19), End = day.AddHours(21), Category = BookingCategory.PracticeIce,
            State = BookingState.Confirmed, RenterName = "Lani Dubberstein (Guest)"
        }, "tester");

        await h.Bookings.CancelGroupAsync(created.Bookings, reopenAsGroupEventHold: false, "kevin");

        var tag = $"booking={AppLogService.BookingRef(created.Bookings[0].BookingGroupId)}";
        var lines = await h.AppLog.TailAsync(10);
        Assert.Contains(lines, l => l.Contains("action=BookingCreated") && l.Contains(tag));
        var cancelled = Assert.Single(lines, l => l.Contains("action=BookingCancelled"));
        Assert.Contains(tag, cancelled);
        Assert.Contains("sheets=1-2", cancelled);
        Assert.Contains("details=PracticeIce Confirmed", cancelled);
        Assert.Contains("Lani Dubberstein (Guest)", cancelled);
    }
}
