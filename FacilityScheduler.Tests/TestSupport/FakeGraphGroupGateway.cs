using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>In-memory stand-in for GraphGroupGateway - records queried (userId, groupId) pairs as
/// explicit membership rather than always answering true/false, so a test can catch a hypothetical
/// bug where the wrong group id gets passed.</summary>
public class FakeGraphGroupGateway : IGraphGroupGateway
{
    public Dictionary<string, HashSet<string>> GroupMembers { get; } = new();

    /// <summary>Simulates a Graph-side failure (throttling, a transient outage) so callers can be
    /// tested for the fail-closed behavior StaffAccessService relies on.</summary>
    public bool ThrowOnCheck { get; set; }

    public Task<bool> IsMemberOfGroupAsync(string userId, string groupId, CancellationToken ct = default)
    {
        if (ThrowOnCheck)
        {
            throw new InvalidOperationException("Simulated Graph checkMemberGroups failure.");
        }

        return Task.FromResult(GroupMembers.TryGetValue(groupId, out var members) && members.Contains(userId));
    }
}
