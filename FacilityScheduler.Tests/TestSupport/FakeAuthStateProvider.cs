using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace FacilityScheduler.Tests.TestSupport;

public class FakeAuthStateProvider : AuthenticationStateProvider
{
    public string UserName { get; set; } = "tester@example.com";

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName)], "TestAuth");
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}
