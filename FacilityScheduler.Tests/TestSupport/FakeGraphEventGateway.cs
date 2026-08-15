using System.Globalization;
using System.Text.RegularExpressions;
using FacilityScheduler.Services.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>
/// In-memory stand-in for GraphEventGateway, precise enough to exercise the specific behaviors the
/// code review's high-priority fixes depend on:
///  - PatchEventAsync only applies fields actually set on the patch object, and merges
///    SingleValueExtendedProperties per-property-id rather than replacing the collection - the
///    exact Graph PATCH semantics H1 was fixed against.
///  - DeleteEventAsync/PatchEventAsync on an unknown id throw a 404 ODataError, matching the
///    "already gone" tolerance CancelAsync/CancelGroupAsync/CancelSeriesAsync rely on.
///  - DelayDuringCalendarView/DelayDuringFindEvents let a test force a genuine await-yield inside
///    the read step of a check-then-act sequence, so a concurrency test can prove SheetLocks/
///    ExternalIdLocks actually serialize callers rather than passing by sheer luck on fast, fully
///    synchronous fake I/O.
/// Recurring-series instance expansion (GetInstancesAsync) is deliberately NOT modeled - no test in
/// this suite currently exercises CreateSeriesAsync's excluded-date deletion path.
/// </summary>
public class FakeGraphEventGateway(TimeZoneInfo zone) : IGraphEventGateway
{
    private readonly Dictionary<string, List<Event>> _mailboxes = new();
    private int _nextId = 1;

    public Func<Task>? DelayDuringCalendarView { get; set; }
    public Func<Task>? DelayDuringFindEvents { get; set; }

    private static readonly Regex ExtendedPropertyFilter =
        new(@"ep/id eq '(?<id>[^']+)' and ep/value eq '(?<value>[^']*)'", RegexOptions.Compiled);

    private List<Event> Mailbox(string mailbox) =>
        _mailboxes.TryGetValue(mailbox, out var list) ? list : _mailboxes[mailbox] = [];

