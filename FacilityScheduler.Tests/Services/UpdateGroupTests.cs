using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// UpdateGroupAsync - the staff edit path for a one-off booking (single or multi-sheet). Moving
/// time, adding sheets and splitting a subset off are all-or-nothing against conflicts; only a
/// time change sends Start/End to Graph, because re-sent times trip Graph's adjacent-occurrence
/// check on series occurrences.
/// </summary>
public class UpdateGroupTests
{
    private static readonly string[] Sheets = TestFacility.SheetMailboxes;

    private sealed class PatchRecordingGateway(IGraphEventGateway inner) : DelegatingGraphEventGateway(inner)
    {
        public List<Event> Patches { get; } = [];

        public override Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default)
        {
            Patches.Add(patch);
            return base.PatchEventAsync(mailbox, eventId, patch, ct);
        }
    }

    private static SheetBooking Booking(string sheet, DateTime start, DateTime end, string? renter = "Smith Party") => new()
    {
        SheetMailbox = sheet, Start = start, End = end, Category = BookingCategory.GroupEvent, State = BookingState.Confirmed, RenterName = renter
    };

    private static async Task<List<SheetBooking>> SeedGroupAsync(SheetBookingService service, string[] sheets, DateTime start, DateTime end)
    {
        var created = await service.CreateAcrossSheetsAsync(sheets, Booking("", start, end), "tester");
        Assert.True(created.IsSuccess);
        return created.Bookings;
    }

    private static async Task<SheetBooking> OnlyBookingAsync(SheetBookingService service, string sheet, DateTime day) =>
        Assert.Single(await service.GetBookingsAsync(sheet, day, day.AddDays(1)));

    [Fact]
    public async Task MovingTheGroup_ToAFreeTime_MovesEverySheetAndKeepsTheGroup()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0], Sheets[1]], day.AddHours(18), day.AddHours(20));

        var result = await h.Bookings.UpdateGroupAsync(group, Booking("", day.AddHours(14), day.AddHours(16)), "tester");

        Assert.True(result.IsSuccess);
        foreach (var sheet in new[] { Sheets[0], Sheets[1] })
        {
            var moved = await OnlyBookingAsync(h.Bookings, sheet, day);
            Assert.Equal(day.AddHours(14), moved.Start);
            Assert.Equal(day.AddHours(16), moved.End);
            Assert.Equal(group[0].BookingGroupId, moved.BookingGroupId);
        }
    }

    [Fact]
    public async Task MovingTheGroup_OntoABookingOnOneOfItsSheets_IsRejectedAndNoSheetMoves()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0], Sheets[1]], day.AddHours(18), day.AddHours(20));
        Assert.True((await h.Bookings.BookAsync(Booking(Sheets[1], day.AddHours(14), day.AddHours(15), "League"))).IsSuccess);

        var result = await h.Bookings.UpdateGroupAsync(group, Booking("", day.AddHours(14), day.AddHours(16)), "tester");

        Assert.False(result.IsSuccess);
        Assert.Equal("League", Assert.Single(result.Conflicts).RenterName);
        // All-or-nothing: the free sheet didn't move either.
        Assert.Equal(day.AddHours(18), (await OnlyBookingAsync(h.Bookings, Sheets[0], day)).Start);
    }

    [Fact]
    public async Task AddingASheet_CreatesItInTheSameGroup()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0]], day.AddHours(18), day.AddHours(20));

        var result = await h.Bookings.UpdateGroupAsync(group, Booking("", day.AddHours(18), day.AddHours(20)), "tester",
            newSheetMailboxes: [Sheets[2]]);

        Assert.True(result.IsSuccess);
        var added = await OnlyBookingAsync(h.Bookings, Sheets[2], day);
        Assert.Equal(group[0].BookingGroupId, added.BookingGroupId);
        Assert.Equal("Smith Party", added.RenterName);
    }

    [Fact]
    public async Task AddingASheetThatIsTaken_IsRejectedAndTheExistingSheetIsUnchanged()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0]], day.AddHours(18), day.AddHours(20));
        Assert.True((await h.Bookings.BookAsync(Booking(Sheets[2], day.AddHours(19), day.AddHours(21), "League"))).IsSuccess);

        var result = await h.Bookings.UpdateGroupAsync(group, Booking("", day.AddHours(18), day.AddHours(20), "Renamed"), "tester",
            newSheetMailboxes: [Sheets[2]]);

        Assert.False(result.IsSuccess);
        Assert.Equal("Smith Party", (await OnlyBookingAsync(h.Bookings, Sheets[0], day)).RenterName);
        Assert.Equal("League", (await OnlyBookingAsync(h.Bookings, Sheets[2], day)).RenterName);
    }

    [Fact]
    public async Task EditingASubsetWithANewGroupId_SplitsOnlyThoseSheetsOff()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0], Sheets[1]], day.AddHours(18), day.AddHours(20));
        var edited = group.Single(b => b.SheetMailbox == Sheets[1]);
        var splitId = Guid.NewGuid();

        var result = await h.Bookings.UpdateGroupAsync([edited], Booking("", day.AddHours(18), day.AddHours(20), "Jones Party"), "tester",
            newBookingGroupId: splitId);

        Assert.True(result.IsSuccess);
        var untouched = await OnlyBookingAsync(h.Bookings, Sheets[0], day);
        var split = await OnlyBookingAsync(h.Bookings, Sheets[1], day);
        Assert.Equal(group[0].BookingGroupId, untouched.BookingGroupId);
        Assert.Equal("Smith Party", untouched.RenterName);
        Assert.Equal(splitId, split.BookingGroupId);
        Assert.Equal("Jones Party", split.RenterName);
    }

    [Fact]
    public async Task OnlyAnEditThatMovesTheTime_SendsStartAndEnd()
    {
        PatchRecordingGateway? recorder = null;
        var h = ServiceHarness.Create(decorate: fake => recorder = new PatchRecordingGateway(fake));
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0]], day.AddHours(18), day.AddHours(20));

        await h.Bookings.UpdateGroupAsync(group, Booking("", day.AddHours(18), day.AddHours(20), "Renamed"), "tester");
        var metadataOnly = Assert.Single(recorder!.Patches);
        Assert.Null(metadataOnly.Start);
        Assert.Null(metadataOnly.End);

        var renamed = await OnlyBookingAsync(h.Bookings, Sheets[0], day);
        await h.Bookings.UpdateGroupAsync([renamed], Booking("", day.AddHours(19), day.AddHours(21), "Renamed"), "tester");
        Assert.NotNull(recorder.Patches[1].Start);
        Assert.NotNull(recorder.Patches[1].End);
    }

    [Fact]
    public async Task AFailedCreateOnAnAddedSheet_RollsBackTheSheetsItAlreadyAdded()
    {
        var h = ServiceHarness.Create();
        var day = h.Facility.Today.AddDays(2);
        var group = await SeedGroupAsync(h.Bookings, [Sheets[0]], day.AddHours(18), day.AddHours(20));
        h.Gateway.FailCreateAfter = 1; // counted from here: the first added sheet is created, the second throws

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bookings.UpdateGroupAsync(
            group, Booking("", day.AddHours(18), day.AddHours(20)), "tester", newSheetMailboxes: [Sheets[1], Sheets[2]]));

        Assert.Empty(h.Gateway.Events(Sheets[1]));
        Assert.Empty(h.Gateway.Events(Sheets[2]));
        Assert.Single(h.Gateway.Events(Sheets[0]));
    }
}
