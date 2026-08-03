using FacilityScheduler.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Collections.Concurrent;

namespace FacilityScheduler.Services;

/// <summary>
/// Owns the single dedicated Club Events mailbox (architecture doc §4.4, D13). Deliberately much
/// simpler than SheetBookingService - one low-volume mailbox, no per-sheet locking, no
/// BookingGroupId/multi-sheet grouping concept, and no conflict checking at all (neither against
/// sheet bookings nor between club events themselves) - build-simplicity is the explicit design
/// choice here, not an oversight.
/// </summary>
public class ClubEventService(GraphServiceClient graphClient, IMemoryCache cache, FacilityConfiguration facility, AppLogService log)
{
    private const string PropertyGuid = FacilityGraphConventions.PropertyGuid;
    private const string BookedByPropertyId = $"String {{{PropertyGuid}}} Name ClubEventBookedBy";
    private const string MarksUnavailablePropertyId = $"String {{{PropertyGuid}}} Name MarksSheetsUnavailable";

    private static readonly string[] ExtendedPropertiesExpand =
    [
        $"singleValueExtendedProperties($filter=id eq '{BookedByPropertyId}' or id eq '{MarksUnavailablePropertyId}')"
    ];

    // Phase 7: same short-TTL read cache as SheetBookingService.GetBookingsForAllSheetsAsync, for the
    // same reason - this service has no conflict-check path at all (D13), so unlike SheetBookingService
    // there's no "never cache this" method to carve out; the whole read surface is just GetEventsAsync.
    private readonly ConcurrentDictionary<string, byte> _viewCacheKeys = new();
    private static readonly TimeSpan ViewCacheTtl = TimeSpan.FromSeconds(30);

    private void InvalidateViewCache()
    {
        foreach (var key in _viewCacheKeys.Keys)
        {
            cache.Remove(key);
        }
        _viewCacheKeys.Clear();
    }

    public async Task<ClubEvent> CreateAsync(ClubEvent clubEvent, string actingUser, CancellationToken ct = default)
    {
        var graphEvent = ToGraphEvent(clubEvent);
        var created = await graphClient.Users[facility.ClubEventsMailbox].Events.PostAsync(graphEvent, cancellationToken: ct);
        clubEvent.EventId = created?.Id;
        clubEvent.ICalUId = created?.ICalUId;
        InvalidateViewCache();
        await log.LogActionAsync("ClubEventCreated", actingUser, clubEvent.EventId, sheet: null,
            details: $"{clubEvent.Category} \"{clubEvent.Title}\", {clubEvent.Start:d}-{clubEvent.End:d}", ct: ct);
        return clubEvent;
    }

    public async Task UpdateAsync(ClubEvent clubEvent, string actingUser, CancellationToken ct = default)
    {
        var graphEvent = ToGraphEvent(clubEvent);
        await graphClient.Users[facility.ClubEventsMailbox].Events[clubEvent.EventId!].PatchAsync(graphEvent, cancellationToken: ct);
        InvalidateViewCache();
        await log.LogActionAsync("ClubEventUpdated", actingUser, clubEvent.EventId, sheet: null,
            details: $"{clubEvent.Category} \"{clubEvent.Title}\", {clubEvent.Start:d}-{clubEvent.End:d}", ct: ct);
    }

    public async Task CancelAsync(string eventId, string actingUser, CancellationToken ct = default)
    {
        await graphClient.Users[facility.ClubEventsMailbox].Events[eventId].DeleteAsync(cancellationToken: ct);
        InvalidateViewCache();
        await log.LogActionAsync("ClubEventCancelled", actingUser, eventId, ct: ct);
    }

    /// <summary>Read-cached (Phase 7, 30s TTL) - used by Calendar.razor, ClubEvents.razor, both
    /// PublicAvailabilityService methods, and Calendar.razor's closure-conflict check. A brief cache
    /// window here is acceptable even for the closure check: invalidation fires the instant a closure
    /// is created through this app, so staleness only applies to a closure added directly in Outlook -
    /// the same caveat that already applies to every other cached read.</summary>
    public async Task<List<ClubEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        var cacheKey = $"clubevents:{start:O}:{end:O}";
        if (cache.TryGetValue(cacheKey, out List<ClubEvent>? cached) && cached is not null)
        {
            return cached;
        }

