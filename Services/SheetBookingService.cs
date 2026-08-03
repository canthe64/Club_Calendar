using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FacilityScheduler.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace FacilityScheduler.Services;

/// <summary>
/// Owns conflict enforcement for sheet bookings. Direct Graph writes bypass the Resource
/// Booking Attendant (confirmed via spike, architecture doc D3/S6.1), so this service is the
/// only thing standing between two overlapping bookings on the same sheet.
/// </summary>
public class SheetBookingService(GraphServiceClient graphClient, IMemoryCache cache, FacilityConfiguration facility, AppLogService log)
{
    // Standard-tier action logging for the external-booking-source methods below
    // (ClaimHoldAsync/ForceCreateConfirmedAsync) is done by BreelyBookingProcessor itself, not here -
    // it has the richer context (Breely event id, reschedule-vs-fresh-claim) to write one clear log
    // line per real-world action, rather than this generic layer guessing at "why" and duplicating it.

    // One semaphore per sheet mailbox, lazily created. Serializes create/confirm/cancel per
    // sheet so the check-then-write conflict check can't race. Adequate at the app's known
    // concurrency (1-2 staff); would need a distributed lock if this ever ran multi-instance.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SheetLocks = new();

    // Phase 7: a short-TTL read cache for GetBookingsForAllSheetsAsync only - the "give me
    // everything in this window, for display" method used by Calendar.razor and
    // PublicAvailabilityService. Deliberately NOT applied to GetEventsInRangeAsync/GetBookingsAsync,
    // which every conflict check (CreateAsync, CreateAcrossSheetsAsync, UpdateGroupAsync,
    // PreviewSeriesConflictsAsync) reads directly - conflict enforcement must always see live data,
    // never a cached snapshot that could mask a just-created booking. Keys are tracked here (not
    // static, since this service is registered as a singleton) so a write can invalidate every
    // outstanding view-cache entry without needing precise per-window overlap logic.
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

    // Fixed GUID namespace for this app's custom extended properties. BookedBy and
    // BookingGroupId are named, individually filterable properties; everything display-only
    // is bundled into one JSON blob (architecture doc S4.1 design rule).
    private const string PropertyGuid = FacilityGraphConventions.PropertyGuid;
    private const string BookedByPropertyId = $"String {{{PropertyGuid}}} Name BookedBy";
    private const string DetailsPropertyId = $"String {{{PropertyGuid}}} Name BookingDetails";
    private const string GroupIdPropertyId = $"String {{{PropertyGuid}}} Name BookingGroupId";
    private const string ExternalIdPropertyId = $"String {{{PropertyGuid}}} Name ExternalBookingId";

    // A blanket $expand=singleValueExtendedProperties is not sufficient in practice - Graph
    // appears to require the $filter sub-clause scoped to the specific property IDs to actually
    // populate results, matching the pattern shown in Microsoft's own documentation examples.
    private static readonly string[] ExtendedPropertiesExpand =
    [
        $"singleValueExtendedProperties($filter=id eq '{DetailsPropertyId}' or id eq '{BookedByPropertyId}' or id eq '{GroupIdPropertyId}' or id eq '{ExternalIdPropertyId}')"
    ];

    // Breely (and any future external booking source) sends its own event id as a plain integer -
    // this is deliberately strict rather than trying to escape arbitrary input, since the value is
    // embedded directly into a Graph $filter query string (FindByExternalIdAsync) and this app has
    // no other reason to accept anything an OData filter clause could misinterpret.
    private static readonly Regex ExternalIdPattern = new(@"^[A-Za-z0-9:_-]+$", RegexOptions.Compiled);

    public Task<BookingResult> CreateHoldAsync(SheetBooking booking, string actingUser, CancellationToken ct = default)
    {
        booking.State = BookingState.Hold;
        return CreateAsync(booking, actingUser, ct);
    }

    public Task<BookingResult> CreateConfirmedAsync(SheetBooking booking, string actingUser, CancellationToken ct = default)
    {
        booking.State = BookingState.Confirmed;
        return CreateAsync(booking, actingUser, ct);
    }

