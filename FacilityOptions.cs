namespace FacilityScheduler;

public class FacilityOptions
{
    public const string SectionName = "Facility";

    public string TenantDomain { get; set; } = string.Empty;
    public string[] SheetMailboxLocalParts { get; set; } = [];
    public string ClubEventsMailboxLocalPart { get; set; } = "clubevents";
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Display name for the facility. Inert for now - accepted and stored, but not yet
    /// wired into any UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Relative path under wwwroot to the facility's logo image (e.g. "/branding/logo.png").
    /// Inert for now - accepted and stored, but not yet wired into any UI.</summary>
    public string? LogoPath { get; set; }

    /// <summary>The app's public address for this environment (e.g. "https://calendar.example.org"),
    /// used to build links in emails - a member's "cancel this booking" link. The app can't work this
    /// out itself: emails are sent from whichever host the booking happened on, which may not be the
    /// address members should use. Optional - left blank, emails go out without the link.</summary>
    public string? PublicBaseUrl { get; set; }
}
