namespace FacilityScheduler.Services;

/// <summary>
/// Shared Graph-writing conventions used by every service that writes to a facility resource
/// mailbox (sheets, Club Events). The facility's local time zone lives in FacilityConfiguration
/// (tenant-specific, configuration-driven) - this class holds only the one thing that's genuinely
/// constant across every deployment: the extended-property namespace this app uses for its own
/// custom fields.
/// </summary>
public static class FacilityGraphConventions
{
    // Fixed GUID namespace for this app's custom extended properties, shared across every mailbox
    // type (sheets, Club Events) so they're all recognizable as belonging to the same app.
    public const string PropertyGuid = "c11ff204-d3f3-4a0c-9639-d915f8c8a3e3";
}
