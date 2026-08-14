using System.Security.Claims;
using FacilityScheduler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// Evaluates the real authorization policies (StaffAuthorizationPolicies, exactly as Program.cs
/// registers them) against real ClaimsPrincipals.
///
/// These exist because of a live-found bug (2026-08-12, caught only on first deploy): the policies
/// were originally built inline in Program.cs with ClaimTypes.Role + RequireRole, while the sign-in
/// hook added the claim as ClaimTypes.Role. That looks self-consistent, but RequireRole resolves via
/// ClaimsPrincipal.IsInRole, which matches only claims whose type equals ClaimsIdentity.RoleClaimType
/// - and Microsoft.Identity.Web sets that to "roles" for Entra tokens. The claim was added and never
/// matched: no one was ever staff, and every staff page denied access. StaffPrincipal below
/// deliberately sets RoleClaimType to "roles" to reproduce that exact environment.
/// </summary>
public class StaffAuthorizationPolicyTests
{
    // Mirrors how Microsoft.Identity.Web shapes an Entra principal: a non-default RoleClaimType, and
    // a NameClaimType of "preferred_username" (the same override behind the D71 UPN bug).
    private static ClaimsPrincipal Principal(bool authenticated, bool staff)
    {
        var claims = new List<Claim> { new("preferred_username", "someone@example.com") };
        if (staff)
        {
            claims.Add(new Claim(StaffAccessService.StaffClaimType, StaffAccessService.StaffClaimValue));
        }

        var identity = authenticated
            ? new ClaimsIdentity(claims, "TestAuth", "preferred_username", "roles")
            : new ClaimsIdentity(claims, null, "preferred_username", "roles");

        return new ClaimsPrincipal(identity);
    }

    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(StaffAuthorizationPolicies.Configure);
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    [Fact]
    public async Task StaffOnly_SignedInStaffMember_IsAllowed()
    {
        var authz = BuildAuthorizationService();

        var result = await authz.AuthorizeAsync(Principal(authenticated: true, staff: true), null, StaffAuthorizationPolicies.StaffOnly);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task StaffOnly_SignedInNonStaffMember_IsDenied()
    {
        var authz = BuildAuthorizationService();

        var result = await authz.AuthorizeAsync(Principal(authenticated: true, staff: false), null, StaffAuthorizationPolicies.StaffOnly);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task StaffOnly_AnonymousUser_IsDenied()
    {
        var authz = BuildAuthorizationService();

        var result = await authz.AuthorizeAsync(Principal(authenticated: false, staff: false), null, StaffAuthorizationPolicies.StaffOnly);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AnyAuthenticatedUser_SignedInNonStaffMember_IsAllowed()
    {
        // The practice ice request carve-out - the whole point is that a member who is deliberately
        // NOT staff can still reach this one page.
        var authz = BuildAuthorizationService();

        var result = await authz.AuthorizeAsync(Principal(authenticated: true, staff: false), null, StaffAuthorizationPolicies.AnyAuthenticatedUser);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AnyAuthenticatedUser_AnonymousUser_IsDenied()
    {
        var authz = BuildAuthorizationService();

        var result = await authz.AuthorizeAsync(Principal(authenticated: false, staff: false), null, StaffAuthorizationPolicies.AnyAuthenticatedUser);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task FallbackPolicy_IsTheStaffOnlyPolicy_NotMerelyAuthenticated()
    {
        // The fallback is what actually guards every page with no explicit attribute (Calendar,
        // Settings, Club Events, practice ice approvals), so it must be the strict one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(StaffAuthorizationPolicies.Configure);
        var options = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value;

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements, r => r is ClaimsAuthorizationRequirement c
            && c.ClaimType == StaffAccessService.StaffClaimType);
    }
}
