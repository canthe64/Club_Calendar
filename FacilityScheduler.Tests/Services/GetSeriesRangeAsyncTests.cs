using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;
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
    /// failure (master deleted underneath, transient error) can be exercised without touching every
    /// other gateway member. Same local-decorator shape as EventSearchTests' CountingGateway.</summary>
    private sealed class ThrowingOnGetEventGateway(IGraphEventGateway inner) : IGraphEventGateway
    {
        public Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated Graph failure");

        public Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
            IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default) =>
            inner.GetCalendarViewAsync(mailbox, startUtc, endUtc, expand, extraHeaders, ct);

        public Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default) =>
            inner.FindEventsAsync(mailbox, filter, expand, ct);

        public Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default) =>
            inner.CreateEventAsync(mailbox, graphEvent, ct);

        public Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default) =>
            inner.PatchEventAsync(mailbox, eventId, patch, ct);

        public Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default) =>
            inner.DeleteEventAsync(mailbox, eventId, ct);

        public Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default) =>
            inner.GetInstancesAsync(mailbox, eventId, startUtc, endUtc, ct);
    }

    private static (SheetBookingService Service, FakeGraphEventGateway Gateway, FacilityConfiguration Facility) Build(IGraphEventGateway? gatewayOverride = null)
    {
        var facility = TestFacility.Create();
        var fake = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var service = new SheetBookingService(gatewayOverride ?? fake, cache, facility, appLog, viewCache, new SchedulingWindowService(appLog, viewCache));
        return (service, fake, facility);
    }

    private static SheetBooking SingleBooking(FacilityConfiguration facility, DateTime start) => new()
    {
        SheetMailbox = TestFacility.SheetMailboxes[0],
        Start = start,
        End = start.AddHours(1),
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = "Tuesday League"
    };

    [Fact]
    public async Task NonSeriesBooking_ReturnsNull()
    {
        var (service, gateway, facility) = Build();
        var booking = SingleBooking(facility, facility.Today.AddDays(1).AddHours(18));
        var created = await service.CreateConfirmedAsync(booking, "tester");
        Assert.True(created.IsSuccess);

        var result = await service.GetSeriesRangeAsync(created.Booking!);

        Assert.Null(result);
    }

    [Fact]
    public async Task SeriesBooking_ReturnsThePatternsConfiguredFirstAndLastDate()
    {
        var (service, gateway, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        var lastNight = firstNight.AddDays(21); // 4 weekly occurrences: 0, 7, 14, 21

        await service.CreateSeriesAsync(
            [TestFacility.SheetMailboxes[0]],
            new SheetBooking
            {
                SheetMailbox = "",
                Start = firstNight.AddHours(19),
                End = firstNight.AddHours(21),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Tuesday Nite League"
            },
            lastNight, [], "tester");

        var occurrence = (await service.GetBookingsForAllSheetsAsync(firstNight.Date, firstNight.Date.AddDays(1)))
            .Single(b => b.SeriesMasterId is not null);

        var result = await service.GetSeriesRangeAsync(occurrence);

        Assert.NotNull(result);
        Assert.Equal(firstNight.Date, result!.Value.FirstDate);
        Assert.Equal(lastNight.Date, result.Value.LastDate);
    }

    [Fact]
    public async Task LaterOccurrence_StillReportsTheSeriesOriginalFirstDate_NotItsOwnDate()
    {
        // The series' start, not "the date this particular occurrence happens to fall on" - the
        // whole point of showing this on a single occurrence's detail view.
        var (service, gateway, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        var lastNight = firstNight.AddDays(21);

        await service.CreateSeriesAsync(
            [TestFacility.SheetMailboxes[0]],
            new SheetBooking
            {
                SheetMailbox = "",
                Start = firstNight.AddHours(19),
                End = firstNight.AddHours(21),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Tuesday Nite League"
            },
            lastNight, [], "tester");

        var thirdWeek = firstNight.AddDays(14);
        var occurrence = (await service.GetBookingsForAllSheetsAsync(thirdWeek.Date, thirdWeek.Date.AddDays(1)))
            .Single(b => b.SeriesMasterId is not null);

        var result = await service.GetSeriesRangeAsync(occurrence);

        Assert.Equal(firstNight.Date, result!.Value.FirstDate);
        Assert.Equal(lastNight.Date, result.Value.LastDate);
    }

    [Fact]
    public async Task MasterEventUnreachable_ReturnsNullRatherThanThrowing()
    {
        // A stale reference to an already-deleted master, or any transient Graph failure - this is a
        // purely informational read, so it must degrade to "just don't show the extra text," never
        // break the dialog it's decorating.
        var facility = TestFacility.Create();
        var fake = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var seedingService = new SheetBookingService(fake, cache, facility, appLog, viewCache, new SchedulingWindowService(appLog, viewCache));

        var firstNight = facility.Today.AddDays(7);
        await seedingService.CreateSeriesAsync(
            [TestFacility.SheetMailboxes[0]],
            new SheetBooking
            {
                SheetMailbox = "",
                Start = firstNight.AddHours(19),
                End = firstNight.AddHours(21),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Tuesday Nite League"
            },
            firstNight.AddDays(21), [], "tester");

        var occurrence = (await seedingService.GetBookingsForAllSheetsAsync(firstNight.Date, firstNight.Date.AddDays(1)))
            .Single(b => b.SeriesMasterId is not null);

        var throwingService = new SheetBookingService(new ThrowingOnGetEventGateway(fake), new MemoryCache(new MemoryCacheOptions()), facility, appLog, viewCache, new SchedulingWindowService(appLog, viewCache));

        var result = await throwingService.GetSeriesRangeAsync(occurrence);

        Assert.Null(result);
    }
}
