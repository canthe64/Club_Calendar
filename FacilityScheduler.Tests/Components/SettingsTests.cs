using Bunit;
using Microsoft.Extensions.DependencyInjection;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Components;

public class SettingsTests : BunitContext
{
    private (SheetBookingService BookingService, AppLogService LogService) RegisterServices()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var logService = TestAppLog.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var bookingService = new SheetBookingService(gateway, cache, facility, logService, new ViewCacheRegistry(cache));

        Services.AddSingleton(logService);
        Services.AddSingleton(bookingService);
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        return (bookingService, logService);
    }

    [Fact]
    public void Render_DebugNotSelected_NoPiiWarningBanner()
    {
        RegisterServices();
        var cut = Render<Settings>();

        Assert.DoesNotContain("may log customer names, emails, and phone numbers", cut.Markup);
    }

    [Fact]
    public void SelectingDebug_ShowsPiiWarningBannerImmediately()
    {
        RegisterServices();
        var cut = Render<Settings>();

        var debugRadio = cut.FindAll("input[type=radio]")[1]; // Standard, Debug - in that DOM order
        debugRadio.Change(true);

        Assert.Contains("may log customer names, emails, and phone numbers", cut.Markup);
    }

    [Fact]
    public async Task SavingDebugLevel_PersistsToAppLogService()
    {
        var (_, logService) = RegisterServices();
        var cut = Render<Settings>();

        var debugRadio = cut.FindAll("input[type=radio]")[1];
        debugRadio.Change(true);

        var saveButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Save").ToList();
        await cut.InvokeAsync(() => saveButtons[0].Click());

        Assert.Equal(AppLogLevel.Debug, logService.CurrentLevel);
    }

    [Fact]
    public void Render_MinimumIntervalDropdown_DefaultsToPersistedValue()
    {
        var (bookingService, _) = RegisterServices();
        var cut = Render<Settings>();

        var select = cut.Find("select");
        var selectedOption = select.Children.Single(o => o.HasAttribute("selected"));
        Assert.Equal(bookingService.MinimumGroupEventBookingIntervalMinutes.ToString(), selectedOption.GetAttribute("value"));
    }

    [Fact]
    public async Task SavingMinimumInterval_PersistsToSheetBookingService()
    {
        var (bookingService, _) = RegisterServices();
        var cut = Render<Settings>();

        var select = cut.Find("select");
        select.Change("90");

        var saveButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Save").ToList();
        await cut.InvokeAsync(() => saveButtons[1].Click());

        Assert.Equal(90, bookingService.MinimumGroupEventBookingIntervalMinutes);
    }
}
