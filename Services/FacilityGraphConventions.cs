namespace FacilityScheduler.Services;

/// <summary>
/// Shared Graph-writing conventions used by every service that writes to a facility resource
/// mailbox (sheets, Club Events): the facility's fixed local time zone, and the extended-property
/// namespace this app uses for its own custom fields.
/// </summary>
public static class FacilityGraphConventions
{
    // All hours entered in the UI are the facility's own local wall-clock time (e.g. "6 PM" means
    // 6 PM at the club, not UTC). Every write tags Start/End/RecurrenceTimeZone with this zone, and
    // every read sets the matching Prefer: outlook.timezone header, so Graph does all the UTC
    // conversion internally and the app never has to - it only ever sees local wall time.
    public const string FacilityTimeZone = "Pacific Standard Time";
    public static readonly TimeZoneInfo FacilityZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(FacilityTimeZone);

    // calendarView's startDateTime/endDateTime query parameters are interpreted as UTC by Graph
    // when no explicit offset is present - unlike Start/End on an event body, they are NOT
    // reinterpreted per the Prefer: outlook.timezone header. A bare "facility wall-clock" string
    // sent as-is gets silently read as UTC, shifting the whole query window by the zone's offset.
    // Converting to a real UTC instant first (Kind=Utc, so "o" emits a "Z") removes the ambiguity.
    public static string ToUtcQueryString(DateTime facilityLocalTime) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(facilityLocalTime, DateTimeKind.Unspecified), FacilityZoneInfo).ToString("o");

    // Fixed GUID namespace for this app's custom extended properties, shared across every mailbox
    // type (sheets, Club Events) so they're all recognizable as belonging to the same app.
    public const string PropertyGuid = "c11ff204-d3f3-4a0c-9639-d915f8c8a3e3";
}
