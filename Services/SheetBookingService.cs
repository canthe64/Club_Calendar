using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FacilityScheduler.Domain;
using FacilityScheduler.Services.Graph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace FacilityScheduler.Services;

/// <summary>
/// Owns conflict enforcement for sheet bookings. Direct Graph writes bypass the Resource
/// Booking Attendant (confirmed via spike, architecture doc D3/S6.1), so this service is the
/// only thing standing between two overlapping bookings on the same sheet.
/// </summary>
public class SheetBookingService(IGraphEventGateway graph, IMemoryCache cache, FacilityConfiguration facility, AppLogService log, ViewCacheRegistry viewCache, SchedulingWindowService window)
{
    // Title given to a leftover Group Event hold created/kept by TrimHoldAsync when claiming a
    // Breely booking (architecture doc §4.8) - distinguishes an app-generated "the rest of this slot
    // is still open" fragment from a plain staff-created hold, which keeps its category-label title.
    private const string AvailableForGroupEventsTitle = "Available for Group Events";

    // Minimum group event booking interval (Settings page) - a leftover fragment before/after a
    // claimed Breely booking shorter than this is dropped instead of offered as its own bookable
    // slot (TrimHoldAsync below). Deliberately not a whole separate service for one int: read once
    // at construction, updated in memory and re-persisted only when Settings saves a change.
    private const string MinimumIntervalFileName = "booking-policy.txt";
    private const int DefaultMinimumGroupEventBookingIntervalMinutes = 60;
    private int _minimumGroupEventBookingIntervalMinutes = LoadMinimumInterval(log.LogDirectory);

    public int MinimumGroupEventBookingIntervalMinutes => _minimumGroupEventBookingIntervalMinutes;

    public async Task SetMinimumGroupEventBookingIntervalAsync(int minutes, string actor, CancellationToken ct = default)
    {
        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Minimum interval cannot be negative.");
        }

