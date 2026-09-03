using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The shared "contact Tech Committee" footer (operator request, 2026-09-04), covering all three
/// staff pages that carry it (Calendar, Search, Settings) via one PageFooter.razor component - one
/// test class here rather than the same assertion spread across each page's own test file. The three
/// anonymous public pages that carry the identical line are covered separately
/// (PublicPageFooterTests, since they're hand-built HTML strings, not a Blazor component).
/// </summary>
public class PageFooterTests : BunitContext
{
    private const string ExpectedLink = """<a href="mailto:techcommittee@curlingseattle.org" """;
    private const string ExpectedText = "For problems or questions with this page, contact";

    [Fact]
    public void Calendar_ShowsTheFooter()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        Assert.Contains(ExpectedText, cut.Markup);
        Assert.Contains(ExpectedLink, cut.Markup);
    }

    [Fact]
    public void EventSearch_ShowsTheFooter()
    {
        StaffPageServices.Register(this);

        var cut = Render<EventSearch>();

        Assert.Contains(ExpectedText, cut.Markup);
        Assert.Contains(ExpectedLink, cut.Markup);
    }

    [Fact]
    public void Settings_ShowsTheFooter()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var logService = TestAppLog.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(logService, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, logService, viewCache, window);
        Services.AddSingleton(logService);
        Services.AddSingleton(bookingService);
        Services.AddSingleton(window);
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        var cut = Render<Settings>();

        Assert.Contains(ExpectedText, cut.Markup);
        Assert.Contains(ExpectedLink, cut.Markup);
    }
}
