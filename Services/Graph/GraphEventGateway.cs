using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Services.Graph;

/// <summary>The only class in this app that talks to GraphServiceClient directly for event
/// operations - see IGraphEventGateway for why this boundary exists.</summary>
public class GraphEventGateway(GraphServiceClient graphClient) : IGraphEventGateway
{
    public async Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
    {
        var allEvents = new List<Event>();
        var response = await graphClient.Users[mailbox].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = startUtc;
            config.QueryParameters.EndDateTime = endUtc;
            config.QueryParameters.Expand = expand;
            if (extraHeaders is not null)
            {
                foreach (var (key, value) in extraHeaders)
                {
                    config.Headers.Add(key, value);
                }
            }
        }, ct);

        // calendarView pages its results - a wide window (e.g. a 6-week month view) with several
        // recurring series expanded into occurrences can easily exceed one page. Only reading the
        // first page silently truncates later occurrences; follow @odata.nextLink until exhausted.
        while (response is not null)
        {
            if (response.Value is not null)
            {
                allEvents.AddRange(response.Value);
            }

            response = response.OdataNextLink is not null
                ? await graphClient.Users[mailbox].CalendarView.WithUrl(response.OdataNextLink).GetAsync(cancellationToken: ct)
                : null;
        }

        return allEvents;
    }

    public Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default) =>
        graphClient.Users[mailbox].Events[eventId].GetAsync(config =>
        {
            if (expand is not null)
            {
                config.QueryParameters.Expand = expand;
            }
        }, ct);

    public async Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default)
    {
        var response = await graphClient.Users[mailbox].Events.GetAsync(config =>
        {
            config.QueryParameters.Filter = filter;
            config.QueryParameters.Expand = expand;
        }, ct);

        return response?.Value ?? [];
    }

    public Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default) =>
        graphClient.Users[mailbox].Events.PostAsync(graphEvent, cancellationToken: ct);

    public Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default) =>
        graphClient.Users[mailbox].Events[eventId].PatchAsync(patch, cancellationToken: ct);

    public Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default) =>
        graphClient.Users[mailbox].Events[eventId].DeleteAsync(cancellationToken: ct);

    public async Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default)
    {
        var allInstances = new List<Event>();
        var instances = await graphClient.Users[mailbox].Events[eventId].Instances.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = startUtc;
            config.QueryParameters.EndDateTime = endUtc;
        }, ct);

        // Same pagination gotcha as GetCalendarViewAsync - a long season's worth of occurrences can
        // exceed one page.
        while (instances is not null)
        {
            if (instances.Value is not null)
            {
                allInstances.AddRange(instances.Value);
            }

            instances = instances.OdataNextLink is not null
                ? await graphClient.Users[mailbox].Events[eventId].Instances.WithUrl(instances.OdataNextLink).GetAsync(cancellationToken: ct)
                : null;
        }

        return allInstances;
    }
}
