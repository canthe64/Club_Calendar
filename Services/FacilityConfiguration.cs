using Microsoft.Extensions.Options;

namespace FacilityScheduler.Services;

/// <summary>
/// This deployment's own tenant-specific configuration - which mailboxes are the ice sheets vs.
/// Club Events, and what local time zone the facility operates in. Everything here comes from
/// configuration (appsettings/environment/user-secrets), never hardcoded, so the exact same
/// deployed app can be repointed at a different tenant - or stood up fresh for a different facility
/// entirely - by changing config alone, no code change or recompile.
/// </summary>
public class FacilityConfiguration
{
    public string[] SheetMailboxes { get; }
    public string ClubEventsMailbox { get; }
    public string TimeZone { get; }
    public TimeZoneInfo ZoneInfo { get; }
    public string Name { get; }
    public string? LogoPath { get; }

    public FacilityConfiguration(IOptions<FacilityOptions> options)
    {
        var o = options.Value;

        // Fail fast at startup rather than silently defaulting (e.g. to UTC) - this app has already
        // shipped two real bugs from wrong timezone assumptions (see project history), so a
        // misconfigured deployment should error immediately, not limp along wrong.
        if (string.IsNullOrWhiteSpace(o.TenantDomain))
        {
            throw new InvalidOperationException("Facility:TenantDomain is not configured.");
        }
        if (o.SheetMailboxLocalParts.Length == 0)
        {
            throw new InvalidOperationException("Facility:SheetMailboxLocalParts is not configured.");
        }
        if (string.IsNullOrWhiteSpace(o.TimeZone))
        {
            throw new InvalidOperationException("Facility:TimeZone is not configured.");
        }

        SheetMailboxes = o.SheetMailboxLocalParts.Select(p => $"{p}@{o.TenantDomain}").ToArray();
        ClubEventsMailbox = $"{o.ClubEventsMailboxLocalPart}@{o.TenantDomain}";
        TimeZone = o.TimeZone;
        ZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone);
        Name = o.Name;
        LogoPath = o.LogoPath;
    }

    // calendarView's startDateTime/endDateTime query parameters are interpreted as UTC by Graph
    // when no explicit offset is present - unlike Start/End on an event body, they are NOT
    // reinterpreted per the Prefer: outlook.timezone header. Converting to a real UTC instant first
    // (Kind=Utc, so "o" emits a "Z") removes the ambiguity.
    public string ToUtcQueryString(DateTime facilityLocalTime) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(facilityLocalTime, DateTimeKind.Unspecified), ZoneInfo).ToString("o");

    // The read-side counterpart. Originally relied on a Prefer: outlook.timezone header to have
    // Graph return Start/End already converted to facility-local wall-clock digits - live-confirmed
    // 2026-07-16 to be unreliable specifically for occurrences of a recurring series expanded out of
    // a wide calendarView window (correct for a narrow week-sized window, off by exactly the
    // facility's UTC offset for the same occurrence read via a month-sized window). Rather than
    // depend on Graph applying that header consistently across every query shape, callers now omit
    // the Prefer header entirely - Graph's documented fallback for an omitted header is to return
    // Start/End in plain UTC - and this method does the UTC-to-facility-local conversion here,
    // ourselves, deterministically, the same way ToUtcQueryString already does the reverse.
    public DateTime FromUtcResponseString(string utcDateTimeString) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(DateTime.Parse(utcDateTimeString, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc), ZoneInfo);
}
