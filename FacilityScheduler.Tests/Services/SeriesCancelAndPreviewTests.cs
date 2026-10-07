using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// CancelSeriesAsync (delete every occurrence, past and future, on every sheet of the group) and
/// PreviewSeriesConflictsAsync (the series wizard's per-date conflict list, informational only).
/// </summary>
public class SeriesCancelAndPreviewTests
{
    private static readonly string[] Sheets = TestFacility.SheetMailboxes;

    private static SheetBooking League(DateTime start, DateTime end) => new()
    {
        SheetMailbox = "", Start = start, End = end, Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Tuesday League"
    };

    /// <summary>A 4-week weekly series on the first two sheets, 7-9 PM, starting a week ago.</summary>
    private static async Task<DateTime> SeedSeriesAsync(ServiceHarness h)
    {
        var firstNight = h.Facility.Today.AddDays(-7);
        await h.Bookings.CreateSeriesAsync([Sheets[0], Sheets[1]], League(firstNight.AddHours(19), firstNight.AddHours(21)),
            firstNight.AddDays(21), [], "tester");
        return firstNight;
    }

    private static async Task<List<SheetBooking>> OccurrencesAsync(ServiceHarness h, DateTime from, int days) =>
        [.. (await h.Bookings.GetBookingsForAllSheetsAsync(from, from.AddDays(days))).Where(b => b.SeriesMasterId is not null)];

    [Fact]
    public async Task CancelSeries_RemovesEveryOccurrenceOnEverySheet_PastAndFuture()
    {
        var h = ServiceHarness.Create();
        var firstNight = await SeedSeriesAsync(h);
        Assert.Equal(8, (await OccurrencesAsync(h, firstNight, 28)).Count);

        var group = await OccurrencesAsync(h, firstNight.AddDays(7), 1);
        await h.Bookings.CancelSeriesAsync(group, "tester");

        Assert.Empty(await OccurrencesAsync(h, firstNight, 28));
    }

    [Fact]
    public async Task CancelSeries_ASheetWhoseSeriesIsAlreadyGone_DoesNotStopTheOthers()
    {
        var h = ServiceHarness.Create();
        var firstNight = await SeedSeriesAsync(h);
        var group = (await OccurrencesAsync(h, firstNight.AddDays(7), 1)).OrderBy(b => b.SheetMailbox, StringComparer.Ordinal).ToList();
        await h.Gateway.DeleteEventAsync(group[0].SheetMailbox, group[0].SeriesMasterId!);

        await h.Bookings.CancelSeriesAsync(group, "tester");

        Assert.Empty(await OccurrencesAsync(h, firstNight, 28));
    }

    [Fact]
    public async Task CancelSeries_LeavesAOneOffBookingInTheSelectionAlone()
    {
        var h = ServiceHarness.Create();
        var firstNight = await SeedSeriesAsync(h);
        var day = firstNight.AddDays(7);
        var oneOff = await h.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = Sheets[2], Start = day.AddHours(19), End = day.AddHours(21), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        });
        Assert.True(oneOff.IsSuccess);

        await h.Bookings.CancelSeriesAsync([.. await OccurrencesAsync(h, day, 1), oneOff.Bookings[0]], "tester");

        Assert.Single(h.Gateway.Events(Sheets[2]));
    }

    [Fact]
    public async Task Preview_ListsOnlyTheDatesThatOverlapABookingOnTheChosenSheets()
    {
        var h = ServiceHarness.Create();
        var week1 = h.Facility.Today.AddDays(3);
        var week2 = week1.AddDays(7);
        var week3 = week1.AddDays(14);
        var week4 = week1.AddDays(21);
        Assert.True((await h.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = Sheets[1], Start = week2.AddHours(20), End = week2.AddHours(22), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        })).IsSuccess);
        // Ends exactly when the series starts - touching, not overlapping.
        Assert.True((await h.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = Sheets[0], Start = week3.AddHours(17), End = week3.AddHours(19), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        })).IsSuccess);
        // Overlaps, but on a sheet the series doesn't use.
        Assert.True((await h.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = Sheets[2], Start = week4.AddHours(19), End = week4.AddHours(21), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        })).IsSuccess);

        var conflicts = await h.Bookings.PreviewSeriesConflictsAsync([Sheets[0], Sheets[1]], [week1, week2, week3, week4],
            TimeSpan.FromHours(19), TimeSpan.FromHours(21));

        var only = Assert.Single(conflicts);
        Assert.Equal(week2, only.Key);
        Assert.Equal(Sheets[1], Assert.Single(only.Value).SheetMailbox);
    }

    [Fact]
    public async Task Preview_NoCandidateDates_IsEmpty()
    {
        var h = ServiceHarness.Create();

        Assert.Empty(await h.Bookings.PreviewSeriesConflictsAsync(Sheets, [], TimeSpan.FromHours(19), TimeSpan.FromHours(21)));
    }
}
