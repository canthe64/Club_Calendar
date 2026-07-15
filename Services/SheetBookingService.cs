using System.Collections.Concurrent;
using System.Text.Json;
using FacilityScheduler.Domain;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

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

    // Fixed GUID namespace for this app's custom extended properties. BookedBy and
    // BookingGroupId are named, individually filterable properties; everything display-only
    // is bundled into one JSON blob (architecture doc S4.1 design rule).
    private const string FacilityTimeZone = FacilityGraphConventions.FacilityTimeZone;
    private const string PropertyGuid = FacilityGraphConventions.PropertyGuid;
    private const string BookedByPropertyId = $"String {{{PropertyGuid}}} Name BookedBy";
    private const string DetailsPropertyId = $"String {{{PropertyGuid}}} Name BookingDetails";
    private const string GroupIdPropertyId = $"String {{{PropertyGuid}}} Name BookingGroupId";

    // A blanket $expand=singleValueExtendedProperties is not sufficient in practice - Graph
    // appears to require the $filter sub-clause scoped to the specific property IDs to actually
    // populate results, matching the pattern shown in Microsoft's own documentation examples.
    private static readonly string[] ExtendedPropertiesExpand =
    [
        $"singleValueExtendedProperties($filter=id eq '{DetailsPropertyId}' or id eq '{BookedByPropertyId}' or id eq '{GroupIdPropertyId}')"
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

            if (booking.BookingGroupId == Guid.Empty)
            {
                booking.BookingGroupId = Guid.NewGuid();
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

    /// <summary>
    /// Creates the same conceptual booking across multiple sheets at once (e.g. a rental
    /// spanning 3 sheets). All-or-nothing: if any requested sheet conflicts, nothing is created
    /// and every conflict across every sheet is reported, so the caller can deselect a sheet or
    /// change the time rather than getting a partially-booked result.
    /// </summary>
    public async Task<GroupBookingResult> CreateAcrossSheetsAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, CancellationToken ct = default)
    {
        // Sorted lock order avoids deadlock if two staff book overlapping multi-sheet requests
        // that share some sheets but list them in a different order.
        var orderedSheets = sheetMailboxes.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sems = orderedSheets.Select(s => SheetLocks.GetOrAdd(s, _ => new SemaphoreSlim(1, 1))).ToList();

        foreach (var sem in sems)
        {
            await sem.WaitAsync(ct);
        }

        try
        {
            var conflicts = new List<SheetBooking>();
            foreach (var sheet in orderedSheets)
            {
                var overlapping = await GetEventsInRangeAsync(sheet, template.Start, template.End, ct);
                conflicts.AddRange(overlapping.Select(e => FromGraphEvent(sheet, e)));
            }

            if (conflicts.Count > 0)
            {
                return GroupBookingResult.Conflict(conflicts);
            }

            var groupId = Guid.NewGuid();
            var created = new List<SheetBooking>();
            foreach (var sheet in orderedSheets)
            {
                var booking = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = template.Start,
                    End = template.End,
                    Category = template.Category,
                    State = template.State,
                    RenterName = template.RenterName,
                    RenterPhone = template.RenterPhone,
                    RenterEmail = template.RenterEmail,
                    Notes = template.Notes,
                    BookedBy = template.BookedBy,
                    BookingGroupId = groupId
                };

                var graphEvent = ToGraphEvent(booking);
                var result = await graphClient.Users[sheet].Events.PostAsync(graphEvent, cancellationToken: ct);
                booking.EventId = result?.Id;
                booking.ICalUId = result?.ICalUId;
                created.Add(booking);
            }

            return GroupBookingResult.Success(created);
        }
        finally
        {
            foreach (var sem in sems)
            {
                sem.Release();
            }
        }
    }

    public async Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId, CancellationToken ct = default)
    {
        var update = new Event { ShowAs = FreeBusyStatus.Busy };
        await graphClient.Users[sheetMailbox].Events[eventId].PatchAsync(update, cancellationToken: ct);
        return await GetEventAsync(sheetMailbox, eventId, ct);
    }

    public async Task CancelAsync(string sheetMailbox, string eventId, CancellationToken ct = default)
    {
        await graphClient.Users[sheetMailbox].Events[eventId].DeleteAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Updates every event in a booking group - category, time, renter/contact/notes, and
    /// state (hold vs. confirmed) all come from <paramref name="updatedFields"/>. Does not
    /// add/remove sheets from the group. Re-checks conflicts against the new time on each member's
    /// sheet before writing anything (all-or-nothing, same philosophy as CreateAcrossSheetsAsync) -
    /// each member's own current event is excluded from its own conflict check, so an edit that
    /// doesn't move the time never conflicts with itself. <paramref name="newBookingGroupId"/>, when
    /// given, reassigns all updated members to a new group - used when a caller only edited a subset
    /// of the original group's sheets, so the edited subset splits off rather than staying linked to
    /// sheets that were deliberately left untouched.
    /// </summary>
    public async Task<GroupBookingResult> UpdateGroupAsync(
        IEnumerable<SheetBooking> members, SheetBooking updatedFields, Guid? newBookingGroupId = null, CancellationToken ct = default)
    {
        var memberList = members.Where(m => m.EventId is not null).ToList();
        if (memberList.Count == 0)
        {
            return GroupBookingResult.Success([]);
        }

        var orderedSheets = memberList.Select(m => m.SheetMailbox).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sems = orderedSheets.Select(s => SheetLocks.GetOrAdd(s, _ => new SemaphoreSlim(1, 1))).ToList();

        foreach (var sem in sems)
        {
            await sem.WaitAsync(ct);
        }

        try
        {
            var ownEventIds = memberList.Select(m => m.EventId!).ToHashSet();
            var conflicts = new List<SheetBooking>();
            foreach (var member in memberList)
            {
                var overlapping = await GetEventsInRangeAsync(member.SheetMailbox, updatedFields.Start, updatedFields.End, ct);
                conflicts.AddRange(overlapping.Where(e => e.Id is not null && !ownEventIds.Contains(e.Id)).Select(e => FromGraphEvent(member.SheetMailbox, e)));
            }

            if (conflicts.Count > 0)
            {
                return GroupBookingResult.Conflict(conflicts);
            }

            var updated = new List<SheetBooking>();
            foreach (var member in memberList)
            {
                // Occurrences of a recurring series reject a PATCH that includes Start/End at all if
                // Graph's business validation decides the (re-sent, even if unchanged) time crosses or
                // overlaps the adjacent occurrence - "Modified occurrence is crossing or overlapping
                // adjacent occurrence". Only send Start/End when the time actually moved, so metadata-only
                // edits (category, notes, dropping a sheet) never trip that check.
                var timeChanged = updatedFields.Start != member.Start || updatedFields.End != member.End;

                var merged = new SheetBooking
                {
                    SheetMailbox = member.SheetMailbox,
                    Start = updatedFields.Start,
                    End = updatedFields.End,
                    Category = updatedFields.Category,
                    State = updatedFields.State,
                    RenterName = updatedFields.RenterName,
                    RenterPhone = updatedFields.RenterPhone,
                    RenterEmail = updatedFields.RenterEmail,
                    Notes = updatedFields.Notes,
                    BookedBy = member.BookedBy,
                    BookingGroupId = newBookingGroupId ?? member.BookingGroupId
                };

                var graphEvent = ToGraphEvent(merged, includeTime: timeChanged);
                await graphClient.Users[member.SheetMailbox].Events[member.EventId!].PatchAsync(graphEvent, cancellationToken: ct);
                merged.EventId = member.EventId;
                updated.Add(merged);
            }

            return GroupBookingResult.Success(updated);
        }
        finally
        {
            foreach (var sem in sems)
            {
                sem.Release();
            }
        }
    }

    /// <summary>
    /// Cancels every event in a booking group. <paramref name="reopenAsRentalHold"/> distinguishes
    /// the two cancel paths surfaced to staff: reopen (the slot goes back to an unclaimed
    /// "open for rental" hold, publicly bookable again) vs. close the ice (hard delete, slot no
    /// longer offered at all).
    /// </summary>
    public async Task CancelGroupAsync(IEnumerable<SheetBooking> members, bool reopenAsRentalHold, CancellationToken ct = default)
    {
        foreach (var member in members)
        {
            if (member.EventId is null)
            {
                continue;
            }

            if (reopenAsRentalHold)
            {
                var reopened = new SheetBooking
                {
                    SheetMailbox = member.SheetMailbox,
                    Start = member.Start,
                    End = member.End,
                    Category = BookingCategory.Rental,
                    State = BookingState.Hold,
                    BookingGroupId = member.BookingGroupId
                    // Renter-specific fields intentionally omitted - back to a plain open hold.
                };

                var graphEvent = ToGraphEvent(reopened, includeTime: false);
                await graphClient.Users[member.SheetMailbox].Events[member.EventId].PatchAsync(graphEvent, cancellationToken: ct);
            }
            else
            {
                await graphClient.Users[member.SheetMailbox].Events[member.EventId].DeleteAsync(cancellationToken: ct);
            }
        }
    }

    /// <summary>
    /// Checks each candidate date against existing bookings on any of the given sheets.
    /// Informational only - conflicts here never block creation, they're surfaced to staff so
    /// they can choose to skip that date. One fetch per sheet across the whole date range,
    /// not one call per date.
    /// </summary>
    public async Task<Dictionary<DateTime, List<SheetBooking>>> PreviewSeriesConflictsAsync(
        IEnumerable<string> sheetMailboxes, IReadOnlyCollection<DateTime> candidateDates, TimeSpan startTime, TimeSpan endTime, CancellationToken ct = default)
    {
        var result = new Dictionary<DateTime, List<SheetBooking>>();
        if (candidateDates.Count == 0)
        {
            return result;
        }

        var rangeStart = candidateDates.Min().Date;
        var rangeEnd = candidateDates.Max().Date.AddDays(1);

        var allBookings = new List<SheetBooking>();
        foreach (var sheet in sheetMailboxes.Distinct())
        {
            allBookings.AddRange(await GetBookingsAsync(sheet, rangeStart, rangeEnd, ct));
        }

        foreach (var date in candidateDates)
        {
            var slotStart = date.Date + startTime;
            var slotEnd = date.Date + endTime;
            var conflicts = allBookings.Where(b => b.Start < slotEnd && b.End > slotStart).ToList();
            if (conflicts.Count > 0)
            {
                result[date] = conflicts;
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a weekly recurring series across the given sheets (one native Graph recurring
    /// series per sheet, sharing a BookingGroupId - Graph has no concept of one series spanning
    /// multiple mailboxes). <paramref name="excludedDates"/> are dates staff chose to skip during
    /// review; those specific occurrences are deleted immediately after the series is created,
    /// per architecture doc D-record: native recurrence, not one event per date. Conflicts are
    /// never checked here - by this point staff have already reviewed and decided, via
    /// PreviewSeriesConflictsAsync.
    /// </summary>
    public async Task<List<SheetBooking>> CreateSeriesAsync(
        IEnumerable<string> sheetMailboxes, SheetBooking template, DateTime lastOccurrenceDate, IEnumerable<DateTime> excludedDates, CancellationToken ct = default)
    {
        var orderedSheets = sheetMailboxes.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var groupId = Guid.NewGuid();
        var excluded = excludedDates.Select(d => d.Date).ToHashSet();
        var created = new List<SheetBooking>();

        foreach (var sheet in orderedSheets)
        {
            var booking = new SheetBooking
            {
                SheetMailbox = sheet,
                Start = template.Start,
                End = template.End,
                Category = template.Category,
                State = template.State,
                RenterName = template.RenterName,
                RenterPhone = template.RenterPhone,
                RenterEmail = template.RenterEmail,
                Notes = template.Notes,
                BookedBy = template.BookedBy,
                BookingGroupId = groupId
            };

            var graphEvent = ToGraphEvent(booking);
            graphEvent.Recurrence = new PatternedRecurrence
            {
                Pattern = new RecurrencePattern
                {
                    Type = RecurrencePatternType.Weekly,
                    Interval = 1,
                    DaysOfWeek = [Enum.Parse<Microsoft.Graph.Models.DayOfWeekObject>(template.Start.DayOfWeek.ToString())]
                },
                Range = new RecurrenceRange
                {
                    Type = RecurrenceRangeType.EndDate,
                    StartDate = new Microsoft.Kiota.Abstractions.Date(template.Start.Year, template.Start.Month, template.Start.Day),
                    EndDate = new Microsoft.Kiota.Abstractions.Date(lastOccurrenceDate.Year, lastOccurrenceDate.Month, lastOccurrenceDate.Day),
                    RecurrenceTimeZone = FacilityTimeZone
                }
            };

            var result = await graphClient.Users[sheet].Events.PostAsync(graphEvent, cancellationToken: ct);
            booking.EventId = result?.Id;
            booking.ICalUId = result?.ICalUId;
            created.Add(booking);

            if (excluded.Count > 0 && result?.Id is not null)
            {
                var allInstances = new List<Event>();
                var instances = await graphClient.Users[sheet].Events[result.Id].Instances.GetAsync(config =>
                {
                    config.QueryParameters.StartDateTime = FacilityGraphConventions.ToUtcQueryString(template.Start);
                    config.QueryParameters.EndDateTime = FacilityGraphConventions.ToUtcQueryString(lastOccurrenceDate.Date.AddDays(1));
                    config.Headers.Add("Prefer", $"outlook.timezone=\"{FacilityTimeZone}\"");
                }, ct);

                // Same pagination gotcha as GetEventsInRangeAsync - a long season's worth of
                // occurrences can exceed one page.
                while (instances is not null)
                {
                    if (instances.Value is not null)
                    {
                        allInstances.AddRange(instances.Value);
                    }

                    instances = instances.OdataNextLink is not null
                        ? await graphClient.Users[sheet].Events[result.Id].Instances.WithUrl(instances.OdataNextLink).GetAsync(cancellationToken: ct)
                        : null;
                }

                foreach (var instance in allInstances)
                {
                    if (instance.Start?.DateTime is null || instance.Id is null)
                    {
                        continue;
                    }

                    var instanceDate = DateTime.Parse(instance.Start.DateTime).Date;
                    if (excluded.Contains(instanceDate))
                    {
                        await graphClient.Users[sheet].Events[instance.Id].DeleteAsync(cancellationToken: ct);
                    }
                }
            }
        }

        return created;
    }

    /// <summary>
    /// Deletes the entire recurring series (all occurrences, past and future) for every sheet in
    /// the group. This is the "backdoor" for correcting a data-entry mistake at series creation -
    /// deliberately not a primary UX path. No-op for members that aren't part of a series.
    /// </summary>
    public async Task CancelSeriesAsync(IEnumerable<SheetBooking> members, CancellationToken ct = default)
    {
        foreach (var member in members)
        {
            if (member.SeriesMasterId is null)
            {
                continue;
            }

            try
            {
                await graphClient.Users[member.SheetMailbox].Events[member.SeriesMasterId].DeleteAsync(cancellationToken: ct);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                // Series master already gone on this sheet (manually removed, or a prior partial
                // cancel already got it) - nothing left to delete here. Don't let one already-gone
                // member abort the rest of the group's cancellation.
            }
        }
    }

    public async Task<List<SheetBooking>> GetBookingsAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var events = await GetEventsInRangeAsync(sheetMailbox, start, end, ct);
        return events.Select(e => FromGraphEvent(sheetMailbox, e)).ToList();
    }

    /// <summary>Fans out across all 5 sheets in parallel and merges the results - each item
    /// already carries its own SheetMailbox, so callers can group by sheet or by
    /// BookingGroupId as needed.</summary>
    public async Task<List<SheetBooking>> GetBookingsForAllSheetsAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        var tasks = Sheets.All.Select(sheet => GetBookingsAsync(sheet, start, end, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<SheetBooking> GetEventAsync(string sheetMailbox, string eventId, CancellationToken ct)
    {
        // Re-fetch rather than trust a PATCH/POST response shape - extended properties are only
        // returned when explicitly expanded, and that's not guaranteed on those response bodies.
        var refreshed = await graphClient.Users[sheetMailbox].Events[eventId].GetAsync(config =>
        {
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
            config.Headers.Add("Prefer", $"outlook.timezone=\"{FacilityTimeZone}\"");
        }, ct);

        return FromGraphEvent(sheetMailbox, refreshed!);
    }

    private async Task<List<Event>> GetEventsInRangeAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct)
    {
        var allEvents = new List<Event>();
        var response = await graphClient.Users[sheetMailbox].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = FacilityGraphConventions.ToUtcQueryString(start);
            config.QueryParameters.EndDateTime = FacilityGraphConventions.ToUtcQueryString(end);
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
            config.Headers.Add("Prefer", $"outlook.timezone=\"{FacilityTimeZone}\"");
        }, ct);

        // calendarView pages its results - a wide window (e.g. a 6-week month view) with several
        // recurring series expanded into occurrences can easily exceed one page. Only reading
        // the first page silently truncates later occurrences; follow @odata.nextLink until exhausted.
        while (response is not null)
        {
            if (response.Value is not null)
            {
                allEvents.AddRange(response.Value);
            }

            response = response.OdataNextLink is not null
                ? await graphClient.Users[sheetMailbox].CalendarView.WithUrl(response.OdataNextLink).GetAsync(cancellationToken: ct)
                : null;
        }

        return allEvents;
    }

    private static Event ToGraphEvent(SheetBooking booking, bool includeTime = true)
    {
        var subject = string.IsNullOrWhiteSpace(booking.RenterName)
            ? booking.Category.ToString()
            : $"{booking.Category} - {booking.RenterName}";

        var extendedProps = new List<SingleValueLegacyExtendedProperty>
        {
            new()
            {
                Id = DetailsPropertyId,
                Value = JsonSerializer.Serialize(new BookingDetails(booking.RenterName, booking.RenterPhone, booking.RenterEmail, booking.Notes))
            },
            new()
            {
                Id = GroupIdPropertyId,
                Value = booking.BookingGroupId.ToString()
            }
        };

        if (!string.IsNullOrWhiteSpace(booking.BookedBy))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = BookedByPropertyId, Value = booking.BookedBy });
        }

        var graphEvent = new Event
        {
            Subject = subject,
            ShowAs = booking.State == BookingState.Confirmed ? FreeBusyStatus.Busy : FreeBusyStatus.Tentative,
            Categories = [booking.Category.ToString()],
            SingleValueExtendedProperties = extendedProps
        };

        if (includeTime)
        {
            graphEvent.Start = new DateTimeTimeZone { DateTime = booking.Start.ToString("s"), TimeZone = FacilityTimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = booking.End.ToString("s"), TimeZone = FacilityTimeZone };
        }

        return graphEvent;
    }

    private static SheetBooking FromGraphEvent(string sheetMailbox, Event e)
    {
        var category = Enum.TryParse<BookingCategory>(e.Categories?.FirstOrDefault(), out var parsedCategory)
            ? parsedCategory
            : BookingCategory.Other;

        var state = e.ShowAs == FreeBusyStatus.Busy ? BookingState.Confirmed : BookingState.Hold;

        var detailsJson = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == DetailsPropertyId)?.Value;
        var bookedBy = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == BookedByPropertyId)?.Value;
        var groupIdRaw = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == GroupIdPropertyId)?.Value;

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
            RenterPhone = details?.RenterPhone,
            RenterEmail = details?.RenterEmail,
            Notes = details?.Notes,
            BookedBy = bookedBy,
            BookingGroupId = Guid.TryParse(groupIdRaw, out var groupId) ? groupId : Guid.Empty,
            SeriesMasterId = e.SeriesMasterId
        };
    }

    private sealed record BookingDetails(string? RenterName, string? RenterPhone, string? RenterEmail, string? Notes);
}
