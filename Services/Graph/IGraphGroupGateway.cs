namespace FacilityScheduler.Services.Graph;

/// <summary>
/// Thin wrapper over Graph's checkMemberGroups action, mirroring IGraphEventGateway's reason for
/// existing - lets tests substitute a fake instead of driving GraphServiceClient directly.
/// </summary>
public interface IGraphGroupGateway
{
    /// <summary>True if the given user (Entra object id) is a member of the given group (Entra
    /// object id), including transitive (nested-group) membership.</summary>
    Task<bool> IsMemberOfGroupAsync(string userId, string groupId, CancellationToken ct = default);
}
