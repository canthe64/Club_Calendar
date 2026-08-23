using Bunit;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>The service graph every staff Blazor page needs, registered into a BunitContext against
/// in-memory fakes. Extracted from EventSearchTests, which grew the first copy of this - Calendar.razor
/// injects one service more than EventSearch does (SchedulingWindowService), so a per-test-class copy
/// was a per-test-class chance to omit one and get a render-time DI failure instead of a real result.
/// Register the whole graph once, here, and let each test class take what it needs from the result.</summary>
public static class StaffPageServices
{
    /// <param name="gateway">A gateway to register instead of a bare <see cref="FakeGraphEventGateway"/> -
    /// pass a decorator (e.g. a counting or failing one) when the test needs to observe Graph traffic.</param>
    public static Registered Register(BunitContext ctx, IGraphEventGateway? gateway = null)
    {
        var facility = TestFacility.Create();
        var effectiveGateway = gateway ?? new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(effectiveGateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(effectiveGateway, cache, facility, appLog, viewCache);

        ctx.Services.AddSingleton(facility);
        ctx.Services.AddSingleton(bookingService);
        ctx.Services.AddSingleton(clubEventService);
        // Calendar.razor injects this; EventSearch.razor doesn't. Registered unconditionally so adding
        // an injection to a page under test can't silently depend on which test class renders it.
        ctx.Services.AddSingleton(window);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        return new Registered(facility, effectiveGateway, bookingService, clubEventService, window);
    }

    public sealed record Registered(
        FacilityConfiguration Facility,
        IGraphEventGateway Gateway,
        SheetBookingService BookingService,
        ClubEventService ClubEventService,
        SchedulingWindowService Window);
}
