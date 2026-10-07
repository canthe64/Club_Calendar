using Bunit;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>The service graph every staff Blazor page needs, registered into a BunitContext against
/// in-memory fakes. Registered as a whole, once, so adding an injection to a page under test can't
/// silently depend on which test class renders it.</summary>
public static class StaffPageServices
{
    /// <param name="decorate">Wraps the fake gateway - pass a <see cref="DelegatingGraphEventGateway"/>
    /// subclass when the test needs to observe or break Graph traffic.</param>
    public static ServiceHarness Register(BunitContext ctx, Func<FakeGraphEventGateway, IGraphEventGateway>? decorate = null)
    {
        var harness = ServiceHarness.Create(decorate: decorate);

        ctx.Services.AddSingleton(harness.Facility);
        ctx.Services.AddSingleton(harness.Bookings);
        ctx.Services.AddSingleton(harness.ClubEvents);
        ctx.Services.AddSingleton(harness.Window);
        ctx.Services.AddSingleton(harness.AppLog);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        return harness;
    }
}