        var previous = _minimumGroupEventBookingIntervalMinutes;
        _minimumGroupEventBookingIntervalMinutes = minutes;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(log.LogDirectory, MinimumIntervalFileName), minutes.ToString(CultureInfo.InvariantCulture), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence - the in-memory value above is already updated and takes
            // effect immediately either way; a failed write just means it won't survive a restart.
        }

        if (previous != minutes)
        {
            await log.LogActionAsync("MinimumGroupEventBookingIntervalChanged", actor, details: $"{previous} -> {minutes} minutes", ct: ct);
        }
    }

    private static int LoadMinimumInterval(string logDirectory)
    {
        try
        {
            var path = Path.Combine(logDirectory, MinimumIntervalFileName);
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0)
            {
                return minutes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the default - not worth failing startup over.
        }

        return DefaultMinimumGroupEventBookingIntervalMinutes;
    }

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

        // Also clear the public-facing layer that sits on top of these reads. It used to expire on
        // its own TTL only, on the reasoning that nothing wrote through it - practice ice hosting
        // made that false, and a staff booking equally ought not leave the public calendar showing
        // ice as free for another minute (ViewCacheRegistry).
        viewCache.InvalidateAll();
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
            var created = await graph.CreateEventAsync(booking.SheetMailbox, graphEvent, ct);

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
        // Cheap short-circuit before any lock/Graph call - covers both the staff booking form and
        // PracticeIceRequestService.SubmitAsync (which calls this same method), so gating it here
        // is sufficient for both without touching PracticeIceRequestService at all. Deliberately does
        // NOT cover Breely (BreelyBookingProcessor writes through ClaimHoldAsync, a different method)
        // or edits (UpdateGroupAsync/UpdateSeriesAsync, also different methods) - both excluded by
        // construction, matching the operator's "new creations only, not Breely" decision with no
        // special-casing needed.
        if (window.IsOutsideSeason(template.Start))
        {
            return GroupBookingResult.Conflict([OutOfSeasonConflict(template.Start, template.End, window.SeasonStartDate, window.SeasonEndDate)]);
        }

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

            // The conflict check above makes this all-or-nothing against *existing* bookings, but
            // the writes themselves are separate Graph calls with no transaction behind them. A
            // failure partway through (a 429, a 5xx, a timeout, an over-size extended property) used
            // to leave the sheets already written in place while the caller saw an error - a
            // half-created booking carrying a BookingGroupId nobody holds a reference to, which
            // reads as a real booking on the calendar and blocks that ice. Roll them back instead,
            // then rethrow so the caller still surfaces the failure.
            try
            {
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
                    var result = await graph.CreateEventAsync(sheet, graphEvent, ct);
                    booking.EventId = result?.Id;
                    booking.ICalUId = result?.ICalUId;
                    created.Add(booking);
                }
            }
            catch (Exception ex)
            {
                await RollbackCreatedAsync(created, actingUser, ex, ct);
                throw;
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
        await graph.PatchEventAsync(sheetMailbox, eventId, update, ct);
        InvalidateViewCache();
        await log.LogActionAsync("BookingConfirmed", actingUser, eventId, sheetMailbox, ct: ct);
        return await GetEventAsync(sheetMailbox, eventId, ct);
    }

    public async Task CancelAsync(string sheetMailbox, string eventId, string actingUser, CancellationToken ct = default)
    {
        try
        {
            await graph.DeleteEventAsync(sheetMailbox, eventId, ct);
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
    /// state (hold vs. confirmed) all come from <paramref name="updatedFields"/>. Re-checks
    /// conflicts against the new time on each member's sheet before writing anything (all-or-nothing,
    /// same philosophy as CreateAcrossSheetsAsync) - each member's own current event is excluded from
    /// its own conflict check, so an edit that doesn't move the time never conflicts with itself.
    /// <paramref name="newBookingGroupId"/>, when given, reassigns all updated members to a new group
    /// - used when a caller only edited a subset of the original group's sheets, so the edited subset
    /// splits off rather than staying linked to sheets that were deliberately left untouched.
    /// <paramref name="newSheetMailboxes"/> adds brand-new sheets to the group in the same operation -
    /// a sheet with no existing member has no event to PATCH, so without this it was silently dropped
    /// (live-found 2026-08-03: adding a sheet to an existing multi-sheet hold appeared to save
    /// successfully but never actually created anything on the new sheet). Conflict-checked and
    /// locked alongside the existing members, same all-or-nothing guarantee.
    /// </summary>
    public async Task<GroupBookingResult> UpdateGroupAsync(
        IEnumerable<SheetBooking> members, SheetBooking updatedFields, string actingUser,
        IEnumerable<string>? newSheetMailboxes = null, Guid? newBookingGroupId = null, CancellationToken ct = default)
    {
        var memberList = members.Where(m => m.EventId is not null).ToList();
        var existingSheets = memberList.Select(m => m.SheetMailbox).ToHashSet();
        var newSheets = (newSheetMailboxes ?? []).Distinct().Where(s => !existingSheets.Contains(s)).ToList();

        if (memberList.Count == 0 && newSheets.Count == 0)
        {
            return GroupBookingResult.Success([]);
        }

        var orderedSheets = existingSheets.Concat(newSheets).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
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
            foreach (var sheet in newSheets)
            {
                var overlapping = await GetEventsInRangeAsync(sheet, updatedFields.Start, updatedFields.End, ct);
                conflicts.AddRange(overlapping.Select(e => FromGraphEvent(sheet, e)));
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
                await graph.PatchEventAsync(member.SheetMailbox, member.EventId!, graphEvent, ct);
                merged.EventId = member.EventId;
                updated.Add(merged);
            }

            // New sheets join whatever group id the rest of this edit settles on - explicitly given
            // (a split happened) or, failing that, whatever the retained members already share.
            // Guid.NewGuid() only applies if there were no existing members at all (every sheet in
            // this "edit" is brand new).
            var targetGroupId = newBookingGroupId ?? memberList.FirstOrDefault()?.BookingGroupId ?? Guid.NewGuid();

            // Same partial-write rollback as CreateAcrossSheetsAsync, scoped to the sheets newly
            // *created* here. The PATCHes above are deliberately not rolled back: restoring them
            // would mean replaying each member's prior state, and a half-applied edit still leaves
            // real bookings on real sheets, whereas a half-created new sheet leaves a phantom event
            // in a group the caller was told didn't save. See §8 for the residual on PATCH.
            var newlyCreated = new List<SheetBooking>();
            try
            {
                foreach (var sheet in newSheets)
                {
                    var created = new SheetBooking
                    {
                        SheetMailbox = sheet,
                        Start = updatedFields.Start,
                        End = updatedFields.End,
                        Category = updatedFields.Category,
                        State = updatedFields.State,
                        RenterName = updatedFields.RenterName,
                        RenterPhone = updatedFields.RenterPhone,
                        RenterEmail = updatedFields.RenterEmail,
                        Notes = updatedFields.Notes,
                        BookedBy = updatedFields.BookedBy,
                        BookingGroupId = targetGroupId
                    };

                    var graphEvent = ToGraphEvent(created);
                    var result = await graph.CreateEventAsync(sheet, graphEvent, ct);
                    created.EventId = result?.Id;
                    created.ICalUId = result?.ICalUId;
                    newlyCreated.Add(created);
                    updated.Add(created);
                }
            }
            catch (Exception ex)
            {
                await RollbackCreatedAsync(newlyCreated, actingUser, ex, ct);
                throw;
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

        // Locked per sheet (sorted order, same deadlock-avoidance reasoning as CreateAcrossSheetsAsync)
        // because reopening now reads other holds on the sheet before deciding what to write
        // (AbsorbAdjacentHoldsAsync) - a genuine read-then-write race against a concurrent Breely
        // claim on the same sheet, not just a courtesy lock.
        var orderedSheets = memberList.Select(m => m.SheetMailbox).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sems = orderedSheets.Select(s => SheetLocks.GetOrAdd(s, _ => new SemaphoreSlim(1, 1))).ToList();
        foreach (var sem in sems)
        {
            await sem.WaitAsync(ct);
        }

        try
        {
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
                        var (mergedStart, mergedEnd) = await AbsorbAdjacentHoldsAsync(member.SheetMailbox, member.Start, member.End, member.EventId, ct);
                        var merged = mergedStart != member.Start || mergedEnd != member.End;

                        var reopened = new SheetBooking
                        {
                            SheetMailbox = member.SheetMailbox,
                            Start = mergedStart,
                            End = mergedEnd,
                            Category = BookingCategory.GroupEvent,
                            State = BookingState.Hold,
                            BookingGroupId = merged ? Guid.NewGuid() : member.BookingGroupId
                            // Renter-specific fields intentionally omitted - back to a plain open hold.
                        };

                        if (merged && member.SeriesMasterId is not null)
                        {
                            // Can't widen a recurring occurrence's time via PATCH - Graph rejects a
                            // Start/End change on an occurrence that would cross into an adjacent
                            // occurrence, same restriction TrimHoldAsync already works around. Delete
                            // this occurrence and create a standalone merged hold instead.
                            await graph.DeleteEventAsync(member.SheetMailbox, member.EventId, ct);
                            await graph.CreateEventAsync(member.SheetMailbox, ToGraphEvent(reopened, titleOverride: AvailableForGroupEventsTitle, clearUnsetOptionalProperties: true), ct);
                        }
                        else
                        {
                            var graphEvent = ToGraphEvent(reopened, includeTime: merged, titleOverride: AvailableForGroupEventsTitle, clearUnsetOptionalProperties: true);
                            await graph.PatchEventAsync(member.SheetMailbox, member.EventId, graphEvent, ct);
                        }
                    }
                    else
                    {
                        await graph.DeleteEventAsync(member.SheetMailbox, member.EventId, ct);
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
        }
        finally
        {
            foreach (var sem in sems)
            {
                sem.Release();
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
    /// Finds any open Group Event hold(s) on the same sheet immediately touching [start, end) -
    /// i.e. another hold whose End equals start, or whose Start equals end - and folds them into one
    /// contiguous span, deleting the absorbed hold(s). Called before reopening a canceled booking as
    /// a hold (CancelGroupAsync), so a booking that was claimed out of the middle of a larger hold
    /// (architecture doc §4.8's TrimHoldAsync) reunites into a single hold on cancel instead of
    /// leaving 2-3 separate back-to-back chips for what's really one open block of ice (live-found
    /// 2026-08-03: staff manually cleaning up test bookings ended up with exactly that clutter).
    /// Keeps extending outward on both sides until no more touching hold is found, so this also
    /// handles a chain longer than one neighbor per side.
    /// </summary>
    private async Task<(DateTime Start, DateTime End)> AbsorbAdjacentHoldsAsync(string sheet, DateTime start, DateTime end, string excludeEventId, CancellationToken ct)
    {
        var candidates = (await GetEventsInRangeAsync(sheet, start.AddHours(-24), end.AddHours(24), ct))
            .Select(e => FromGraphEvent(sheet, e))
            .Where(b => b.EventId != excludeEventId && b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold)
            .ToList();

        var mergedStart = start;
        var mergedEnd = end;
        var absorbedIds = new HashSet<string>();

        var extended = true;
        while (extended)
        {
            extended = false;

            var before = candidates.FirstOrDefault(b => b.EventId is not null && !absorbedIds.Contains(b.EventId) && b.End == mergedStart);
            if (before is not null)
            {
                mergedStart = before.Start;
                absorbedIds.Add(before.EventId!);
                extended = true;
            }

            var after = candidates.FirstOrDefault(b => b.EventId is not null && !absorbedIds.Contains(b.EventId) && b.Start == mergedEnd);
            if (after is not null)
            {
                mergedEnd = after.End;
                absorbedIds.Add(after.EventId!);
                extended = true;
            }
        }

        foreach (var eventId in absorbedIds)
        {
            await graph.DeleteEventAsync(sheet, eventId, ct);
        }

        return (mergedStart, mergedEnd);
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

        // Same partial-write rollback as CreateAcrossSheetsAsync - a failure on sheet 3 of 5 would
        // otherwise leave two orphaned recurring series (every occurrence of each) on the calendar
        // while the caller sees an error. Worse here than for a one-off booking, since a leftover
        // series blocks its slot every week until someone finds and cancels it.
        try
        {
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

            var result = await graph.CreateEventAsync(sheet, graphEvent, ct);
            booking.EventId = result?.Id;
            booking.ICalUId = result?.ICalUId;
            created.Add(booking);

            if (excluded.Count > 0 && result?.Id is not null)
            {
                var allInstances = await graph.GetInstancesAsync(sheet, result.Id,
                    facility.ToUtcQueryString(template.Start), facility.ToUtcQueryString(lastOccurrenceDate.Date.AddDays(1)), ct);

                foreach (var instance in allInstances)
                {
                    if (instance.Start?.DateTime is null || instance.Id is null)
                    {
                        continue;
                    }

                    var instanceDate = facility.FromUtcResponseString(instance.Start.DateTime).Date;
                    if (excluded.Contains(instanceDate))
                    {
                        await graph.DeleteEventAsync(sheet, instance.Id, ct);
                    }
                }
            }
        }
        }
        catch (Exception ex)
        {
            await RollbackCreatedAsync(created, actingUser, ex, ct);
            throw;
        }

        InvalidateViewCache();
        await log.LogActionAsync("SeriesCreated", actingUser, string.Join(",", created.Select(c => c.EventId)), string.Join(",", orderedSheets),
            $"{template.Category}, weekly through {lastOccurrenceDate:d}" + (string.IsNullOrWhiteSpace(template.RenterName) ? "" : $", {template.RenterName}"), ct);
        return created;
    }

    /// <summary>
    /// Edits a recurring series as a whole: metadata on every occurrence (past included), plus
    /// adding and removing sheets. Time is deliberately not editable - moving a whole series is the
    /// operation most likely to collide with everything else on the calendar, so the operator ruled
    /// it out; <see cref="UpdateGroupAsync"/> still moves a single occurrence.
    ///
    /// Each sheet carries its own native Graph series (Graph has no cross-mailbox recurrence), so
    /// "the series" is really N series sharing a BookingGroupId:
    ///  - kept sheets    - PATCH the series master, which Graph propagates to every occurrence.
    ///  - removed sheets - DELETE the series master, taking all of its occurrences with it. Never
    ///    conflicts, so it needs no check.
    ///  - added sheets   - conflict-check EVERY occurrence window first, all-or-nothing, then
    ///    replicate the reference master's recurrence onto the new sheet.
    /// </summary>
    public async Task<GroupBookingResult> UpdateSeriesAsync(
        IEnumerable<SheetBooking> members, SheetBooking updatedFields, IEnumerable<string> targetSheetMailboxes,
        string actingUser, CancellationToken ct = default)
    {
        var memberList = members.Where(m => m.SeriesMasterId is not null).ToList();
        if (memberList.Count == 0)
        {
            return GroupBookingResult.Success([]);
        }

        var existingSheets = memberList.Select(m => m.SheetMailbox).ToHashSet();
        var targets = targetSheetMailboxes.Distinct().ToHashSet();

        // Unticking every sheet would be a disguised "cancel the series" - no-op rather than let a
        // stray form state quietly delete the lot. CancelSeriesAsync is the explicit path for that.
        if (targets.Count == 0)
        {
            return GroupBookingResult.Success([]);
        }

        var toKeep = memberList.Where(m => targets.Contains(m.SheetMailbox)).ToList();
        var toRemove = memberList.Where(m => !targets.Contains(m.SheetMailbox)).ToList();
        var toAdd = targets.Where(t => !existingSheets.Contains(t)).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var orderedSheets = existingSheets.Concat(toAdd).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sems = orderedSheets.Select(s => SheetLocks.GetOrAdd(s, _ => new SemaphoreSlim(1, 1))).ToList();
        foreach (var sem in sems)
        {
            await sem.WaitAsync(ct);
        }

        var groupId = memberList[0].BookingGroupId;
        var created = new List<SheetBooking>();

        try
        {
            // One sheet's master defines the shape of the whole thing - the recurrence to replicate
            // and the occurrence windows to conflict-check against. Prefer a sheet that is staying,
            // so a "swap sheet A for sheet B" edit doesn't read its template from a master it is
            // about to delete.
            var reference = toKeep.FirstOrDefault() ?? memberList[0];
            var referenceMaster = await graph.GetEventAsync(reference.SheetMailbox, reference.SeriesMasterId!, ExtendedPropertiesExpand, ct);
            var occurrences = await SeriesOccurrenceWindowsAsync(reference, referenceMaster, ct);

            if (toAdd.Count > 0)
            {
                if (referenceMaster?.Recurrence is null || occurrences.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot add sheets to this series: no recurrence could be read from {reference.SheetMailbox}.");
                }

                var conflicts = new List<SheetBooking>();
                foreach (var sheet in toAdd)
                {
                    foreach (var (start, end) in occurrences)
                    {
                        var overlapping = await GetEventsInRangeAsync(sheet, start, end, ct);
                        conflicts.AddRange(overlapping.Select(e => FromGraphEvent(sheet, e)));
                    }
                }

                // All-or-nothing, matching every other multi-sheet write here: one colliding date on
                // one sheet refuses the whole edit rather than adding the sheet for the dates that
                // happen to be free. A series with holes is harder to reason about than a refusal.
                if (conflicts.Count > 0)
                {
                    return GroupBookingResult.Conflict(conflicts);
                }
            }

            var updated = new List<SheetBooking>();
            foreach (var member in toKeep)
            {
                var merged = MergeSeriesFields(member, updatedFields, groupId);
                await graph.PatchEventAsync(member.SheetMailbox, member.SeriesMasterId!, ToGraphEvent(merged, includeTime: false), ct);
                merged.EventId = member.EventId;
                updated.Add(merged);
            }

            foreach (var member in toRemove)
            {
                await graph.DeleteEventAsync(member.SheetMailbox, member.SeriesMasterId!, ct);
            }

            foreach (var sheet in toAdd)
            {
                var booking = MergeSeriesFields(reference, updatedFields, groupId);
                booking.SheetMailbox = sheet;

                var graphEvent = ToGraphEvent(booking);
                graphEvent.Recurrence = referenceMaster!.Recurrence;

                var result = await graph.CreateEventAsync(sheet, graphEvent, ct);
                booking.EventId = result?.Id;
                booking.ICalUId = result?.ICalUId;
                created.Add(booking);
                updated.Add(booking);

                // Mirror the reference series' own gaps. Occurrences staff excluded at creation
                // (SeriesWizard) were deleted individually, so replaying the recurrence verbatim
                // would give the new sheet dates none of the other sheets have.
                if (result?.Id is not null)
                {
                    await MirrorExcludedOccurrencesAsync(sheet, result.Id, occurrences, ct);
                }
            }

            InvalidateViewCache();
            await log.LogActionAsync("SeriesUpdated", actingUser,
                string.Join(",", memberList.Select(m => m.SeriesMasterId)),
                string.Join(",", orderedSheets),
                $"{updatedFields.Category}, {occurrences.Count} occurrence(s)"
                    + (toAdd.Count > 0 ? $", added {string.Join("/", toAdd)}" : "")
                    + (toRemove.Count > 0 ? $", removed {string.Join("/", toRemove.Select(r => r.SheetMailbox))}" : ""), ct);

            return GroupBookingResult.Success(updated);
        }
        catch (Exception ex)
        {
            // Only newly created series are rolled back, same rule as UpdateGroupAsync: a
            // half-applied PATCH still leaves a real booking on a real sheet, whereas a phantom
            // series on a sheet the caller was told didn't save blocks that slot every week.
            await RollbackCreatedAsync(created, actingUser, ex, ct);
            throw;
        }
        finally
        {
            foreach (var sem in sems)
            {
                sem.Release();
            }
        }
    }

    private static SheetBooking MergeSeriesFields(SheetBooking source, SheetBooking updatedFields, Guid groupId) =>
        new()
        {
            SheetMailbox = source.SheetMailbox,
            // Carried through unchanged - UpdateSeriesAsync never moves a series, and kept sheets
            // are patched with includeTime:false so these never reach Graph at all.
            Start = source.Start,
            End = source.End,
            Category = updatedFields.Category,
            State = source.State,
            RenterName = updatedFields.RenterName,
            RenterPhone = updatedFields.RenterPhone,
            RenterEmail = updatedFields.RenterEmail,
            Notes = updatedFields.Notes,
            BookedBy = source.BookedBy,
            BookingGroupId = groupId
        };

    /// <summary>
    /// The (start, end) window of every live occurrence of a series, read from Graph rather than
    /// recomputed from the recurrence pattern - so occurrences that were individually deleted are
    /// reflected as they actually are, not as the pattern says they should be.
    /// </summary>
    private async Task<List<(DateTime Start, DateTime End)>> SeriesOccurrenceWindowsAsync(
        SheetBooking reference, Event? master, CancellationToken ct)
    {
        if (master?.Recurrence?.Range is null)
        {
            return [];
        }

        var range = master.Recurrence.Range;
        var from = range.StartDate is { } sd ? new DateTime(sd.Year, sd.Month, sd.Day) : reference.Start.Date;
        // NoEnd/Numbered ranges carry no EndDate. This app only ever creates EndDate series
        // (CreateSeriesAsync), so the two-year ceiling is a guard for hand-edited data, not a
        // supported shape.
        var to = range.EndDate is { } ed ? new DateTime(ed.Year, ed.Month, ed.Day) : from.AddYears(2);

        var instances = await graph.GetInstancesAsync(reference.SheetMailbox, reference.SeriesMasterId!,
            facility.ToUtcQueryString(from), facility.ToUtcQueryString(to.AddDays(1)), ct);

        return instances
            .Where(i => i.Start?.DateTime is not null && i.End?.DateTime is not null)
            .Select(i => (Start: facility.FromUtcResponseString(i.Start!.DateTime!), End: facility.FromUtcResponseString(i.End!.DateTime!)))
            .OrderBy(w => w.Start)
            .ToList();
    }

    private async Task MirrorExcludedOccurrencesAsync(
        string sheet, string newMasterId, List<(DateTime Start, DateTime End)> keep, CancellationToken ct)
    {
        if (keep.Count == 0)
        {
            return;
        }

        var keepDates = keep.Select(w => w.Start.Date).ToHashSet();
        var instances = await graph.GetInstancesAsync(sheet, newMasterId,
            facility.ToUtcQueryString(keep[0].Start.Date), facility.ToUtcQueryString(keep[^1].Start.Date.AddDays(1)), ct);

        foreach (var instance in instances)
        {
            if (instance.Start?.DateTime is null || instance.Id is null)
            {
                continue;
            }

            if (!keepDates.Contains(facility.FromUtcResponseString(instance.Start.DateTime).Date))
            {
                await graph.DeleteEventAsync(sheet, instance.Id, ct);
            }
        }
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
                await graph.DeleteEventAsync(member.SheetMailbox, member.SeriesMasterId, ct);
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
            var matches = await graph.FindEventsAsync(sheet,
                $"singleValueExtendedProperties/Any(ep: ep/id eq '{ExternalIdPropertyId}' and ep/value eq '{externalBookingId}')",
                ExtendedPropertiesExpand, ct);

            if (matches is { Count: > 0 })
            {
                // Defense in depth: this app's own writers always keep at most one live match per
                // external id (ToGraphEvent's clearUnsetOptionalProperties clears it on reopen), but
                // if that invariant is ever violated - or a stray match exists from data written
                // outside the app - prefer a Confirmed booking over a Hold, since a released hold
                // wrongly carrying a stale id is exactly the scenario that invariant guards against.
                var booked = matches.Select(e => FromGraphEvent(sheet, e)).ToList();
                return booked.FirstOrDefault(b => b.State == BookingState.Confirmed) ?? booked[0];
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
    /// <paramref name="groupId"/> is set explicitly by the caller (BreelyBookingProcessor) rather
    /// than minted here, so multiple sheets claimed from the same Breely submission - or a sheet
    /// reclaimed on reschedule - can share (or keep) one BookingGroupId instead of each claim getting
    /// its own random one.
    /// </summary>
    public async Task<SheetBooking?> ClaimHoldAsync(DateTime start, DateTime end, SheetBooking template, Guid groupId, CancellationToken ct = default)
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
                    BookingGroupId = groupId
                };

                var graphEvent = ToGraphEvent(booking);
                var created = await graph.CreateEventAsync(sheet, graphEvent, ct);
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
        var minInterval = TimeSpan.FromMinutes(_minimumGroupEventBookingIntervalMinutes);

        // A leftover fragment shorter than the configured minimum (Settings page) is dropped rather
        // than offered as its own bookable slot - nobody can realistically use a 5-minute sliver of
        // ice. Filtering here means every branch below (delete-if-none/patch-if-one/split-if-two)
        // just works on whatever's left, unchanged.
        var remainders = CalendarStyles.SubtractIntervals(hold.Start, hold.End, [(claimedStart, claimedEnd)])
            .Where(r => r.End - r.Start >= minInterval)
            .ToList();

        if (hold.SeriesMasterId is not null)
        {
            // Delete this occurrence and create standalone events for whatever remains, rather than
            // trying to PATCH it in place - Graph rejects a time-bearing PATCH on a recurring
            // occurrence with "Modified occurrence is crossing or overlapping adjacent occurrence"
            // even when the resulting time doesn't actually conflict with anything.
            if (hold.EventId is not null)
            {
                await graph.DeleteEventAsync(sheet, hold.EventId, ct);
            }

            foreach (var (segStart, segEnd) in remainders)
            {
                var remainderHold = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = segStart,
                    End = segEnd,
                    Category = BookingCategory.GroupEvent,
                    State = BookingState.Hold,
                    BookingGroupId = Guid.NewGuid()
                };
                await graph.CreateEventAsync(sheet, ToGraphEvent(remainderHold, titleOverride: AvailableForGroupEventsTitle), ct);
            }

            return;
        }

        if (remainders.Count == 0)
        {
            if (hold.EventId is not null)
            {
                await graph.DeleteEventAsync(sheet, hold.EventId, ct);
            }
            return;
        }

        // One remainder: patch the existing hold in place to the shrunken window.
        var (firstStart, firstEnd) = remainders[0];
        var patch = new Event
        {
            Subject = AvailableForGroupEventsTitle,
            Start = new DateTimeTimeZone { DateTime = firstStart.ToString("s"), TimeZone = facility.TimeZone },
            End = new DateTimeTimeZone { DateTime = firstEnd.ToString("s"), TimeZone = facility.TimeZone }
        };
        await graph.PatchEventAsync(sheet, hold.EventId!, patch, ct);

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
        await graph.CreateEventAsync(sheet, ToGraphEvent(secondHold, titleOverride: AvailableForGroupEventsTitle), ct);
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
            var created = await graph.CreateEventAsync(sheetMailbox, graphEvent, ct);
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
        var refreshed = await graph.GetEventAsync(sheetMailbox, eventId, ExtendedPropertiesExpand, ct);

        return FromGraphEvent(sheetMailbox, refreshed!);
    }

    // Graph's calendarView is boundary-inclusive: an event ending exactly at `start` (or starting
    // exactly at `end`) still comes back in the view even though it doesn't actually overlap
    // [start, end) - live-found 2026-08-26, a 1pm-6pm booking request was rejected as conflicting
    // with a pre-existing 9am-1pm event. Re-checked here with a strict overlap test so every caller
    // (conflict checks, adjacency lookups, display reads) sees only events that genuinely overlap.
    private async Task<List<Event>> GetEventsInRangeAsync(string sheetMailbox, DateTime start, DateTime end, CancellationToken ct)
    {
        var events = await graph.GetCalendarViewAsync(sheetMailbox, facility.ToUtcQueryString(start), facility.ToUtcQueryString(end), ExtendedPropertiesExpand, ct: ct);
        return events.Where(e =>
            facility.FromUtcResponseString(e.Start?.DateTime ?? DateTime.UtcNow.ToString("o")) < end &&
            facility.FromUtcResponseString(e.End?.DateTime ?? DateTime.UtcNow.ToString("o")) > start).ToList();
    }

    /// <summary>
    /// Best-effort compensation for a multi-sheet write that failed partway through: deletes the
    /// events already created so the caller's "this failed" is actually true on the calendar too.
    /// Runs on <see cref="CancellationToken.None"/> deliberately - if the original failure was a
    /// cancelled request, the already-written events still need removing, and inheriting the
    /// cancelled token would skip exactly the cleanup that matters. A delete that itself fails is
    /// logged at Standard tier rather than thrown: the caller is already receiving the original
    /// exception, and a leftover event a human has to remove is worth a visible log line.
    /// </summary>
    private async Task RollbackCreatedAsync(List<SheetBooking> created, string actingUser, Exception cause, CancellationToken ct)
    {
        if (created.Count == 0)
        {
            return;
        }

        var orphaned = new List<string>();
        foreach (var booking in created)
        {
            if (booking.EventId is null)
            {
                continue;
            }

            try
            {
                await graph.DeleteEventAsync(booking.SheetMailbox, booking.EventId, CancellationToken.None);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                // Already gone - nothing to undo.
            }
            catch (Exception)
            {
                orphaned.Add($"{booking.SheetMailbox}:{booking.EventId}");
            }
        }

        InvalidateViewCache();

        var detail = $"Rolled back {created.Count} sheet(s) after: {cause.Message}"
            + (orphaned.Count > 0 ? $". MANUAL CLEANUP REQUIRED, could not delete: {string.Join(",", orphaned)}" : "");
        await log.LogActionAsync("MultiSheetCreateRolledBack", actingUser, details: detail, ct: ct);
    }

    private Event ToGraphEvent(SheetBooking booking, bool includeTime = true, string? titleOverride = null, bool clearUnsetOptionalProperties = false)
    {
        var subject = titleOverride ?? (string.IsNullOrWhiteSpace(booking.RenterName)
            ? CalendarStyles.CategoryLabel(booking.Category)
            : $"{CalendarStyles.CategoryLabel(booking.Category)} - {booking.RenterName}");

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

        // A PATCH only upserts the extended properties it includes - one omitted from the request
        // keeps whatever value it already had on Graph, it is NOT cleared. clearUnsetOptionalProperties
        // (used when reopening a booking as a hold, CancelGroupAsync) forces BookedBy/ExternalBookingId
        // to an explicit empty string when the booking doesn't carry its own value, rather than
        // silently leaving the previous occupant's identity in place. Live-found 2026-08-04: a
        // reopened hold kept the ExternalBookingId of the Breely booking that had just been released
        // from it, so the next notification for that same external id could match the leftover hold
        // instead of the real (now-elsewhere) booking - FindByExternalIdAsync has no way to tell them
        // apart once both carry the same value.
        if (!string.IsNullOrWhiteSpace(booking.BookedBy))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = BookedByPropertyId, Value = booking.BookedBy });
        }
        else if (clearUnsetOptionalProperties)
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = BookedByPropertyId, Value = "" });
        }

        if (!string.IsNullOrWhiteSpace(booking.ExternalBookingId))
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = ExternalIdPropertyId, Value = booking.ExternalBookingId });
        }
        else if (clearUnsetOptionalProperties)
        {
            extendedProps.Add(new SingleValueLegacyExtendedProperty { Id = ExternalIdPropertyId, Value = "" });
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

    // Sentinel SheetMailbox, matching the class of trick ClosureConflict already uses for a
    // non-sheet blocker shown through the same conflict-rendering UI as a real double-booking - but
    // distinct from ClosureConflict's own "" sentinel, since that one renders hardcoded "closes all
    // sheets" wording in EventFormModal that would be wrong here.
    private const string SeasonConflictSheetMailbox = "__season__";

    private static SheetBooking OutOfSeasonConflict(DateTime start, DateTime end, DateTime? seasonStart, DateTime? seasonEnd) => new()
    {
        SheetMailbox = SeasonConflictSheetMailbox,
        Start = start,
        End = end,
        Category = BookingCategory.Other,
        State = BookingState.Confirmed,
        RenterName = (seasonStart, seasonEnd) switch
        {
            ({ } s, { } e) => $"Outside the booking season ({s:MMM d, yyyy} – {e:MMM d, yyyy})",
            ({ } s, null) => $"Before the booking season starts ({s:MMM d, yyyy})",
            (null, { } e) => $"After the booking season ends ({e:MMM d, yyyy})",
            _ => "Outside the booking season",
        },
    };

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