    private DateTime ToUtc(DateTimeTimeZone? dtz)
    {
        if (dtz?.DateTime is null)
        {
            return DateTime.MinValue;
        }

        var tz = dtz.TimeZone switch
        {
            null or "" => zone,
            "UTC" => TimeZoneInfo.Utc,
            _ => TimeZoneInfo.FindSystemTimeZoneById(dtz.TimeZone)
        };
        var local = DateTime.SpecifyKind(DateTime.Parse(dtz.DateTime, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        return tz == TimeZoneInfo.Utc ? DateTime.SpecifyKind(local, DateTimeKind.Utc) : TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public async Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
    {
        if (DelayDuringCalendarView is not null)
        {
            await DelayDuringCalendarView();
        }

        var start = DateTime.Parse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var end = DateTime.Parse(endUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        return Mailbox(mailbox).Where(e => ToUtc(e.Start) < end && ToUtc(e.End) > start).Select(Clone).ToList();
    }

    public Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default)
    {
        var found = Mailbox(mailbox).FirstOrDefault(e => e.Id == eventId);
        return Task.FromResult(found is null ? null : Clone(found));
    }

    public async Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default)
    {
        if (DelayDuringFindEvents is not null)
        {
            await DelayDuringFindEvents();
        }

        var match = ExtendedPropertyFilter.Match(filter);
        if (!match.Success)
        {
            throw new InvalidOperationException($"FakeGraphEventGateway doesn't understand filter: {filter}");
        }

        var propId = match.Groups["id"].Value;
        var propValue = match.Groups["value"].Value;

        return Mailbox(mailbox)
            .Where(e => e.SingleValueExtendedProperties?.Any(p => p.Id == propId && p.Value == propValue) == true)
            .Select(Clone)
            .ToList();
    }

    /// <summary>When set, the (n+1)th CreateEventAsync call throws - simulates a Graph write failing
    /// partway through a multi-sheet create (a 429, a 5xx, an over-size extended property) so the
    /// rollback path can be tested. Null disables it.</summary>
    public int? FailCreateAfter { get; set; }

    private int _createCount;

    public Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default)
    {
        if (FailCreateAfter is int limit && _createCount++ >= limit)
        {
            throw new InvalidOperationException($"Simulated Graph create failure on call {_createCount}.");
        }

        var created = Clone(graphEvent);
        created.Id = $"evt-{_nextId++}";
        created.ICalUId = $"ical-{created.Id}";
        NormalizeStorage(created);
        Mailbox(mailbox).Add(created);
        return Task.FromResult<Event?>(Clone(created));
    }

    public Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default)
    {
        var stored = Mailbox(mailbox).FirstOrDefault(e => e.Id == eventId) ?? throw NotFound();

        // Matches Graph's real PATCH semantics: only fields the caller actually set on the request
        // body are applied; everything else on the stored event is left untouched. Same is true
        // per-property for SingleValueExtendedProperties - a property id omitted from the patch
        // keeps whatever value it already had. This is the exact behavior H1 was fixed against.
        if (patch.Subject is not null) stored.Subject = patch.Subject;
        if (patch.ShowAs is not null) stored.ShowAs = patch.ShowAs;
        if (patch.Categories is not null) stored.Categories = patch.Categories;
        if (patch.Start is not null) stored.Start = NormalizeToStoredUtc(patch.Start);
        if (patch.End is not null) stored.End = NormalizeToStoredUtc(patch.End);
        if (patch.IsAllDay is not null) stored.IsAllDay = patch.IsAllDay;
        if (patch.Body is not null) stored.Body = new ItemBody { ContentType = patch.Body.ContentType, Content = patch.Body.Content };

        if (patch.SingleValueExtendedProperties is not null)
        {
            stored.SingleValueExtendedProperties ??= [];
            foreach (var prop in patch.SingleValueExtendedProperties)
            {
                var existing = stored.SingleValueExtendedProperties.FirstOrDefault(p => p.Id == prop.Id);
                if (existing is not null)
                {
                    existing.Value = prop.Value;
                }
                else
                {
                    stored.SingleValueExtendedProperties.Add(new SingleValueLegacyExtendedProperty { Id = prop.Id, Value = prop.Value });
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default)
    {
        var list = Mailbox(mailbox);
        var found = list.FirstOrDefault(e => e.Id == eventId) ?? throw NotFound();
        list.Remove(found);
        return Task.CompletedTask;
    }

    public Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default) =>
        Task.FromResult(new List<Event>());

    // Real Graph normalizes whatever local-time-plus-zone an event is written with into UTC
    // internally, and the app's own FromGraphEvent unconditionally treats a read-back DateTime
    // string as UTC (FacilityConfiguration.FromUtcResponseString - no outlook.timezone Prefer
    // header, per that method's own doc comment on why). This fake must reproduce that same
    // write-time normalization, or a value written as local-plus-zone would be silently
    // misinterpreted as UTC on the very next read within this same fake.
    private DateTimeTimeZone NormalizeToStoredUtc(DateTimeTimeZone dtz) =>
        new() { DateTime = ToUtc(dtz).ToString("s"), TimeZone = "UTC" };

    private void NormalizeStorage(Event e)
    {
        if (e.Start is not null) e.Start = NormalizeToStoredUtc(e.Start);
        if (e.End is not null) e.End = NormalizeToStoredUtc(e.End);
    }

    private static ODataError NotFound() => new() { ResponseStatusCode = 404 };

    private static Event Clone(Event e) => new()
    {
        Id = e.Id,
        ICalUId = e.ICalUId,
        Subject = e.Subject,
        ShowAs = e.ShowAs,
        Categories = e.Categories is null ? null : [.. e.Categories],
        Start = e.Start is null ? null : new DateTimeTimeZone { DateTime = e.Start.DateTime, TimeZone = e.Start.TimeZone },
        End = e.End is null ? null : new DateTimeTimeZone { DateTime = e.End.DateTime, TimeZone = e.End.TimeZone },
        IsAllDay = e.IsAllDay,
        SeriesMasterId = e.SeriesMasterId,
        Body = e.Body is null ? null : new ItemBody { ContentType = e.Body.ContentType, Content = e.Body.Content },
        SingleValueExtendedProperties = e.SingleValueExtendedProperties?.Select(p => new SingleValueLegacyExtendedProperty { Id = p.Id, Value = p.Value }).ToList()
    };

    /// <summary>Test-only direct seed, bypassing CreateEventAsync - for setting up pre-existing
    /// state (e.g. an open hold already on a sheet) without driving it through the service under
    /// test.</summary>
    public string Seed(string mailbox, Event graphEvent, string? eventId = null)
    {
        var stored = Clone(graphEvent);
        stored.Id = eventId ?? $"evt-{_nextId++}";
        stored.ICalUId ??= $"ical-{stored.Id}";
        NormalizeStorage(stored);
        Mailbox(mailbox).Add(stored);
        return stored.Id;
    }

    public IReadOnlyList<Event> Events(string mailbox) => Mailbox(mailbox);
}
