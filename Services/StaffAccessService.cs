using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Services;

/// <summary>
/// Determines whether a signed-in user should get the staff claim, checked live against Entra
/// group membership at sign-in (Program.cs's OnTokenValidated hook) rather than relying on Entra's
/// own group-to-app-role assignment feature, which requires an Entra ID P1 license this tenant
/// doesn't have (Entra ID Free only supports assigning individual users to an app role, which would
/// mean an Entra admin action for every single staff change). Ownership of the configured security
/// group can instead be delegated to non-admins, so ongoing staff membership changes need no Entra
/// admin action at all - only the one-time setup (granting GroupMember.Read.All, creating the group)
/// does.
/// </summary>
public class StaffAccessService(IGraphGroupGateway groupGateway, FacilityConfiguration facility, AppLogService log)
{
    /// <summary>
    /// A private, app-owned claim type rather than <see cref="System.Security.Claims.ClaimTypes.Role"/>
    /// plus <c>RequireRole</c>. Live-found 2026-08-12, immediately on first deploy: `RequireRole`
    /// resolves through `ClaimsPrincipal.IsInRole`, which only matches claims whose type equals
    /// `ClaimsIdentity.RoleClaimType` - and Microsoft.Identity.Web overrides that to "roles" for
    /// Entra tokens. A claim added as `ClaimTypes.Role` therefore never matched, no one was ever
    /// Staff, and every staff page denied access. The same library rewrites `NameClaimType` too,
    /// which caused a separate live bug (D71). Matching on an explicit claim type this app fully
    /// controls, via RequireClaim, depends on none of those conventions.
    /// </summary>
    public const string StaffClaimType = "facility:staff";
    public const string StaffClaimValue = "true";

    /// <summary>Fails closed: a Graph error (transient outage, throttling, a missing
    /// GroupMember.Read.All consent) is logged and treated as "not staff" for that sign-in, never as
    /// "staff" - an authorization check should never grant elevated access as its failure mode. The
    /// person still signs in fine; they just don't get the Staff claim until their next sign-in.
    /// Logged at Standard tier, not Debug: this failure locks a real staff member out of every page,
    /// and the log viewer that would explain why is itself behind the staff-only policy, so a
    /// Debug-tier-only record would be invisible exactly when it's needed.</summary>
    public async Task<bool> IsStaffAsync(string userObjectId, CancellationToken ct = default)
    {
        try
        {
            return await groupGateway.IsMemberOfGroupAsync(userObjectId, facility.StaffGroupId, ct);
        }
        catch (Exception ex)
        {
            await log.LogActionAsync("StaffGroupCheckFailed", userObjectId, details: ex.Message, ct: ct);
            return false;
        }
    }
}
