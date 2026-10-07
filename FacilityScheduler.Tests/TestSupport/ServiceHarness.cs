using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>The real service graph wired against an in-memory FakeGraphEventGateway - the same
/// setup most service, endpoint and page tests need, built once here instead of in each class.</summary>
public sealed record ServiceHarness(
    FacilityConfiguration Facility,
    FakeGraphEventGateway Gateway,
    IGraphEventGateway EffectiveGateway,
    IMemoryCache Cache,
    AppLogService AppLog,
    ViewCacheRegistry ViewCache,
    SchedulingWindowService Window,
    SheetBookingService Bookings,
    ClubEventService ClubEvents,
    PublicAvailabilityService Availability)
{
    /// <param name="facility">Defaults to <see cref="TestFacility.Create"/>.</param>
    /// <param name="decorate">Wraps the fake before the services see it - pass a
    /// <see cref="DelegatingGraphEventGateway"/> subclass to observe or break one Graph call.</param>
    /// <param name="appLog">A pre-built log, for tests that read back what was written.</param>
    public static ServiceHarness Create(
        FacilityConfiguration? facility = null,
        Func<FakeGraphEventGateway, IGraphEventGateway>? decorate = null,
        AppLogService? appLog = null)
    {
        facility ??= TestFacility.Create();
        var fake = new FakeGraphEventGateway(facility.ZoneInfo);
        var gateway = decorate?.Invoke(fake) ?? fake;
        var cache = new MemoryCache(new MemoryCacheOptions());
        appLog ??= TestAppLog.Create(facility);
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookings = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEvents = new ClubEventService(gateway, cache, facility, appLog, viewCache);
        var availability = new PublicAvailabilityService(bookings, clubEvents, cache, facility, viewCache, window);
        return new ServiceHarness(facility, fake, gateway, cache, appLog, viewCache, window, bookings, clubEvents, availability);
    }
}

public static class SheetBookingServiceTestExtensions
{
    /// <summary>Books one sheet through the production create path (CreateAcrossSheetsAsync) -
    /// the test-setup shorthand for "this booking already exists".</summary>
    public static Task<GroupBookingResult> BookAsync(this SheetBookingService service, SheetBooking booking, string actingUser = "tester") =>
        service.CreateAcrossSheetsAsync([booking.SheetMailbox], booking, actingUser);
}
