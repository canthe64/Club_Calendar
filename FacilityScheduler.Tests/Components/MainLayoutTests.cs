using Bunit;
using FacilityScheduler.Components.Layout;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The staff header's menu hides destinations a non-staff member can't use. This is presentation
/// only - every page still enforces its own access (§6.5), so a bug here is a dead link rather than
/// an access hole - but it's worth pinning, because the menu evaluates the *real* StaffOnly policy
/// and a drifting second copy of that rule is precisely what caused the D75 lockout.
/// </summary>
public class MainLayoutTests : BunitContext
{
    private FakeAuthStateProvider Arrange(bool isStaff)
    {
        var auth = new FakeAuthStateProvider { IsStaff = isStaff, DisplayName = "Jane Curler" };
        Services.AddSingleton<AuthenticationStateProvider>(auth);

        // bUnit pre-registers a placeholder IAuthorizationService that throws, and
        // AddAuthorizationCore uses TryAdd - so it would silently never take effect. Drop the
        // placeholder first. Deliberately NOT bUnit's own AddTestAuthorization(): that fakes policy
        // evaluation, and the entire point here is to exercise the real StaffOnly policy the app
        // registers, so the menu can't drift from what the pages actually enforce.
        Services.RemoveAll<IAuthorizationService>();
        Services.AddAuthorizationCore(StaffAuthorizationPolicies.Configure);
        Services.AddCascadingAuthenticationState();
        return auth;
    }

    private IRenderedComponent<MainLayout> RenderWithMenuOpen(bool isStaff)
    {
        Arrange(isStaff);
        var cut = Render<MainLayout>();
        cut.Find("button.app-menu-button").Click();
        return cut;
    }

    private static readonly string[] StaffOnlyLabels =
        ["Staff Calendar", "Club Events", "Practice Ice Approvals", "Settings"];

    // Reachable by anyone signed in - the two anonymous public pages, plus the way back out.
    private static readonly string[] AlwaysVisibleLabels =
        ["Public Calendar", "Practice Ice", "Sign out"];

    [Fact]
    public void StaffMember_SeesEveryDestination()
    {
        var cut = RenderWithMenuOpen(isStaff: true);
        var markup = cut.Find("nav.app-menu-panel").TextContent;

        Assert.All(StaffOnlyLabels, label => Assert.Contains(label, markup));
        Assert.All(AlwaysVisibleLabels, label => Assert.Contains(label, markup));
    }

    [Fact]
    public void NonStaffMember_SeesOnlyWhatTheyCanActuallyUse()
    {
        var cut = RenderWithMenuOpen(isStaff: false);
        var markup = cut.Find("nav.app-menu-panel").TextContent;

        Assert.All(AlwaysVisibleLabels, label => Assert.Contains(label, markup));
        Assert.All(StaffOnlyLabels, label => Assert.DoesNotContain(label, markup));
    }

    [Fact]
    public void NonStaffMember_StaffHrefsAreAbsentEntirely_NotJustHidden()
    {
        // Not merely visually hidden - the anchors must not be in the DOM at all, so the menu can't
        // advertise a route that would deny them.
        var cut = RenderWithMenuOpen(isStaff: false);
        var hrefs = cut.FindAll("nav.app-menu-panel a").Select(a => a.GetAttribute("href")).ToList();

        Assert.DoesNotContain("/calendar", hrefs);
        Assert.DoesNotContain("/club-events", hrefs);
        Assert.DoesNotContain("/practice-ice/approvals", hrefs);
        Assert.DoesNotContain("/settings", hrefs);
    }

    [Fact]
    public void MenuIsClosedUntilTheButtonIsClicked()
    {
        Arrange(isStaff: true);
        var cut = Render<MainLayout>();

        Assert.Empty(cut.FindAll("nav.app-menu-panel"));

        cut.Find("button.app-menu-button").Click();
        Assert.Single(cut.FindAll("nav.app-menu-panel"));
    }

    [Fact]
    public void ClickingTheBackdropClosesTheMenu()
    {
        var cut = RenderWithMenuOpen(isStaff: true);

        cut.Find("div.app-menu-backdrop").Click();

        Assert.Empty(cut.FindAll("nav.app-menu-panel"));
    }

    [Fact]
    public void GreetingUsesTheDisplayNameClaim_NotTheUpn()
    {
        // Identity.Name is the UPN on a real Entra token (D71) - greeting on it would render
        // "Hello, tester@example.com".
        Arrange(isStaff: true);
        var cut = Render<MainLayout>();

        var greeting = cut.Find(".app-greeting").TextContent;

        Assert.Contains("Jane Curler", greeting);
        Assert.DoesNotContain("tester@example.com", greeting);
    }
}