        var allEvents = new List<Event>();
        var response = await graphClient.Users[facility.ClubEventsMailbox].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = facility.ToUtcQueryString(start);
            config.QueryParameters.EndDateTime = facility.ToUtcQueryString(end);
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
            // Exchange auto-converts/wraps a plain-text event body internally regardless of the
            // ContentType we send on write ("converted from text" HTML wrapper) - this Prefer
            // directive asks Graph to normalize the response body back to plain text on the way out.
            // No outlook.timezone preference here (deliberately) - see FacilityConfiguration.FromUtcResponseString.
            config.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        }, ct);

        // Same pagination gotcha as SheetBookingService.GetEventsInRangeAsync - a wide window can
        // exceed one calendarView page, silently truncating results if only .Value is read.
        while (response is not null)
        {
            if (response.Value is not null)
            {
                allEvents.AddRange(response.Value);
            }

            response = response.OdataNextLink is not null
                ? await graphClient.Users[facility.ClubEventsMailbox].CalendarView.WithUrl(response.OdataNextLink).GetAsync(cancellationToken: ct)
                : null;
        }

        var result = allEvents.Select(FromGraphEvent).ToList();
        cache.Set(cacheKey, result, ViewCacheTtl);
        _viewCacheKeys[cacheKey] = 0;
        return result;
    }

    private Event ToGraphEvent(ClubEvent clubEvent)
    {
        var extendedProps = new List<SingleValueLegacyExtendedProperty>
        {
            new() { Id = MarksUnavailablePropertyId, Value = clubEvent.MarksSheetsUnavailable ? "true" : "false" }
        };

        if (!string.IsNullOrWhiteSpace(clubEvent.BookedBy))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = BookedByPropertyId, Value = clubEvent.BookedBy });
        }

        var graphEvent = new Event
        {
            Subject = clubEvent.Title,
            Categories = [clubEvent.Category.ToString()],
            IsAllDay = clubEvent.IsAllDay,
            Body = new ItemBody { ContentType = BodyType.Text, Content = clubEvent.Notes ?? string.Empty },
            SingleValueExtendedProperties = extendedProps
        };

        if (clubEvent.IsAllDay)
        {
            // Graph's all-day events use an exclusive end date - an inclusive Aug 15-17 span is
            // Start=Aug15, End=Aug18. Staff enter (and this app otherwise treats) an inclusive end.
            graphEvent.Start = new DateTimeTimeZone { DateTime = clubEvent.Start.Date.ToString("s"), TimeZone = facility.TimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = clubEvent.End.Date.AddDays(1).ToString("s"), TimeZone = facility.TimeZone };
        }
        else
        {
            graphEvent.Start = new DateTimeTimeZone { DateTime = clubEvent.Start.ToString("s"), TimeZone = facility.TimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = clubEvent.End.ToString("s"), TimeZone = facility.TimeZone };
        }

        return graphEvent;
    }

    private ClubEvent FromGraphEvent(Event e)
    {
        var category = Enum.TryParse<ClubEventCategory>(e.Categories?.FirstOrDefault(), out var parsedCategory)
            ? parsedCategory
            : ClubEventCategory.Other;

        var isAllDay = e.IsAllDay ?? false;
        DateTime start, end;

        if (isAllDay)
        {
            // Graph returns an all-day event's Start/End as a bare midnight date labeled "UTC" -
            // that's Graph's convention for "this is a calendar date, not a real instant," not an
            // actual UTC timestamp needing conversion. Running it through FromUtcResponseString
            // (built for genuine UTC instants) shifted the date backward by the facility's UTC
            // offset before truncating - e.g. "2026-07-31T00:00:00" (meant to just mean July 31)
            // got converted to Pacific time and landed on July 30 instead. Parse it as a literal
            // calendar date instead - live-confirmed 2026-07-16 via an edit that silently reverted
            // to (new date - 1 day) on every read, which is exactly this off-by-one-UTC-offset shift.
            start = DateTime.Parse(e.Start?.DateTime ?? DateTime.UtcNow.ToString("o"), System.Globalization.CultureInfo.InvariantCulture).Date;
            end = DateTime.Parse(e.End?.DateTime ?? DateTime.UtcNow.ToString("o"), System.Globalization.CultureInfo.InvariantCulture).Date;

            // Undo the +1 day exclusive-end conversion applied on write, back to the inclusive
            // last day staff entered.
            end = end.AddDays(-1);
        }
        else
        {
            start = facility.FromUtcResponseString(e.Start?.DateTime ?? DateTime.UtcNow.ToString("o"));
            end = facility.FromUtcResponseString(e.End?.DateTime ?? DateTime.UtcNow.ToString("o"));
        }

        var marksUnavailableRaw = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == MarksUnavailablePropertyId)?.Value;
        var bookedBy = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == BookedByPropertyId)?.Value;

        return new ClubEvent
        {
            EventId = e.Id,
            ICalUId = e.ICalUId,
            Title = e.Subject ?? string.Empty,
            Category = category,
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            MarksSheetsUnavailable = marksUnavailableRaw == "true",
            Notes = e.Body?.Content,
            BookedBy = bookedBy
        };
    }
}
