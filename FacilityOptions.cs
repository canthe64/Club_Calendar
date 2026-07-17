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
}
