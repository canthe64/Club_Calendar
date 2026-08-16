using System.Security.Claims;
using FacilityScheduler.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace FacilityScheduler.Tests.TestSupport;

public class FakeAuthStateProvider : AuthenticationStateProvider
{
    public string UserName { get; set; } = "tester@example.com";

    /// <summary>Adds the staff claim the real sign-in hook adds for a member of the configured staff
    /// group. Default false so a test has to opt in, matching the app's secure-by-default stance.</summary>
    public bool IsStaff { get; set; }

    /// <summary>The display-name claim, which is what the header greets on - deliberately separate
    /// from UserName, since on a real Entra token those are different values (D71).</summary>
    public string? DisplayName { get; set; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, UserName) };
        if (DisplayName is not null)
        {
            claims.Add(new Claim("name", DisplayName));
        }
        if (IsStaff)
        {
            claims.Add(new Claim(StaffAccessService.StaffClaimType, StaffAccessService.StaffClaimValue));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}
