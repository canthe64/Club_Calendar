using Bunit;
using Microsoft.Extensions.DependencyInjection;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Components;

public class ScheduleTests : BunitContext
{
    private (FacilityConfiguration Facility, FakeGraphEventGateway Gateway) RegisterServices()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        Services.AddSingleton(facility);
        Services.AddSingleton<SheetBookingService>(new SheetBookingService(gateway, new MemoryCache(new MemoryCacheOptions()), facility, TestAppLog.Create()));
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
        return (facility, gateway);
    }

    [Fact]
    public void OnInitializedAsync_DefaultsSelectedDateToFacilityToday_NotDateTimeMinValue()
    {
        // Regression coverage for the field-initializer/DI-timing bug class (@inject properties are
        // set AFTER construction, so a field initializer referencing Facility can't run before
        // OnInitializedAsync) - if SelectedDate ever regressed to its DateTime.MinValue placeholder,
        // this would render "Monday, January 1, 0001" instead of the facility's real today.
        var (facility, _) = RegisterServices();
        var cut = Render<Schedule>();

        Assert.Contains(facility.Today.ToString("dddd, MMMM d, yyyy"), cut.Markup);
        Assert.DoesNotContain("0001", cut.Markup);
    }

    [Fact]
    public void OnInitializedAsync_DefaultsSelectedSheetToFirstConfiguredSheet()
    {
        var (_, _) = RegisterServices();
        var cut = Render<Schedule>();

        var firstOption = cut.Find("select").Children.First();
        Assert.Equal(TestFacility.SheetMailboxes[0], firstOption.GetAttribute("value"));
    }

    [Fact]
    public void NoBookingsForSelectedDay_ShowsEmptyStateMessage()
    {
        RegisterServices();
        var cut = Render<Schedule>();

        Assert.Contains("No bookings for this sheet on this day.", cut.Markup);
    }

    [Fact]
    public async Task SubmittingNewBookingForm_CreatesHoldOnSelectedSheet()
    {
        var (facility, gateway) = RegisterServices();
        var cut = Render<Schedule>();

        var newBookingButton = cut.FindAll("button").First(b => b.TextContent.Contains("New booking"));
        await cut.InvokeAsync(() => newBookingButton.Click());

        await cut.InvokeAsync(() => cut.Find("form").Submit());

        var sheet = TestFacility.SheetMailboxes[0];
        cut.WaitForAssertion(() => Assert.DoesNotContain("class=\"create-form\"", cut.Markup)); // form closed after successful save
        Assert.Single(gateway.Events(sheet));
    }

    [Fact]
    public async Task SubmittingConflictingBooking_ShowsConflictInsteadOfClosingForm()
    {
        var (facility, gateway) = RegisterServices();
        var sheet = TestFacility.SheetMailboxes[0];

        // Pre-existing booking exactly overlapping the create form's default 6pm-8pm window, seeded
        // before the component's own OnInitializedAsync fetch runs.
        gateway.Seed(sheet, new Microsoft.Graph.Models.Event
        {
            Subject = "League", ShowAs = Microsoft.Graph.Models.FreeBusyStatus.Busy,
            Categories = ["League"],
            Start = TestFacility.Dtz(facility.Today.AddHours(18)), End = TestFacility.Dtz(facility.Today.AddHours(20))
        });

        var cut = Render<Schedule>();
        var newBookingButton = cut.FindAll("button").First(b => b.TextContent.Contains("New booking"));
        await cut.InvokeAsync(() => newBookingButton.Click());
        await cut.InvokeAsync(() => cut.Find("form").Submit());

        Assert.Contains("Conflict - slot not available", cut.Markup);
        Assert.Single(gateway.Events(sheet)); // only the pre-existing booking - nothing new written
    }
}
