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

    public int PracticeIceEligibleStartHour { get; }
    public int PracticeIceEligibleEndHour { get; }
    public int PracticeIceMinLeadHours { get; }
    public int PracticeIceMaxHorizonDays { get; }
    public string PracticeIceApproverEmail { get; }
    public string PracticeIceMailerMailbox { get; }

    /// <summary>False until both the approver distribution address and the mailer mailbox are set
    /// (deployment-time config, deliberately left blank until then - see PracticeIceOptions). The
    /// request flow checks this and blocks submission with an explicit message rather than silently
    /// writing a hold nobody gets notified about.</summary>
    public bool PracticeIceMailConfigured =>
        !string.IsNullOrWhiteSpace(PracticeIceApproverEmail) && !string.IsNullOrWhiteSpace(PracticeIceMailerMailbox);

    /// <summary>Entra object id of the security group whose members get the staff claim
    /// (Services/StaffAccessService.cs). Load-bearing, unlike the PracticeIce mail addresses above -
    /// leaving it unset wouldn't just disable a feature, it would lock everyone (including real
    /// staff) out of every staff page under the app's Staff-only fallback authorization policy.</summary>
    public string StaffGroupId { get; }

    public FacilityConfiguration(IOptions<FacilityOptions> options, IOptions<PracticeIceOptions> practiceIceOptions, IOptions<StaffAccessOptions> staffAccessOptions)
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
        if (string.IsNullOrWhiteSpace(staffAccessOptions.Value.StaffGroupId))
        {
            throw new InvalidOperationException("StaffAccess:StaffGroupId is not configured.");
        }

        StaffGroupId = staffAccessOptions.Value.StaffGroupId;

        SheetMailboxes = o.SheetMailboxLocalParts.Select(p => $"{p}@{o.TenantDomain}").ToArray();
        ClubEventsMailbox = $"{o.ClubEventsMailboxLocalPart}@{o.TenantDomain}";
        TimeZone = o.TimeZone;
        ZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone);
        Name = o.Name;
        LogoPath = o.LogoPath;

        var pi = practiceIceOptions.Value;

        // Unlike TenantDomain/SheetMailboxLocalParts/TimeZone above, the approver email and mailer
        // mailbox are allowed to be blank at startup (PracticeIceMailConfigured then gates the
        // feature at request time) - an incremental feature shouldn't stop an already-running
        // deployment from booting just because its notification path hasn't been configured yet.
        // The hours/lead/horizon values ARE validated here, since a nonsensical value (start after
        // end, negative lead time) would be a config mistake worth catching immediately rather than
        // producing a silently-empty or silently-wrong availability window at request time.
        if (pi.EligibleStartHour < 0 || pi.EligibleStartHour > 23 || pi.EligibleEndHour < 1 || pi.EligibleEndHour > 24
            || pi.EligibleStartHour >= pi.EligibleEndHour)
        {
            throw new InvalidOperationException("PracticeIce:EligibleStartHour/EligibleEndHour must be a valid 0-24 range with Start < End.");
        }
        if (pi.MinLeadHours < 0)
        {
            throw new InvalidOperationException("PracticeIce:MinLeadHours cannot be negative.");
        }
        if (pi.MaxHorizonDays < 1)
        {
            throw new InvalidOperationException("PracticeIce:MaxHorizonDays must be at least 1.");
        }

        PracticeIceEligibleStartHour = pi.EligibleStartHour;
        PracticeIceEligibleEndHour = pi.EligibleEndHour;
        PracticeIceMinLeadHours = pi.MinLeadHours;
        PracticeIceMaxHorizonDays = pi.MaxHorizonDays;
        PracticeIceApproverEmail = pi.ApproverDistributionEmail;
        PracticeIceMailerMailbox = pi.MailerMailbox;
    }

    // calendarView's startDateTime/endDateTime query parameters are interpreted as UTC by Graph
    // when no explicit offset is present - unlike Start/End on an event body, they are NOT
    // reinterpreted per the Prefer: outlook.timezone header. Converting to a real UTC instant first
    // (Kind=Utc, so "o" emits a "Z") removes the ambiguity.
    public string ToUtcQueryString(DateTime facilityLocalTime) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(facilityLocalTime, DateTimeKind.Unspecified), ZoneInfo).ToString("o");

    // "Today"/"now" in facility-local wall-clock time - DateTime.UtcNow.Date is NOT the same thing
    // and using it directly was a live-found bug (2026-08-04): from ~5pm PDT/4pm PST onward, UTC has
    // already rolled to tomorrow, so anything anchored on DateTime.UtcNow.Date (calendar "Today"
    // buttons, new-booking defaults, the public availability window's start) silently shifted a day
    // ahead during the facility's own evening hours - exactly when curling ice is busiest. Every
    // "today"/"now" in the app should route through these instead of calling DateTime.UtcNow directly.
    public DateTime Today => Now.Date;
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ZoneInfo);

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
