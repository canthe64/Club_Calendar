using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// A multi-sheet create is all-or-nothing against *existing* bookings via the conflict check, but
/// the writes themselves are separate Graph calls with no transaction. A failure partway through
/// used to leave the sheets already written in place while the caller saw an error - a half-created
/// booking carrying a BookingGroupId nobody references, occupying ice. These cover the compensation.
/// </summary>
public class MultiSheetRollbackTests
{
    private static (SheetBookingService Service, FakeGraphEventGateway Gateway, FacilityConfiguration Facility) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var service = new SheetBookingService(gateway, cache, facility, appLog, viewCache, new SchedulingWindowService(appLog, viewCache));
        return (service, gateway, facility);
    }

    [Fact]
    public async Task CreateAcrossSheets_FailsPartway_LeavesNoEventsBehind()
    {
        var (service, gateway, facility) = Build();
        var day = facility.Today.AddDays(3);

        // Fail on the third sheet, after two have already been written.
        gateway.FailCreateAfter = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
            {
                SheetMailbox = "",
                Start = day.AddHours(10),
                End = day.AddHours(12),
                Category = BookingCategory.Bonspiel,
                State = BookingState.Confirmed,
                RenterName = "Doomed Bonspiel"
            }, "tester"));

        // The two that did get written must have been rolled back, not left occupying ice.
        var remaining = await service.GetBookingsForAllSheetsAsync(day, day.AddDays(1));
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task CreateAcrossSheets_RollsBackAndLeavesTheSlotBookableAgain()
    {
        var (service, gateway, facility) = Build();
        var day = facility.Today.AddDays(3);
        var start = day.AddHours(10);

        gateway.FailCreateAfter = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
            {
                SheetMailbox = "", Start = start, End = start.AddHours(2),
                Category = BookingCategory.Bonspiel, State = BookingState.Confirmed, RenterName = "Doomed"
            }, "tester"));

        // Without rollback the orphans would conflict with this retry - the real user-visible
        // symptom of the bug, not just leftover rows.
        gateway.FailCreateAfter = null;
        var retry = await service.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(2),
            Category = BookingCategory.Bonspiel, State = BookingState.Confirmed, RenterName = "Retry"
        }, "tester");

        Assert.True(retry.IsSuccess);
        Assert.Equal(TestFacility.SheetMailboxes.Length, retry.Bookings.Count);
    }

    [Fact]
    public async Task CreateSeries_FailsPartway_LeavesNoSeriesBehind()
    {
        // Worse than a one-off if left orphaned: a leftover series blocks its slot every week.
        var (service, gateway, facility) = Build();
        var day = facility.Today.AddDays(3);

        gateway.FailCreateAfter = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSeriesAsync(TestFacility.SheetMailboxes, new SheetBooking
            {
                SheetMailbox = "",
                Start = day.AddHours(18),
                End = day.AddHours(20),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Doomed League"
            }, day.AddDays(28), [], "tester"));

        Assert.Empty(await service.GetBookingsForAllSheetsAsync(day, day.AddDays(1)));
    }
}
