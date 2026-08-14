using Microsoft.AspNetCore.Authorization;

namespace FacilityScheduler.Services;

/// <summary>
/// The app's authorization policies, defined here rather than inline in Program.cs specifically so
/// tests can evaluate the real policy objects against real ClaimsPrincipals. The first version of
/// this lived inline and used ClaimTypes.Role + RequireRole, which silently never matched under
/// Microsoft.Identity.Web's overridden RoleClaimType (see StaffAccessService.StaffClaimType) - a
/// bug no unit test could catch while the policies weren't reachable from the test project.
/// </summary>
public static class StaffAuthorizationPolicies
{
    /// <summary>Signed in AND a member of the configured staff group. The app's default for every
    /// page/endpoint that doesn't explicitly opt out.</summary>
    public const string StaffOnly = "StaffOnly";

    /// <summary>Signed in, staff or not - the single deliberate carve-out, used only by the practice
    /// ice request page members need to reach (architecture doc §6.5).</summary>
    public const string AnyAuthenticatedUser = "AnyAuthenticatedUser";

    public static AuthorizationPolicy BuildStaffOnly() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(StaffAccessService.StaffClaimType, StaffAccessService.StaffClaimValue)
            .Build();

    public static AuthorizationPolicy BuildAnyAuthenticatedUser() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

    /// <summary>Wires both policies plus the Staff-only fallback. Called by Program.cs; called
    /// directly by tests so what's asserted is exactly what runs in production.</summary>
    public static void Configure(AuthorizationOptions options)
    {
        var staffOnly = BuildStaffOnly();
        options.FallbackPolicy = staffOnly;
        options.AddPolicy(StaffOnly, staffOnly);
        options.AddPolicy(AnyAuthenticatedUser, BuildAnyAuthenticatedUser());
    }
}
