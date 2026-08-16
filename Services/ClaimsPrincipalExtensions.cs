using System.Security.Claims;

namespace FacilityScheduler.Services;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The signed-in user's human-readable display name ("Charlie Anthe"), for greeting them in the
    /// UI. Deliberately NOT <c>Identity.Name</c>: Microsoft.Identity.Web overrides
    /// <c>NameClaimType</c> to "preferred_username", so on an Entra token that property returns the
    /// UPN (charlie@example.org) rather than a name - live-found 2026-08-09 when it reached both the
    /// audit log and a public booking title (architecture doc D71). The explicit "name" claim is the
    /// one carrying the real display name, so it's checked first and Identity.Name is only a
    /// last-resort fallback.
    ///
    /// Audit logging deliberately still uses Identity.Name (the UPN): for "who did this", an
    /// unambiguous account identifier beats a display name that several people could share.
    /// </summary>
    public static string DisplayName(this ClaimsPrincipal? user) =>
        user?.FindFirst("name")?.Value
        ?? user?.Identity?.Name
        ?? "there";
}
