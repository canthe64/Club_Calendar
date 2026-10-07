using System.Security.Claims;
using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void MemberIdentity_PrefersTheNameClaim_AndPreferredUsernameForEmail()
    {
        // Shaped like a real Entra principal: Identity.Name is the UPN (D71), so the display name has
        // to come from the "name" claim.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("name", "Jane Curler"),
            new Claim("preferred_username", "jane@example.com")
        ], "TestAuth", "preferred_username", "roles"));

        Assert.Equal(("Jane Curler", "jane@example.com"), user.MemberIdentity());
    }
}
