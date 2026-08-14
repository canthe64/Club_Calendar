using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Services;

/// <summary>
/// Determines whether a signed-in user should get the Staff role claim, checked live against Entra
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
    public const string StaffRoleClaim = "Staff";

    /// <summary>Fails closed: a Graph error (transient outage, throttling) is logged and treated as
    /// "not staff" for that sign-in, never as "staff" - an authorization check should never grant
    /// elevated access as its failure mode. The person still signs in fine; they just don't get the
    /// Staff claim until their next sign-in, by which point the transient issue has typically
    /// cleared.</summary>
    public async Task<bool> IsStaffAsync(string userObjectId, CancellationToken ct = default)
    {
        try
        {
            return await groupGateway.IsMemberOfGroupAsync(userObjectId, facility.StaffGroupId, ct);
        }
        catch (Exception ex)
        {
            await log.LogDebugAsync("StaffGroupCheckFailed", userObjectId, details: ex.Message, ct: ct);
            return false;
        }
    }
}