    private async Task<BookingResult> CreateAsync(SheetBooking booking, string actingUser, CancellationToken ct)
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
            InvalidateViewCache();
            await log.LogActionAsync("BookingCreated", actingUser, booking.EventId, booking.SheetMailbox,
                $"{booking.Category} {booking.State}, {booking.Start:g}-{booking.End:g}" + (string.IsNullOrWhiteSpace(booking.RenterName) ? "" : $", {booking.RenterName}"), ct);
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
    public async Task<GroupBookingResult> CreateAcrossSheetsAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, string actingUser, CancellationToken ct = default)
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

            InvalidateViewCache();
            await log.LogActionAsync("BookingCreated", actingUser, string.Join(",", created.Select(c => c.EventId)), string.Join(",", orderedSheets),
                $"{template.Category} {template.State}, {template.Start:g}-{template.End:g}" + (string.IsNullOrWhiteSpace(template.RenterName) ? "" : $", {template.RenterName}"), ct);
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

    public async Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId, string actingUser, CancellationToken ct = default)
    {
        var update = new Event { ShowAs = FreeBusyStatus.Busy };
        await graphClient.Users[sheetMailbox].Events[eventId].PatchAsync(update, cancellationToken: ct);
        InvalidateViewCache();
        await log.LogActionAsync("BookingConfirmed", actingUser, eventId, sheetMailbox, ct: ct);
        return await GetEventAsync(sheetMailbox, eventId, ct);
    }

    public async Task CancelAsync(string sheetMailbox, string eventId, string actingUser, CancellationToken ct = default)
    {
        try
        {
            await graphClient.Users[sheetMailbox].Events[eventId].DeleteAsync(cancellationToken: ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Already gone - e.g. the Breely webhook claimed/trimmed this exact hold out from under a
            // stale staff browser tab between page load and clicking Cancel (live-hit 2026-08-03,
            // same class of "already gone" 404 CancelSeriesAsync below has always tolerated). Treat
            // as already-cancelled rather than letting a 404 here crash the Blazor circuit.
            InvalidateViewCache();
            await log.LogDebugAsync("BookingCancelNoOp", actingUser, eventId, sheetMailbox, "Already gone by the time this ran.", ct);
            return;
        }

        InvalidateViewCache();
        await log.LogActionAsync("BookingCancelled", actingUser, eventId, sheetMailbox, ct: ct);
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
        IEnumerable<SheetBooking> members, SheetBooking updatedFields, string actingUser, Guid? newBookingGroupId = null, CancellationToken ct = default)
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

            InvalidateViewCache();
            await log.LogActionAsync("BookingUpdated", actingUser, string.Join(",", updated.Select(u => u.EventId)), string.Join(",", orderedSheets),
                $"{updatedFields.Category} {updatedFields.State}, {updatedFields.Start:g}-{updatedFields.End:g}" + (string.IsNullOrWhiteSpace(updatedFields.RenterName) ? "" : $", {updatedFields.RenterName}"), ct);
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
    /// Cancels every event in a booking group. <paramref name="reopenAsGroupEventHold"/> distinguishes
    /// the two cancel paths surfaced to staff: reopen (the slot goes back to an unclaimed
    /// "open for group event" hold, publicly bookable again) vs. close the ice (hard delete, slot no
    /// longer offered at all).
    /// </summary>
    public async Task CancelGroupAsync(IEnumerable<SheetBooking> members, bool reopenAsGroupEventHold, string actingUser, CancellationToken ct = default)
    {
        var memberList = members.ToList();
        var affected = new List<SheetBooking>();
        var alreadyGone = new List<SheetBooking>();

        foreach (var member in memberList)
        {
            if (member.EventId is null)
            {
                continue;
            }

            try
            {
                if (reopenAsGroupEventHold)
                {
                    var reopened = new SheetBooking
                    {
                        SheetMailbox = member.SheetMailbox,
                        Start = member.Start,
                        End = member.End,
                        Category = BookingCategory.GroupEvent,
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

                affected.Add(member);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                // Already gone - e.g. the Breely webhook claimed/trimmed this exact hold out from
                // under a stale staff browser tab between page load and clicking Cancel (live-hit
                // 2026-08-03, same class of "already gone" 404 CancelSeriesAsync below has always
                // tolerated). Treat as already-cancelled for this member and keep processing the
                // rest of the group, rather than letting a 404 here crash the Blazor circuit.
                alreadyGone.Add(member);
            }
        }

        InvalidateViewCache();
        var action = reopenAsGroupEventHold ? "BookingReleased" : "BookingCancelled";

        if (affected.Count > 0)
        {
            await log.LogActionAsync(action, actingUser,
                string.Join(",", affected.Select(m => m.EventId)),
                string.Join(",", affected.Select(m => m.SheetMailbox).Distinct()),
                alreadyGone.Count > 0 ? $"{alreadyGone.Count} other member(s) were already gone." : null, ct);
        }
        else if (alreadyGone.Count > 0)
        {
            await log.LogDebugAsync(action + "NoOp", actingUser,
                string.Join(",", alreadyGone.Select(m => m.EventId)),
                string.Join(",", alreadyGone.Select(m => m.SheetMailbox).Distinct()),
                "All members were already gone by the time this ran.", ct);
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
        IEnumerable<string> sheetMailboxes, SheetBooking template, DateTime lastOccurrenceDate, IEnumerable<DateTime> excludedDates, string actingUser, CancellationToken ct = default)
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
                    RecurrenceTimeZone = facility.TimeZone
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
                    config.QueryParameters.StartDateTime = facility.ToUtcQueryString(template.Start);
                    config.QueryParameters.EndDateTime = facility.ToUtcQueryString(lastOccurrenceDate.Date.AddDays(1));
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

                    var instanceDate = facility.FromUtcResponseString(instance.Start.DateTime).Date;
                    if (excluded.Contains(instanceDate))
                    {
                        await graphClient.Users[sheet].Events[instance.Id].DeleteAsync(cancellationToken: ct);
                    }
                }
            }
        }

        InvalidateViewCache();
        await log.LogActionAsync("SeriesCreated", actingUser, string.Join(",", created.Select(c => c.EventId)), string.Join(",", orderedSheets),
            $"{template.Category}, weekly through {lastOccurrenceDate:d}" + (string.IsNullOrWhiteSpace(template.RenterName) ? "" : $", {template.RenterName}"), ct);
        return created;
    }

    /// <summary>
    /// Deletes the entire recurring series (all occurrences, past and future) for every sheet in
    /// the group. This is the "backdoor" for correcting a data-entry mistake at series creation -
    /// deliberately not a primary UX path. No-op for members that aren't part of a series.
    /// </summary>
    public async Task CancelSeriesAsync(IEnumerable<SheetBooking> members, string actingUser, CancellationToken ct = default)
    {
        var memberList = members.ToList();
        foreach (var member in memberList)
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

        InvalidateViewCache();
        await log.LogActionAsync("SeriesCancelled", actingUser,
            string.Join(",", memberList.Select(m => m.SeriesMasterId).Where(id => id is not null)),
            string.Join(",", memberList.Select(m => m.SheetMailbox).Distinct()), ct: ct);
    }

    // ── External booking source support (e.g. a booking-platform webhook) ─────────────────────────
    // These three methods exist for one caller: an external booking notification that already
    // happened in the real world and must be reflected here, not re-validated against it. Unlike
    // every method above, a hold on the same sheet is treated as claimable rather than a conflict -
    // that's exactly what an open hold means - and a booking that doesn't match any known
    // availability is still written rather than dropped (see ForceCreateConfirmedAsync).

    /// <summary>
    /// Finds the booking (on whichever sheet it currently lives on, at whatever time it's currently
    /// scheduled) tagged with this external booking id, or null if none exists yet. Used to upsert
    /// on repeat/rescheduled notifications for the same external booking - there is no companion
    /// database (architecture doc D7) to look this up in, so it's a live Graph query, one per
    /// configured sheet in the worst case (fine at this app's volume).
    /// </summary>
    public async Task<SheetBooking?> FindByExternalIdAsync(string externalBookingId, CancellationToken ct = default)
    {
        if (!ExternalIdPattern.IsMatch(externalBookingId))
        {
            throw new ArgumentException("externalBookingId contains characters unsafe for a Graph $filter query.", nameof(externalBookingId));
        }

        foreach (var sheet in facility.SheetMailboxes)
        {
            var response = await graphClient.Users[sheet].Events.GetAsync(config =>
            {
                config.QueryParameters.Filter = $"singleValueExtendedProperties/Any(ep: ep/id eq '{ExternalIdPropertyId}' and ep/value eq '{externalBookingId}')";
                config.QueryParameters.Expand = ExtendedPropertiesExpand;
            }, ct);

            var match = response?.Value?.FirstOrDefault();
            if (match is not null)
            {
                return FromGraphEvent(sheet, match);
            }
        }

        return null;
    }

    /// <summary>
    /// Claims an open Group Event hold for an externally-sourced booking - tries sheets in
    /// <see cref="FacilityConfiguration.SheetMailboxes"/> order (Sheet 1 first) and claims the
    /// first one whose open hold(s) fully cover [start, end). The claimed hold is trimmed to
    /// reflect the remaining open time (deleted if nothing remains, patched if one segment
    /// remains, split into two events if the claim was in the middle of it) - a hold that's an
    /// occurrence of a recurring series has that occurrence deleted and standalone events created
    /// for any remainder instead, since Graph rejects a time-bearing PATCH on a recurring
    /// occurrence even when the time is technically unchanged. Returns null if no sheet's hold(s)
    /// fully cover the window; the caller decides how to handle that (see ForceCreateConfirmedAsync).
    /// </summary>
    public async Task<SheetBooking?> ClaimHoldAsync(DateTime start, DateTime end, SheetBooking template, CancellationToken ct = default)
    {
        foreach (var sheet in facility.SheetMailboxes)
        {
            var sem = SheetLocks.GetOrAdd(sheet, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                var events = await GetEventsInRangeAsync(sheet, start, end, ct);
                var covering = events
                    .Select(e => FromGraphEvent(sheet, e))
                    .Where(b => b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold)
                    .Where(b => b.Start <= start && b.End >= end)
                    .ToList();

                if (covering.Count == 0)
                {
                    continue; // no covering hold on this sheet - try the next
                }

                var hold = covering[0];

                var booking = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = start,
                    End = end,
                    Category = template.Category,
                    State = BookingState.Confirmed,
                    RenterName = template.RenterName,
                    RenterPhone = template.RenterPhone,
                    RenterEmail = template.RenterEmail,
                    Notes = template.Notes,
                    BookedBy = template.BookedBy,
                    ExternalBookingId = template.ExternalBookingId,
                    BookingGroupId = Guid.NewGuid()
                };

                var graphEvent = ToGraphEvent(booking);
                var created = await graphClient.Users[sheet].Events.PostAsync(graphEvent, cancellationToken: ct);
                booking.EventId = created?.Id;
                booking.ICalUId = created?.ICalUId;

                await TrimHoldAsync(sheet, hold, start, end, ct);

                InvalidateViewCache();
                return booking;
            }
            finally
            {
                sem.Release();
            }
        }

        return null;
    }

    private async Task TrimHoldAsync(string sheet, SheetBooking hold, DateTime claimedStart, DateTime claimedEnd, CancellationToken ct)
    {
        var remainders = CalendarStyles.SubtractIntervals(hold.Start, hold.End, [(claimedStart, claimedEnd)]);

        if (hold.SeriesMasterId is not null)
        {
            // Delete this occurrence and create standalone events for whatever remains, rather than
            // trying to PATCH it in place - Graph rejects a time-bearing PATCH on a recurring
            // occurrence with "Modified occurrence is crossing or overlapping adjacent occurrence"
            // even when the resulting time doesn't actually conflict with anything.
            if (hold.EventId is not null)
            {
                await graphClient.Users[sheet].Events[hold.EventId].DeleteAsync(cancellationToken: ct);
            }

            foreach (var (segStart, segEnd) in remainders)
            {
                if (segEnd <= segStart)
                {
                    continue;
                }

                var remainderHold = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = segStart,
                    End = segEnd,
                    Category = BookingCategory.GroupEvent,
                    State = BookingState.Hold,
                    BookingGroupId = Guid.NewGuid()
                };
                await graphClient.Users[sheet].Events.PostAsync(ToGraphEvent(remainderHold), cancellationToken: ct);
            }

            return;
        }

        if (remainders.Count == 0)
        {
            if (hold.EventId is not null)
            {
                await graphClient.Users[sheet].Events[hold.EventId].DeleteAsync(cancellationToken: ct);
            }
            return;
        }

        // One remainder: patch the existing hold in place to the shrunken window.
        var (firstStart, firstEnd) = remainders[0];
        var patch = new Event
        {
            Start = new DateTimeTimeZone { DateTime = firstStart.ToString("s"), TimeZone = facility.TimeZone },
            End = new DateTimeTimeZone { DateTime = firstEnd.ToString("s"), TimeZone = facility.TimeZone }
        };
        await graphClient.Users[sheet].Events[hold.EventId!].PatchAsync(patch, cancellationToken: ct);

        if (remainders.Count < 2)
        {
            return;
        }

        // Two remainders (the claim was in the middle of the hold): the patch above covers the
        // first fragment; create a new event for the second.
        var (secondStart, secondEnd) = remainders[1];
        var secondHold = new SheetBooking
        {
            SheetMailbox = sheet,
            Start = secondStart,
            End = secondEnd,
            Category = BookingCategory.GroupEvent,
            State = BookingState.Hold,
            BookingGroupId = Guid.NewGuid()
        };
        await graphClient.Users[sheet].Events.PostAsync(ToGraphEvent(secondHold), cancellationToken: ct);
    }

    /// <summary>
    /// Writes a Confirmed booking directly, bypassing the conflict check entirely - the last-resort
    /// fallback when an externally-sourced booking doesn't match any advertised open hold on any
    /// sheet. Graph itself never enforces non-overlap (D3) - this app's own conflict check is the
    /// only thing that normally prevents an overlap, and this method deliberately steps around it
    /// because the booking already happened in the real world regardless of what this calendar
    /// currently shows; never dropping a real booking matters more than keeping the calendar tidy.
    /// The caller is responsible for flagging this for staff review - this method never runs silently.
    /// </summary>
    public async Task<SheetBooking> ForceCreateConfirmedAsync(string sheetMailbox, SheetBooking booking, CancellationToken ct = default)
    {
        var sem = SheetLocks.GetOrAdd(sheetMailbox, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            booking.SheetMailbox = sheetMailbox;
            booking.State = BookingState.Confirmed;
            if (booking.BookingGroupId == Guid.Empty)
            {
                booking.BookingGroupId = Guid.NewGuid();
            }

            var graphEvent = ToGraphEvent(booking);
            var created = await graphClient.Users[sheetMailbox].Events.PostAsync(graphEvent, cancellationToken: ct);
            booking.EventId = created?.Id;
            booking.ICalUId = created?.ICalUId;

            InvalidateViewCache();
            return booking;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<List<SheetBooking>> GetBookingsAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var events = await GetEventsInRangeAsync(sheetMailbox, start, end, ct);
        return events.Select(e => FromGraphEvent(sheetMailbox, e)).ToList();
    }

    /// <summary>Fans out across every configured sheet in parallel and merges the results - each item
    /// already carries its own SheetMailbox, so callers can group by sheet or by
    /// BookingGroupId as needed. Read-cached (Phase 7, 30s TTL) - this is the view-rendering read
    /// path (Calendar.razor, PublicAvailabilityService), not a conflict check, so a short-lived
    /// cached snapshot is safe here even though it never is for GetEventsInRangeAsync/GetBookingsAsync.</summary>
    public async Task<List<SheetBooking>> GetBookingsForAllSheetsAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        var cacheKey = $"sheetbookings:{start:O}:{end:O}";
        if (cache.TryGetValue(cacheKey, out List<SheetBooking>? cached) && cached is not null)
        {
            return cached;
        }

        var tasks = facility.SheetMailboxes.Select(sheet => GetBookingsAsync(sheet, start, end, ct));
        var results = await Task.WhenAll(tasks);
        var combined = results.SelectMany(r => r).ToList();

        cache.Set(cacheKey, combined, ViewCacheTtl);
        _viewCacheKeys[cacheKey] = 0;
        return combined;
    }

    private async Task<SheetBooking> GetEventAsync(string sheetMailbox, string eventId, CancellationToken ct)
    {
        // Re-fetch rather than trust a PATCH/POST response shape - extended properties are only
        // returned when explicitly expanded, and that's not guaranteed on those response bodies.
        var refreshed = await graphClient.Users[sheetMailbox].Events[eventId].GetAsync(config =>
        {
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
        }, ct);

        return FromGraphEvent(sheetMailbox, refreshed!);
    }

    private async Task<List<Event>> GetEventsInRangeAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct)
    {
        var allEvents = new List<Event>();
        var response = await graphClient.Users[sheetMailbox].CalendarView.GetAsync(config =>
        {
            config.QueryParameters.StartDateTime = facility.ToUtcQueryString(start);
            config.QueryParameters.EndDateTime = facility.ToUtcQueryString(end);
            config.QueryParameters.Expand = ExtendedPropertiesExpand;
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

    private Event ToGraphEvent(SheetBooking booking, bool includeTime = true)
    {
        var subject = string.IsNullOrWhiteSpace(booking.RenterName)
            ? CalendarStyles.CategoryLabel(booking.Category)
            : $"{CalendarStyles.CategoryLabel(booking.Category)} - {booking.RenterName}";

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

        if (!string.IsNullOrWhiteSpace(booking.ExternalBookingId))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = ExternalIdPropertyId, Value = booking.ExternalBookingId });
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
            graphEvent.Start = new DateTimeTimeZone { DateTime = booking.Start.ToString("s"), TimeZone = facility.TimeZone };
            graphEvent.End = new DateTimeTimeZone { DateTime = booking.End.ToString("s"), TimeZone = facility.TimeZone };
        }

        return graphEvent;
    }

    private SheetBooking FromGraphEvent(string sheetMailbox, Event e)
    {
        var category = Enum.TryParse<BookingCategory>(e.Categories?.FirstOrDefault(), out var parsedCategory)
            ? parsedCategory
            : BookingCategory.Other;

        var state = e.ShowAs == FreeBusyStatus.Busy ? BookingState.Confirmed : BookingState.Hold;

        var detailsJson = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == DetailsPropertyId)?.Value;
        var bookedBy = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == BookedByPropertyId)?.Value;
        var groupIdRaw = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == GroupIdPropertyId)?.Value;
        var externalBookingId = e.SingleValueExtendedProperties?.FirstOrDefault(p => p.Id == ExternalIdPropertyId)?.Value;

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
            Start = facility.FromUtcResponseString(e.Start?.DateTime ?? DateTime.UtcNow.ToString("o")),
            End = facility.FromUtcResponseString(e.End?.DateTime ?? DateTime.UtcNow.ToString("o")),
            Category = category,
            State = state,
            RenterName = details?.RenterName,
            RenterPhone = details?.RenterPhone,
            RenterEmail = details?.RenterEmail,
            Notes = details?.Notes,
            BookedBy = bookedBy,
            BookingGroupId = Guid.TryParse(groupIdRaw, out var groupId) ? groupId : Guid.Empty,
            SeriesMasterId = e.SeriesMasterId,
            ExternalBookingId = externalBookingId
        };
    }

    private sealed record BookingDetails(string? RenterName, string? RenterPhone, string? RenterEmail, string? Notes);
}
