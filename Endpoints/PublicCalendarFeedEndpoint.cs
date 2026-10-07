using System.Security.Cryptography;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The public calendar as an iCalendar subscription feed, /public/calendar.ics (operator request
/// 2026-10-05: members subscribed to the old Google calendar and want the same here). A plain
/// Minimal API endpoint like every anonymous surface (D15), built from exactly the public calendar's
/// own view and filter code so the feed and the page always show the same events - the operator's
/// hard requirement, since any difference becomes member questions. The one deliberate difference:
/// a hold's title is prefixed "Hold:", because calendar apps can't show the page's dashed hold style.
///
/// Subscribed calendars refresh on the subscriber's app's schedule (Google: up to a day) and can stop
/// refreshing without telling anyone - a known limitation the page states, not something this
/// endpoint can fix. What it does control: stable event UIDs (no duplicates, and a cancelled event
/// disappears on the next refresh), UTC times (no shifted events), a fast cached response, and its own
/// rate-limit bucket so many calendar servers polling at once aren't refused.
/// </summary>
public static class PublicCalendarFeedEndpoint
{
    public const string Path = "/public/calendar.ics";

    public static void MapPublicCalendarFeedEndpoint(this WebApplication app)
    {
        app.MapGet(Path, async (string? filtered, string[]? categories, string? showClubEvents,
            string? clubFiltered, string[]? clubCategories,
            PublicAvailabilityService service, FacilityConfiguration facility, CancellationToken ct) =>
        {
            var filter = PublicCalendarEndpoint.ParseFilter(filtered, categories, showClubEvents, clubFiltered, clubCategories);
            var view = PublicCalendarEndpoint.ApplyFilter(await service.GetFeedViewAsync(ct), filter);
            return Results.Text(BuildFeed(view, facility, DateTime.UtcNow), "text/calendar; charset=utf-8", Encoding.UTF8);
        })
        .AllowAnonymous()
        .RequireRateLimiting("calendar-feed");
    }

    /// <summary>The feed address for a filter, under <paramref name="baseUrl"/> (no trailing slash).</summary>
    internal static string FeedUrl(string baseUrl, PublicCalendarEndpoint.FilterState filter)
    {
        var query = PublicCalendarEndpoint.FilterQuery(filter);
        return query.Length == 0 ? $"{baseUrl}{Path}" : $"{baseUrl}{Path}?{query[1..]}";
    }

    // internal, not private - reached directly by PublicCalendarFeedTests (D60's precedent).
    internal static string BuildFeed(PublicMonthView view, FacilityConfiguration facility, DateTime generatedUtc)
    {
        var name = string.IsNullOrWhiteSpace(facility.Name) ? "GCC Ice & Event Calendar" : facility.Name;
        var stamp = generatedUtc.ToString("yyyyMMdd'T'HHmmss'Z'");
        var ics = new IcsWriter();

        ics.Line("BEGIN:VCALENDAR");
        ics.Line("VERSION:2.0");
        ics.Line($"PRODID:-//{Escape(name)}//Public Calendar//EN");
        ics.Line("CALSCALE:GREGORIAN");
        ics.Line("METHOD:PUBLISH");
        // Two spellings of the calendar's name: X-WR-CALNAME is what Outlook and Apple read; NAME is the
        // standard property (RFC 7986) newer apps read. Google tends to label a URL subscription with its
        // address regardless - no feed property controls that.
        ics.Line($"X-WR-CALNAME:{Escape(name)}");
        ics.Line($"NAME:{Escape(name)}");
        ics.Line("X-WR-CALDESC:Subscribed copy of the club calendar. It refreshes on your calendar app's schedule and can fall behind - the live calendar is always current.");
        // A refresh hint: Outlook and some others honour it; Google and Apple pick their own schedule.
        ics.Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        ics.Line("X-PUBLISHED-TTL:PT1H");

        // Distinct, as the page does per day: identical labels are one item on the calendar too.
        foreach (var b in view.Bookings.Distinct().OrderBy(b => b.Start))
        {
            var categoryLabel = CalendarStyles.CategoryLabel(PublicCalendarEndpoint.ParseCategory(b.CategoryLabel));
            var description = b.IsConfirmed ? categoryLabel : $"{categoryLabel} - hold, not yet confirmed";
            if (!string.IsNullOrWhiteSpace(b.Notes))
            {
                description += $"\n\n{b.Notes}";
            }

            ics.Line("BEGIN:VEVENT");
            ics.Line($"UID:{Uid("booking", b.Title, b.CategoryLabel, b.Start, b.End)}");
            ics.Line($"DTSTAMP:{stamp}");
            ics.Line($"DTSTART:{Utc(b.Start, facility)}");
            ics.Line($"DTEND:{Utc(b.End, facility)}");
            ics.Line($"SUMMARY:{Escape(b.IsConfirmed ? b.Title : $"Hold: {b.Title}")}");
            ics.Line($"DESCRIPTION:{Escape(description)}");
            ics.Line($"CATEGORIES:{Escape(categoryLabel)}");
            ics.Line($"STATUS:{(b.IsConfirmed ? "CONFIRMED" : "TENTATIVE")}");
            ics.Line("END:VEVENT");
        }

        foreach (var ce in view.ClubEvents.Distinct().OrderBy(ce => ce.Start))
        {
            var categoryLabel = CalendarStyles.ClubEventCategoryLabel(ce.Category);
            var description = ce.MarksSheetsUnavailable ? $"{categoryLabel} - all sheets closed" : categoryLabel;
            if (!string.IsNullOrWhiteSpace(ce.Notes))
            {
                description += $"\n\n{ce.Notes}";
            }

            ics.Line("BEGIN:VEVENT");
            ics.Line($"UID:{Uid("off-ice", ce.Title, ce.Category.ToString(), ce.Start, ce.End)}");
            ics.Line($"DTSTAMP:{stamp}");
            if (ce.IsAllDay)
            {
                // All-day stays all-day; DTEND is exclusive in iCalendar, as ExclusiveEnd already is.
                ics.Line($"DTSTART;VALUE=DATE:{ce.Start:yyyyMMdd}");
                ics.Line($"DTEND;VALUE=DATE:{CalendarStyles.ClubEventExclusiveEnd(ce.End, isAllDay: true):yyyyMMdd}");
            }
            else
            {
                ics.Line($"DTSTART:{Utc(ce.Start, facility)}");
                ics.Line($"DTEND:{Utc(ce.End, facility)}");
            }
            ics.Line($"SUMMARY:{Escape(ce.Title)}");
            ics.Line($"DESCRIPTION:{Escape(description)}");
            ics.Line($"CATEGORIES:{Escape(categoryLabel)}");
            ics.Line("STATUS:CONFIRMED");
            ics.Line("END:VEVENT");
        }

        ics.Line("END:VCALENDAR");
        return ics.ToString();
    }

    // Stable for as long as the event is unchanged, so a refresh updates in place rather than
    // duplicating, and a cancelled event simply drops out. Derived from what the public calendar
    // already shows - a hash, so no internal mailbox or event id is published (D11). Confirming a
    // hold keeps its UID unless its title changes with it.
    private static string Uid(string kind, string title, string category, DateTime start, DateTime end)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{title}|{category}|{start:O}|{end:O}"));
        return $"{Convert.ToHexString(hash, 0, 16).ToLowerInvariant()}@public-calendar";
    }

    // UTC, so no subscriber's app can misread the zone. A wall-clock time skipped by a spring-forward
    // change can't be converted as-is; nudging it an hour later matches how calendars display it.
    private static string Utc(DateTime facilityLocal, FacilityConfiguration facility)
    {
        var local = DateTime.SpecifyKind(facilityLocal, DateTimeKind.Unspecified);
        if (facility.ZoneInfo.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, facility.ZoneInfo).ToString("yyyyMMdd'T'HHmmss'Z'");
    }

    // RFC 5545 TEXT escaping: backslash, semicolon, comma, and line breaks.
    internal static string Escape(string? text) => (text ?? "")
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\n")
        .Replace("\r", "\n")
        .Replace("\n", "\\n");

    /// <summary>Writes content lines with CRLF endings, folding any line over 75 octets onto
    /// continuation lines (RFC 5545 §3.1) without splitting a multi-byte UTF-8 character.</summary>
    private sealed class IcsWriter
    {
        private const int MaxOctets = 75;
        private readonly StringBuilder _sb = new();

        public void Line(string content)
        {
            var current = new StringBuilder();
            var octets = 0;
            var limit = MaxOctets;
            foreach (var rune in content.EnumerateRunes())
            {
                var size = rune.Utf8SequenceLength;
                if (octets + size > limit)
                {
                    _sb.Append(current).Append("\r\n ");
                    current.Clear();
                    octets = 0;
                    limit = MaxOctets - 1; // the leading space counts
                }
                current.Append(rune.ToString());
                octets += size;
            }
            _sb.Append(current).Append("\r\n");
        }

        public override string ToString() => _sb.ToString();
    }
}
