using Microsoft.Graph.Models;

namespace FacilityScheduler.Services.Graph;

/// <summary>
/// Thin wrapper over the specific Microsoft Graph event operations SheetBookingService and
/// ClubEventService use (calendarView, single-event CRUD, filtered search, recurring-instance
/// expansion). Exists purely so unit tests can substitute a fake instead of driving Graph SDK's
/// fluent request builders directly - GraphEventGateway is the only class that talks to
/// GraphServiceClient itself.
/// </summary>
public interface IGraphEventGateway
{
    /// <summary>Every event in [startUtc, endUtc) on the given mailbox's calendar, paginated until
    /// exhausted. start/end are already-converted UTC ISO 8601 strings (FacilityConfiguration.ToUtcQueryString).</summary>
    Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default);

    Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default);

    /// <summary>Single-page $filter query - matches the existing behavior of every caller, none of
    /// which have needed pagination for a filtered search.</summary>
    Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default);

    Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default);

    Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default);

    Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default);

    /// <summary>Every occurrence of a recurring event in [startUtc, endUtc), paginated until exhausted.</summary>
    Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default);
}
