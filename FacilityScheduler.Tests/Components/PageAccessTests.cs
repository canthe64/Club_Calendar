using System.Reflection;
using System.Security.Claims;
using Bunit;
using FacilityScheduler.Components;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// Every page states its own access level. The interactive connection is open to any signed-in user
/// (so member pages work for members outside the staff group), which means the staff-only fallback
/// policy no longer stands between a member and a staff page reached by an in-app link - only the
/// page's own [Authorize], enforced by Routes.razor's AuthorizeRouteView, does.
/// </summary>
public class PageAccessTests : BunitContext
{
    private static readonly Dictionary<Type, string> Expected = new()
    {
        [typeof(Calendar)] = StaffAuthorizationPolicies.StaffOnly,
        [typeof(ClubEvents)] = StaffAuthorizationPolicies.StaffOnly,
        [typeof(EventSearch)] = StaffAuthorizationPolicies.StaffOnly,
        [typeof(PracticeIceApprovals)] = StaffAuthorizationPolicies.StaffOnly,
        [typeof(Settings)] = StaffAuthorizationPolicies.StaffOnly,
        [typeof(PracticeIceRequest)] = StaffAuthorizationPolicies.AnyAuthenticatedUser,
        [typeof(MakeUpGameRequest)] = StaffAuthorizationPolicies.AnyAuthenticatedUser,
        [typeof(MyBookingCancel)] = StaffAuthorizationPolicies.AnyAuthenticatedUser,
        [typeof(NotFound)] = "anonymous",
        [typeof(Error)] = "anonymous",
    };

    private static string AccessOf(Type page)
    {
        if (page.GetCustomAttributes<AllowAnonymousAttribute>().Any())
        {
            return "anonymous";
        }
        var policies = page.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy ?? "(default)").ToList();
        return policies.Count == 1 ? policies[0] : $"[{string.Join(", ", policies)}]";
    }

    [Fact]
    public void EveryPage_DeclaresTheAccessLevelItIsMeantToHave()
    {
        var pages = typeof(App).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .ToDictionary(t => t, AccessOf);

        // A new page fails here until it's added above with a deliberate access level.
        Assert.Equal(Expected.OrderBy(p => p.Key.Name), pages.OrderBy(p => p.Key.Name));
    }

    private sealed class AuthState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private IRenderedComponent<Routes> RenderRoutesAt(string path, ClaimsPrincipal user)
    {
        Services.AddSingleton<AuthenticationStateProvider>(new AuthState(user));
        Services.RemoveAll<IAuthorizationService>();
        Services.AddAuthorizationCore(StaffAuthorizationPolicies.Configure);
        Services.AddCascadingAuthenticationState();
        var facility = TestFacility.Create();
        Services.AddSingleton(facility);
        Services.AddSingleton(new SchedulingWindowService(TestAppLog.Create(facility), new ViewCacheRegistry(new MemoryCache(new MemoryCacheOptions()))));
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        return Render<Routes>();
    }

    [Fact]
    public void ASignedInMember_ReachingAStaffPageInApp_IsToldItIsStaffOnly()
    {
        var member = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "Pat Member")], "TestAuth"));

        var cut = RenderRoutesAt("settings", member);

        Assert.Contains("This page is for calendar staff only.", cut.Markup);
        Assert.DoesNotContain("MicrosoftIdentity/Account/SignIn", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void AnAnonymousVisitor_ReachingAStaffPage_IsSentToSignIn()
    {
        RenderRoutesAt("settings", new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Contains("MicrosoftIdentity/Account/SignIn", Services.GetRequiredService<NavigationManager>().Uri);
    }
}
