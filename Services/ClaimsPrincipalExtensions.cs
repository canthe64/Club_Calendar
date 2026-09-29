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

    /// <summary>
    /// The name and email a member books with (practice ice, make-up games) and later cancels with.
    /// One definition on purpose: cancelling checks the signed-in email against the email stored on
    /// the booking, so booking and cancelling must resolve it identically - two copies of this rule
    /// drifting apart would lock members out of their own bookings. Identity comes from the token
    /// only, never from anything typed (docs/practice-ice-hosting-design.md §3.3); "name" first,
    /// for the same UPN reason as <see cref="DisplayName"/>.
    /// </summary>
    public static (string Name, string Email) MemberIdentity(this ClaimsPrincipal user)
    {
        var name = user.FindFirst("name")?.Value ?? user.Identity?.Name ?? "Unknown";
        var email = user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.Identity?.Name
            ?? string.Empty;
        return (name, email);
    }
}
