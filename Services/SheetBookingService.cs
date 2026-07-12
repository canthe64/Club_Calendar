using System.Collections.Concurrent;
using System.Text.Json;
using FacilityScheduler.Domain;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Services;

/// <summary>
/// Owns conflict enforcement for sheet bookings. Direct Graph writes bypass the Resource
/// Booking Attendant (confirmed via spike, architecture doc D3/S6.1), so this service is the
/// only thing standing between two overlapping bookings on the same sheet.
/// </summary>
public class SheetBookingService(GraphServiceClient graphClient)
{
    // One semaphore per sheet mailbox, lazily created. Serializes create/confirm/cancel per
    // sheet so the check-then-write conflict check can't race. Adequate at the app's known
    // concurrency (1-2 staff); would need a distributed lock if this ever ran multi-instance.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SheetLocks = new();

    // Fixed GUID namespace for this app's custom extended properties. BookedBy is a named,
    // individually filterable property; everything display-only is bundled into one JSON blob
    // (architecture doc S4.1 design rule).
    private const string PropertyGuid = "c11ff204-d3f3-4a0c-9639-d915f8c8a3e3";
    private const string BookedByPropertyId = $"String {{{PropertyGuid}}} Name BookedBy";
    private const string DetailsPropertyId = $"String {{{PropertyGuid}}} Name BookingDetails";

    // A blanket $expand=singleValueExtendedProperties is not sufficient in practice - Graph
    // appears to require the $filter sub-clause scoped to the specific property IDs to actually
    // populate results, matching the pattern shown in Microsoft's own documentation examples.
    private static readonly string[] ExtendedPropertiesExpand =
    [
        $"singleValueExtendedProperties($filter=id eq '{DetailsPropertyId}' or id eq '{BookedByPropertyId}')"
    ];

    public Task<BookingResult> CreateHoldAsync(SheetBooking booking, CancellationToken ct = default)
    {
        booking.State = BookingState.Hold;
        return CreateAsync(booking, ct);
    }

    public Task<BookingResult> CreateConfirmedAsync(SheetBooking booking, CancellationToken ct = default)
    {
        booking.State = BookingState.Confirmed;
        return CreateAsync(booking, ct);
    }

    private async Task<BookingResult> CreateAsync(SheetBooking booking, CancellationToken ct)
    {
        var sem = SheetLocks.GetOrAdd(booking.SheetMailbox, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var overlapping = await GetEventsInRangeAsync(booking.SheetMailbox, booking.Start, booking.End, ct);
            if (overlapping.Count > 0)
            {
                var conflicts = overlapping.Select(e => FromGraphEvent(booking.SheetMailbox, e)).ToList();
                return BookingResult.Conflict(conflicts);
            }

            var graphEvent = ToGraphEvent(booking);
            var created = await graphClient.Users[booking.SheetMailbox].Events.PostAsync(graphEvent, cancellationToken: ct);

            booking.EventId = created?.Id;
            booking.ICalUId = created?.ICalUId;
            return BookingResult.Success(booking);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId, CancellationToken ct = default)
    {
        var update = new Event { ShowAs = FreeBusyStatus.Busy };
        await graphClient.Users[sheetMailbox].Events[eventId].PatchAsync(update, cancellationToken: ct);

        // Re-fetch rather than trust the PATCH response shape - extended properties are only
        // returned when explicitly expanded, and that's not guaranteed on a PATCH response body.
        var refreshed = await graphClient.Users[sheetMailbox].Events[eventId].GetAsync(config =>
        {
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
        }, ct);

        return FromGraphEvent(sheetMailbox, refreshed!);
    }

    public async Task CancelAsync(string sheetMailbox, string eventId, CancellationToken ct = default)
    {
        await graphClient.Users[sheetMailbox].Events[eventId].DeleteAsync(cancellationToken: ct);
    }

    public async Task<List<SheetBooking>> GetBookingsAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var events = await GetEventsInRangeAsync(sheetMailbox, start, end, ct);
        return events.Select(e => FromGraphEvent(sheetMailbox, e)).ToList();
    }

    private async Task<List<Event>> GetEventsInRangeAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct)
    {
        var events = await graphClient.Users[sheetMailbox].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = start.ToString("o");
            config.QueryParameters.EndDateTime = end.ToString("o");
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
        }, ct);

        return events?.Value?.ToList() ?? [];
    }

    private static Event ToGraphEvent(SheetBooking booking)
    {
        var subject = string.IsNullOrWhiteSpace(booking.RenterName)
            ? booking.Category.ToString()
            : $"{booking.Category} - {booking.RenterName}";

        var extendedProps = new List<SingleValueLegacyExtendedProperty>
        {
            new()
            {
                Id = DetailsPropertyId,
                Value = JsonSerializer.Serialize(new BookingDetails(booking.RenterName, booking.RenterContact, booking.Price, booking.Notes))
            }
        };

        if (!string.IsNullOrWhiteSpace(booking.BookedBy))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = BookedByPropertyId, Value = booking.BookedBy });
        }

        return new Event
        {
            Subject = subject,
            Start = new DateTimeTimeZone { DateTime = booking.Start.ToString("s"), TimeZone = "UTC" },
            End = new DateTimeTimeZone { DateTime = booking.End.ToString("s"), TimeZone = "UTC" },
            ShowAs = booking.State == BookingState.Confirmed ? FreeBusyStatus.Busy : FreeBusyStatus.Tentative,
            Categories = [booking.Category.ToString()],
            SingleValueExtendedProperties = extendedProps
        };
    }

    private static SheetBooking FromGraphEvent(string sheetMailbox, Event e)
    {
        var category = Enum.TryParse<BookingCategory>(e.Categories?.FirstOrDefault(), out var parsedCategory)
            ? parsedCategory
            : BookingCategory.Other;

        var state = e.ShowAs == FreeBusyStatus.Busy ? BookingState.Confirmed : BookingState.Hold;

        var detailsJson = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == DetailsPropertyId)?.Value;
        var bookedBy = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == BookedByPropertyId)?.Value;

        BookingDetails? details = null;
        if (detailsJson is not null)
        {
            try { details = JsonSerializer.Deserialize<BookingDetails>(detailsJson); }
            catch (JsonException) { /* malformed or missing blob - treat as no detail available */ }
        }

        return new SheetBooking
        {
            EventId = e.Id,
            ICalUId = e.ICalUId,
            SheetMailbox = sheetMailbox,
            Start = DateTime.Parse(e.Start?.DateTime ?? DateTime.UtcNow.ToString("s")),
            End = DateTime.Parse(e.End?.DateTime ?? DateTime.UtcNow.ToString("s")),
            Category = category,
            State = state,
            RenterName = details?.RenterName,
            RenterContact = details?.RenterContact,
            Price = details?.Price,
            Notes = details?.Notes,
            BookedBy = bookedBy
        };
    }

    private sealed record BookingDetails(string? RenterName, string? RenterContact, decimal? Price, string? Notes);
}
