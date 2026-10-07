using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// SheetBookingService.GetSeriesRangeAsync - the series' own configured first/last date, read from
/// the master's Recurrence.Range via a single Graph GET. Added for staff feedback (2026-08-27):
/// neither BookingDetailModal's "Part of a recurring series" note nor SeriesEditModal's header showed
/// this, because a booking's own SheetBooking record (and everything else the calendar page already
/// has loaded) carries only that ONE occurrence's Start/End - the series' overall span lives only on
/// the master event, which calendarView never returns for an individual occurrence.
/// </summary>
public class GetSeriesRangeAsyncTests
{
    /// <summary>Throws on GetEventAsync only - the one call this method makes - so a real Graph
    /// failure (master deleted underneath, transient error) can be exercised.</summary>
    private sealed class ThrowingOnGetEventGateway(IGraphEventGateway inner) : DelegatingGraphEventGateway(inner)
    {
        public override Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated Graph failure");
    }

    private static SheetBooking LeagueTemplate(DateTime firstNight) => new()
    {
        SheetMailbox = "",
        Start = firstNight.AddHours(19),
        End = firstNight.AddHours(21),
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = "Tuesday Nite League"
    };

    [Fact]
    public async Task NonSeriesBooking_ReturnsNull()
    {
        var h = ServiceHarness.Create();
        var start = h.Facility.Today.AddDays(1).AddHours(18);
        var created = await h.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = start,
            End = start.AddHours(1),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League"
        });
        Assert.True(created.IsSuccess);

        var result = await h.Bookings.GetSeriesRangeAsync(created.Bookings[0]);

        Assert.Null(result);
    }

    [Fact]
    public async Task LaterOccurrence_ReportsTheSeriesOwnFirstAndLastDate_NotItsOwnDate()
    {
        // The series' start, not "the date this particular occurrence happens to fall on" - the
        // whole point of showing this on a single occurrence's detail view.
        var h = ServiceHarness.Create();
        var firstNight = h.Facility.Today.AddDays(7);
        var lastNight = firstNight.AddDays(21); // 4 weekly occurrences: 0, 7, 14, 21
        await h.Bookings.CreateSeriesAsync([TestFacility.SheetMailboxes[0]], LeagueTemplate(firstNight), lastNight, [], "tester");

        var thirdWeek = firstNight.AddDays(14);
        var occurrence = (await h.Bookings.GetBookingsForAllSheetsAsync(thirdWeek.Date, thirdWeek.Date.AddDays(1)))
            .Single(b => b.SeriesMasterId is not null);

        var result = await h.Bookings.GetSeriesRangeAsync(occurrence);

        Assert.Equal(firstNight.Date, result!.Value.FirstDate);
        Assert.Equal(lastNight.Date, result.Value.LastDate);
    }

    [Fact]
    public async Task MasterEventUnreachable_ReturnsNullRatherThanThrowing()
    {
        // A stale reference to an already-deleted master, or any transient Graph failure - this is a
        // purely informational read, so it must degrade to "just don't show the extra text," never
        // break the dialog it's decorating.
        var seeding = ServiceHarness.Create();
        var firstNight = seeding.Facility.Today.AddDays(7);
        await seeding.Bookings.CreateSeriesAsync([TestFacility.SheetMailboxes[0]], LeagueTemplate(firstNight), firstNight.AddDays(21), [], "tester");
        var occurrence = (await seeding.Bookings.GetBookingsForAllSheetsAsync(firstNight.Date, firstNight.Date.AddDays(1)))
            .Single(b => b.SeriesMasterId is not null);

        var throwing = new SheetBookingService(new ThrowingOnGetEventGateway(seeding.Gateway), seeding.Cache, seeding.Facility,
            seeding.AppLog, seeding.ViewCache, seeding.Window);

        Assert.Null(await throwing.GetSeriesRangeAsync(occurrence));
    }
}
