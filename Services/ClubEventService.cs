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
public class ClubEventService(GraphServiceClient graphClient, IMemoryCache cache)
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

    public async Task<ClubEvent> CreateAsync(ClubEvent clubEvent, CancellationToken ct = default)
    {
        var graphEvent = ToGraphEvent(clubEvent);
        var created = await graphClient.Users[Sheets.ClubEvents].Events.PostAsync(graphEvent, cancellationToken: ct);
        clubEvent.EventId = created?.Id;
        clubEvent.ICalUId = created?.ICalUId;
        InvalidateViewCache();
        return clubEvent;
    }

    public async Task UpdateAsync(ClubEvent clubEvent, CancellationToken ct = default)
    {
        var graphEvent = ToGraphEvent(clubEvent);
        await graphClient.Users[Sheets.ClubEvents].Events[clubEvent.EventId!].PatchAsync(graphEvent, cancellationToken: ct);
        InvalidateViewCache();
    }

    public async Task CancelAsync(string eventId, CancellationToken ct = default)
    {
        await graphClient.Users[Sheets.ClubEvents].Events[eventId].DeleteAsync(cancellationToken: ct);
        InvalidateViewCache();
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
        var response = await graphClient.Users[Sheets.ClubEvents].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = FacilityGraphConventions.ToUtcQueryString(start);
            config.QueryParameters.EndDateTime = FacilityGraphConventions.ToUtcQueryString(end);
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
            // Exchange auto-converts/wraps a plain-text event body internally regardless of the
            // ContentType we send on write ("converted from text" HTML wrapper) - this second Prefer
            // directive asks Graph to normalize the response body back to plain text on the way out.
            config.Headers.Add("Prefer", $"outlook.timezone=\"{FacilityGraphConventions.FacilityTimeZone}\", outlook.body-content-type=\"text\"");
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
                ? await graphClient.Users[Sheets.ClubEvents].CalendarView.WithUrl(response.OdataNextLink).GetAsync(cancellationToken: ct)
                : null;
        }

        var result = allEvents.Select(FromGraphEvent).ToList();
        cache.Set(cacheKey, result, ViewCacheTtl);
        _viewCacheKeys[cacheKey] = 0;
        return result;
    }

    private static Event ToGraphEvent(ClubEvent clubEvent)
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
            graphEvent.Start = new DateTimeTimeZone { DateTime = clubEvent.Start.Date.ToString("s"), TimeZone = FacilityGraphConventions.FacilityTimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = clubEvent.End.Date.AddDays(1).ToString("s"), TimeZone = FacilityGraphConventions.FacilityTimeZone };
        }
        else
        {
            graphEvent.Start = new DateTimeTimeZone { DateTime = clubEvent.Start.ToString("s"), TimeZone = FacilityGraphConventions.FacilityTimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = clubEvent.End.ToString("s"), TimeZone = FacilityGraphConventions.FacilityTimeZone };
        }

        return graphEvent;
    }

    private static ClubEvent FromGraphEvent(Event e)
    {
        var category = Enum.TryParse<ClubEventCategory>(e.Categories?.FirstOrDefault(), out var parsedCategory)
            ? parsedCategory
            : ClubEventCategory.Other;

        var isAllDay = e.IsAllDay ?? false;
        var start = DateTime.Parse(e.Start?.DateTime ?? DateTime.UtcNow.ToString("s"));
        var end = DateTime.Parse(e.End?.DateTime ?? DateTime.UtcNow.ToString("s"));

        if (isAllDay)
        {
            // Undo the +1 day exclusive-end conversion applied on write, back to the inclusive
            // last day staff entered.
            end = end.Date.AddDays(-1);
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
