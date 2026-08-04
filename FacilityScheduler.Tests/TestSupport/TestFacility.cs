using FacilityScheduler;
using FacilityScheduler.Services;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Builds a real FacilityConfiguration against fixed test values - same Windows time zone
/// ID convention ("Pacific Standard Time") the real deployment uses (appsettings.Development.json),
/// so DST-boundary tests exercise the exact TimeZoneInfo lookup production relies on.</summary>
public static class TestFacility
{
    public const string TenantDomain = "test.onmicrosoft.com";
    public static readonly string[] SheetLocalParts = ["sheet1", "sheet2", "sheet3"];
    public const string ClubEventsLocalPart = "clubevents";
    public const string TimeZoneId = "Pacific Standard Time";

    public static readonly string[] SheetMailboxes = [.. SheetLocalParts.Select(p => $"{p}@{TenantDomain}")];
    public static readonly string ClubEventsMailbox = $"{ClubEventsLocalPart}@{TenantDomain}";

    public static FacilityConfiguration Create(string[]? sheetLocalParts = null, string timeZoneId = TimeZoneId) =>
        new(Options.Create(new FacilityOptions
        {
            TenantDomain = TenantDomain,
            SheetMailboxLocalParts = sheetLocalParts ?? SheetLocalParts,
            ClubEventsMailboxLocalPart = ClubEventsLocalPart,
            TimeZone = timeZoneId,
            Name = "Test Facility"
        }));

    /// <summary>Builds a Graph DateTimeTimeZone the same way the app's own ToGraphEvent does -
    /// a bare "s"-format local wall-clock string plus the facility's configured zone id.</summary>
    public static DateTimeTimeZone Dtz(DateTime facilityLocalTime) =>
        new() { DateTime = facilityLocalTime.ToString("s"), TimeZone = TimeZoneId };
}
