using FacilityScheduler.Services.Graph;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Passes every call straight through to an inner gateway. A test that needs to observe or
/// break one Graph call (count calendarView reads, throw on GetEvent) overrides just that member, so
/// a change to IGraphEventGateway touches this one file rather than every decorator.</summary>
public class DelegatingGraphEventGateway(IGraphEventGateway inner) : IGraphEventGateway
{
    public virtual Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default) =>
        inner.GetCalendarViewAsync(mailbox, startUtc, endUtc, expand, extraHeaders, ct);

    public virtual Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default) =>
        inner.GetEventAsync(mailbox, eventId, expand, ct);

    public virtual Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default) =>
        inner.FindEventsAsync(mailbox, filter, expand, ct);

    public virtual Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default) =>
        inner.CreateEventAsync(mailbox, graphEvent, ct);

    public virtual Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default) =>
        inner.PatchEventAsync(mailbox, eventId, patch, ct);

    public virtual Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default) =>
        inner.DeleteEventAsync(mailbox, eventId, ct);

    public virtual Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default) =>
        inner.GetInstancesAsync(mailbox, eventId, startUtc, endUtc, ct);
}

/// <summary>Counts calendarView reads - the expensive, per-sheet fan-out call a search triggers.</summary>
public sealed class CountingGraphEventGateway(IGraphEventGateway inner) : DelegatingGraphEventGateway(inner)
{
    private int _calendarViewCalls;

    public int CalendarViewCalls => _calendarViewCalls;

    public override Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calendarViewCalls);
        return base.GetCalendarViewAsync(mailbox, startUtc, endUtc, expand, extraHeaders, ct);
    }
}
