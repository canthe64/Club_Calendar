using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Wires up a real BreelyBookingProcessor + SheetBookingService/ClubEventService against a
/// FakeGraphEventGateway, so processor tests exercise the actual service-layer logic (hold-claiming,
/// trimming, conflict rules) rather than a hand-rolled substitute of it.</summary>
public static class BreelyHarness
{
    public static (BreelyBookingProcessor Processor, FakeGraphEventGateway Gateway, FacilityConfiguration Facility, SheetBookingService SheetBookings) Build(
        Func<Task>? delayDuringFindEvents = null)
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo) { DelayDuringFindEvents = delayDuringFindEvents };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var sheetBookings = new SheetBookingService(gateway, cache, facility, appLog, viewCache, new SchedulingWindowService(appLog, viewCache));
        var clubEvents = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var processor = new BreelyBookingProcessor(sheetBookings, clubEvents, facility, appLog, NullLogger<BreelyBookingProcessor>.Instance);
        return (processor, gateway, facility, sheetBookings);
    }

    /// <summary>Seeds an open Group Event hold directly on the fake, the same shape
    /// SheetBookingService.CreateHoldAsync would have written - avoids needing the private
    /// extended-property ids to set up "there's already an open hold here" starting state.</summary>
    public static string SeedOpenHold(FakeGraphEventGateway gateway, string sheet, DateTime start, DateTime end) =>
        gateway.Seed(sheet, new Event
        {
            Subject = "Available for Group Events",
            ShowAs = FreeBusyStatus.Tentative,
            Categories = [BookingCategory.GroupEvent.ToString()],
            Start = TestFacility.Dtz(start),
            End = TestFacility.Dtz(end)
        });
}
