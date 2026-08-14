using Microsoft.Graph;
using Microsoft.Graph.Users.Item.CheckMemberGroups;

namespace FacilityScheduler.Services.Graph;

/// <summary>The only class in this app that talks to GraphServiceClient directly for directory/group
/// operations - see IGraphGroupGateway for why this boundary exists. Requires the
/// GroupMember.Read.All application permission - unlike Calendars.ReadWrite/Mail.Send, this is a
/// directory (Entra ID) operation, not an Exchange Online mailbox one, so it is NOT subject to
/// Application Access Policy scoping (deployment guide §2.5).</summary>
public class GraphGroupGateway(GraphServiceClient graphClient) : IGraphGroupGateway
{
    public async Task<bool> IsMemberOfGroupAsync(string userId, string groupId, CancellationToken ct = default)
    {
        var response = await graphClient.Users[userId].CheckMemberGroups.PostAsCheckMemberGroupsPostResponseAsync(new CheckMemberGroupsPostRequestBody
        {
            GroupIds = [groupId]
        }, cancellationToken: ct);

        return response?.Value?.Contains(groupId) ?? false;
    }
}
