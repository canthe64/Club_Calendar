using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using FacilityScheduler.Domain;
using Microsoft.Extensions.Logging;

namespace FacilityScheduler.Services;

/// <summary>
/// Processes a Breely webhook notification. Breely fires **one webhook call for an entire multi-sheet
/// reservation at creation** (the sibling sheet-events are only discoverable via the nested
/// "submission.events" array - the top-level "event" alone only names one of them), but **one call
/// per event for reschedule or cancellation** later, since Breely's own UI requires rescheduling a
/// multi-sheet reservation's sheets one at a time (live-confirmed 2026-08-03, after the original
/// "one call per sheet, always" assumption turned out to only hold for reschedule/cancel, not
/// creation). Each event's own `id` is stable across reschedules and is this app's only handle on
/// "have I seen this external booking before," since there is no companion database (architecture
/// doc D7).
///
/// This is a "dumb webhook" in the sense the booking already happened in the real world by the
/// time this fires - the job here is to reflect that, never to reject it. See the architecture doc
/// for the fuller design rationale (fail-open, never drop a real booking, hold-claiming instead of
/// hold-blocking, NeedsTriage markers instead of silent best-effort guesses).
/// </summary>
public class BreelyBookingProcessor(SheetBookingService bookingService, ClubEventService clubEventService, FacilityConfiguration facility, AppLogService appLog, ILogger<BreelyBookingProcessor> logger)
{
    // The Breely resource-type name for a physical sheet, as it currently appears in the "booked_with"
    // field. Update here if the club renames the resource in Breely - this app has no way to learn
    // that on its own since there's no shared config between the two systems.
    private const string SheetResourceType = "Curling Sheet";
    private const string ExternalIdSourcePrefix = "breely";

    /// <summary>
    /// D119 (operator-supplied, 2026-09-14): how many physical sheets a "Group Reservation" event type
    /// actually needs, keyed by its `event_type` label. Staff-maintained, not derived - `event_type` is
    /// an operator-controlled value defined and named in Breely's own admin panel (unlike everything
    /// else in the payload, which Breely itself generates), so this table only drifts out of sync when
    /// the club adds, renames, or retires an event type there; update it then. Case-insensitive
    /// (`GroupReservationSheetCounts`'s own comparer) since nothing guarantees the club will retype a
    /// label with exactly the same casing every time it's edited in Breely.
    /// </summary>
    private static readonly Dictionary<string, int> GroupReservationSheetCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Up to 8 participants"] = 1,
        ["9-16 participants"] = 2,
        ["17-24 participants"] = 3,
        ["25-32 participants"] = 4,
        ["33-40 participants"] = 5,
    };

    // The two "Extended session" labels carry extra free text after the hour count (unspecified by the
    // operator, presumably a date/time-specific suffix) - matched by prefix rather than the exact-
    // equality the fixed participant-count labels above use, since those are short, closed-vocabulary
    // strings with no reason to vary sentence-to-sentence the way a suffixed label might.
    private const string ExtendedSession3HourPrefix = "Extended session - 3-Hours";
    private const string ExtendedSession4HourPrefix = "Extended session - 4-Hours";
    private const int ExtendedSessionSheetCount = 5;

    // Every synthetic sheet ExpandForGroupReservation derives gets Breely's real event id plus this
    // offset times its 1-based position among the *extra* sheets (2nd sheet = +1x, 3rd = +2x, etc. -
    // the 1st/primary sheet keeps its real, unmodified id). A billion is comfortably past any id Breely
    // itself is ever likely to assign (observed ids are low six digits), so a synthetic id can never
    // collide with a genuine future Breely event id, and the real id stays legible at the low end of
    // the synthetic one for anyone reading a log line.
    private const long SyntheticSheetIdOffset = 1_000_000_000L;
    // Internal, not private: PublicAvailabilityService's Notes-exposure gate needs to recognize a
    // machine-authored ClubEvent (the NeedsTriage marker below, FlagNeedsTriageAsync) the same way it
    // recognizes a machine-authored SheetBooking via ExternalBookingId - ClubEvent has no such field,
    // so BookedBy is the only reliable "no staff reviewed this" signal available for that type. Never
    // staff-settable through the UI (ClubEventDraft.ToClubEvent always writes the signed-in user's own
    // name for a new event), so referencing this one constant rather than a second copy of the
    // literal string is what keeps the two checks from silently drifting apart.
    internal const string BookedByLabel = "Breely webhook";

    // Guards against two concurrent webhook deliveries for the same external id (Breely has been
    // observed re-sending the same creation notification twice within minutes) racing through
    // FindByExternalIdAsync before either has claimed anything - without this, both could see "no
    // existing booking" and independently claim two different sheets for what's really one booking.
    // Per-sheet locks in SheetBookingService don't cover this, since the race is in the lookup that
    // happens *before* either request picks a sheet to lock.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ExternalIdLocks = new();

    /// <summary>
    /// Entry point for the endpoint - resolves which event(s) this webhook call is actually about,
    /// then processes each independently. Breely's payload always carries a top-level "event" (the
    /// one this specific call is about) plus a "submission.events" array - at the *original*
    /// multi-sheet creation call, that array lists every sibling sheet-event together, which is the
    /// only way this app can discover them at all (Breely fires one webhook per creation regardless
    /// of sheet count, but individual reschedule/cancel notifications later, one call per event -
    /// live-confirmed 2026-08-03). On those later calls the array is a stale snapshot of the original
    /// submission, not a fresh batch - so every id it names is still resolved and reconciled here
    /// (in case a sibling was never individually claimed), but the top-level "event" object's own
    /// data always wins for its own id, since that's the one actually being updated by this call.
    ///
    /// **Group Reservation expansion (D119, live-found 2026-09-07)**: some event types book against a
    /// single Breely-side resource regardless of how many physical sheets the reservation actually
    /// needs - "Try Curling Weekday Group Reservation" and similar, where `event_type` is a
    /// participant-count label ("25-32 Participants") rather than a per-sheet event. Breely never
    /// sends sibling ids for these, so every resolved event (from either source above) is expanded via
    /// <see cref="ExpandForGroupReservation"/> before being added to <c>eventsById</c> - a no-op for an
    /// ordinary single-sheet event, or for a genuine `submission.events[]` sibling (the original
    /// multi-sheet flow already gives those their own real ids and needs no synthetic ones).
    ///
    /// **Known gap, accepted rather than solved here:** if a reschedule notification's `event_type`
    /// maps to *fewer* sheets than the original booking claimed (the group genuinely shrank), the
    /// sheets no longer named simply stop being expanded - nothing releases them, since there's no
    /// record anywhere of "this reservation previously needed N sheets" to compare against. They'd sit
    /// claimed until a staff member notices and cancels them manually. Growing (more sheets than
    /// before) is handled correctly - the newly-expanded ids are simply never-before-seen and get
    /// claimed like any other. Accepted for now as a rare edge case (staff feedback, 2026-09-14) rather
    /// than adding a second persisted "expected sheet count" concept to track and reconcile against.
    /// </summary>
    public async Task ProcessAsync(BreelyWebhookPayload payload, CancellationToken ct = default)
    {
        var eventsById = new Dictionary<long, BreelyEvent>();
        // Every id this call resolves as authoritative for its own external id - i.e. allowed to
        // cancel/reschedule an existing booking, not just claim a never-before-seen one (see
        // ProcessEventAsync's isPrimary doc). The top-level "event" always qualifies, same as before
        // D119; so now does every synthetic sheet ExpandForGroupReservation derives FROM it, since
        // Breely will only ever notify this app about the one real id it actually knows - a cancel or
        // reschedule of that id has to propagate to every sheet this app itself inferred from it, not
        // just the first one. A genuine submission.events[] sibling that ISN'T the primary keeps the
        // original, stricter behavior (never mutates an existing booking from possibly-stale data).
        var authoritativeIds = new HashSet<long>();

        if (payload.Submission?.Events is { Count: > 0 } siblings)
        {
            foreach (var sibling in siblings)
            {
                foreach (var expanded in ExpandForGroupReservation(sibling))
                {
                    eventsById[expanded.Id] = expanded;
                }
            }
        }
        if (payload.Event is { } primary)
        {
            foreach (var expanded in ExpandForGroupReservation(primary))
            {
                eventsById[expanded.Id] = expanded; // freshest data for its own id - overrides any stale copy from the array above
                authoritativeIds.Add(expanded.Id);
            }
        }

        if (eventsById.Count == 0)
        {
            logger.LogWarning("Breely webhook: request had no top-level \"event\" object and no \"submission.events\" array.");
            return;
        }

        // Resolved once per id and reused below for both the shared-group-id decision and each
        // event's own processing - halves the Graph round-trips a multi-sheet batch needs (each id
        // was previously looked up twice), which matters now that processing runs detached from the
        // request (see the endpoint) but still has real wall-clock cost per Graph call.
        var existingById = new Dictionary<long, SheetBooking?>();
        foreach (var id in eventsById.Keys)
        {
            existingById[id] = await bookingService.FindByExternalIdAsync($"{ExternalIdSourcePrefix}:{id}", ct);
        }

        // One shared BookingGroupId for the whole batch - reuse an existing sibling's group id if
        // any of these ids was already claimed before (so a straggler joins its group correctly,
        // and a reschedule keeps the booking in its original group instead of forking into a new
        // one), otherwise mint a fresh one for a genuinely new submission.
        var sharedGroupId = existingById.Values
            .Where(e => e is { BookingGroupId: var gid } && gid != Guid.Empty)
            .Select(e => e!.BookingGroupId)
            .FirstOrDefault(Guid.NewGuid());

        if (eventsById.Count > 1)
        {
            await appLog.LogDebugAsync("WebhookMultiSheetBatch", BookedByLabel,
                details: $"{eventsById.Count} sibling event(s) resolved for this submission: {string.Join(",", eventsById.Keys)}.", ct: ct);
        }

        var batchIndex = 0;
        foreach (var (id, evt) in eventsById)
        {
            try
            {
                // batchIndex offsets which sheet a force-book fallback lands on (see
                // ProcessEventAsync) - so siblings that all fail to match a hold in the same batch
                // spread across sheets instead of stacking three overlapping bookings on sheet 1.
                await ProcessEventAsync(evt, existingById[id], isPrimary: authoritativeIds.Contains(id), sharedGroupId, batchIndex, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Breely webhook: failed to process event {Id}", evt.Id);
            }
            batchIndex++;
        }
    }

    /// <summary>
    /// Yields <paramref name="evt"/> itself first, unchanged - the sheet Breely actually told this app
    /// about, same id and same external-id scheme as always - then, only if its `event_type` maps to
    /// more than one sheet (<see cref="SheetCountForEventType"/>), one synthetic clone per additional
    /// sheet (D119). Every other event type (including a genuine `submission.events[]` sibling from the
    /// original multi-sheet flow, which already has its own real id) yields just the one unchanged
    /// event - this is a no-op for the vast majority of calls.
    /// </summary>
    private static IEnumerable<BreelyEvent> ExpandForGroupReservation(BreelyEvent evt)
    {
        yield return evt;

        var sheetCount = SheetCountForEventType(evt.EventType);
        if (sheetCount is not > 1)
        {
            yield break;
        }

        for (var extraSheetNumber = 2; extraSheetNumber <= sheetCount; extraSheetNumber++)
        {
            yield return new BreelyEvent
            {
                Id = evt.Id + (extraSheetNumber - 1) * SyntheticSheetIdOffset,
                StartDate = evt.StartDate,
                StartTime = evt.StartTime,
                DurationInMinutes = evt.DurationInMinutes,
                BookedWith = evt.BookedWith,
                Canceled = evt.Canceled,
                ClientFullName = evt.ClientFullName,
                ClientEmail = evt.ClientEmail,
                ClientPhone = evt.ClientPhone,
                EventType = evt.EventType,
                AdminUrl = evt.AdminUrl
            };
        }
    }

    /// <summary>Looks up how many sheets a Group Reservation event type needs from
    /// <see cref="GroupReservationSheetCounts"/> (exact match) or the "Extended session" prefixes
    /// (D119) - null for anything else, including a blank/missing label, which
    /// <see cref="ExpandForGroupReservation"/> then treats as an ordinary single-sheet event exactly
    /// as before this feature existed.</summary>
    internal static int? SheetCountForEventType(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return null;
        }

        var trimmed = eventType.Trim();
        if (GroupReservationSheetCounts.TryGetValue(trimmed, out var count))
        {
            return count;
        }

        if (trimmed.StartsWith(ExtendedSession3HourPrefix, StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(ExtendedSession4HourPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ExtendedSessionSheetCount;
        }

        return null;
    }

    /// <summary>
    /// Non-null only when <paramref name="isPrimary"/> and <paramref name="evt"/>'s `event_type` is a
    /// non-blank label <see cref="SheetCountForEventType"/> didn't recognize (D120, operator request,
    /// 2026-09-14) - the booking still claims exactly 1 sheet either way (unchanged from before D119),
    /// but this also gets it flagged for a human to check whether the label should be added to
    /// <see cref="GroupReservationSheetCounts"/>. A blank/missing `event_type` is left alone - most
    /// Breely bookings never carry this field at all, and flagging every one of those would drown out
    /// the signal this exists to catch. Scoped to <paramref name="isPrimary"/> only - a genuine
    /// `submission.events[]` sibling's own `event_type` isn't re-checked here, so an already-working
    /// ordinary multi-sheet booking doesn't get flagged once per sheet for a label this app was never
    /// meant to recognize in the first place.
    /// </summary>
    private static string? UnrecognizedEventTypeReason(BreelyEvent evt, bool isPrimary) =>
        isPrimary && !string.IsNullOrWhiteSpace(evt.EventType) && SheetCountForEventType(evt.EventType) is null
            ? $"event_type \"{evt.EventType}\" isn't a recognized Group Reservation label - defaulted to 1 sheet. Verify whether this should have claimed more, and update GroupReservationSheetCounts if so."
            : null;

    /// <summary>
    /// <paramref name="existing"/> is resolved once by the caller, not looked up again here.
    /// <paramref name="isPrimary"/> is true for the top-level "event" this specific webhook call is
    /// actually about, and for every synthetic Group Reservation sheet derived from it (D119) - false
    /// only for a genuine sibling resolved purely from "submission.events[]", which (per ProcessAsync's
    /// doc comment) can be a stale snapshot from the original creation call. An already-claimed
    /// non-authoritative sibling is never mutated (cancelled or re-timed) from that possibly-stale
    /// data; only a never-before-seen one is claimed from it. A Group Reservation's synthetic sheets
    /// are always authoritative because Breely will never separately notify this app about them - the
    /// one real id's own cancel/reschedule is the only signal they'll ever get.
    /// </summary>
    private async Task ProcessEventAsync(BreelyEvent evt, SheetBooking? existing, bool isPrimary, Guid groupId, int batchIndex, CancellationToken ct)
    {
        var breelyId = evt.Id.ToString(CultureInfo.InvariantCulture);

        // Debug-tier only, and with customer contact fields redacted even then - this line exists
        // to see exactly what Breely sent while troubleshooting a payload-shape question, not to
        // keep a second at-rest copy of customer PII outside Graph for the whole retention window.
        await appLog.LogDebugAsync("WebhookPayloadReceived", BookedByLabel, breelyId, details: RedactedSummary(evt), ct: ct);

        if (!string.Equals(evt.BookedWith, SheetResourceType, StringComparison.OrdinalIgnoreCase))
        {
            // Not a sheet at all (e.g. a warm room table, if Breely ever sends those as their own
            // top-level event rather than as an add-on question) - nothing for this app to do.
            logger.LogInformation("Breely webhook for event {Id}: booked_with={BookedWith}, not a sheet - ignored.", evt.Id, evt.BookedWith);
            await appLog.LogDebugAsync("WebhookIgnoredNotASheet", BookedByLabel, breelyId, details: $"booked_with={evt.BookedWith}", ct: ct);
            return;
        }

        var externalId = $"{ExternalIdSourcePrefix}:{evt.Id}";

        if (!TryParseWindow(evt, out var start, out var end))
        {
            logger.LogWarning("Breely webhook for event {Id}: could not parse start_date/start_time/duration_in_minutes ({StartDate} {StartTime} {Duration}min) - skipped.",
                evt.Id, evt.StartDate, evt.StartTime, evt.DurationInMinutes);
            await appLog.LogDebugAsync("WebhookUnparseableWindow", BookedByLabel, breelyId,
                details: $"start_date={evt.StartDate} start_time={evt.StartTime} duration_in_minutes={evt.DurationInMinutes}", ct: ct);
            return;
        }

        var sem = ExternalIdLocks.GetOrAdd(externalId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check fresh under the lock - the caller's pre-fetch (existingById in ProcessAsync)
            // is only a hint for the shared-group-id decision; a concurrent duplicate delivery for
            // this exact external id could have claimed or changed it between that pre-fetch and now.
            existing = await bookingService.FindByExternalIdAsync(externalId, ct);

            await appLog.LogDebugAsync("WebhookExternalIdLookup", BookedByLabel, breelyId, existing?.SheetMailbox,
                details: existing is null ? "no existing booking found" : $"found existing eventId={existing.EventId}, {existing.Start:g}-{existing.End:g}", ct: ct);

            if (existing is not null && !isPrimary)
            {
                await appLog.LogDebugAsync("WebhookSiblingAlreadyClaimed", BookedByLabel, breelyId, existing.SheetMailbox,
                    "Resolved only from submission.events[] (possibly stale) and already claimed - not modified from this data.", ct);
                return;
            }

            if (evt.Canceled)
            {
                if (existing is not null)
                {
                    await bookingService.CancelGroupAsync([existing], reopenAsGroupEventHold: true, BookedByLabel, ct);
                    logger.LogInformation("Breely webhook: event {Id} canceled - released sheet {Sheet}.", evt.Id, existing.SheetMailbox);
                    await appLog.LogActionAsync("BreelyBookingCancelled", BookedByLabel, existing.EventId, existing.SheetMailbox, $"Breely event {breelyId}.", ct);
                }
                else
                {
                    logger.LogInformation("Breely webhook: event {Id} canceled, but no matching booking was found - nothing to release.", evt.Id);
                    await appLog.LogDebugAsync("WebhookCancelNoMatch", BookedByLabel, breelyId, ct: ct);
                }
                return;
            }

            if (existing is not null)
            {
                if (existing.Start == start && existing.End == end)
                {
                    await appLog.LogDebugAsync("WebhookDuplicateIgnored", BookedByLabel, breelyId, existing.SheetMailbox, "Already correct - retry or duplicate notification.", ct);
                    return; // retry or duplicate notification - already correct, nothing to do
                }

                // Reschedule: release the old slot back to an open hold, then claim fresh at the new
                // time below (possibly landing on a different sheet than before, if the original one
                // isn't free at the new time - that's expected and fine).
                await bookingService.CancelGroupAsync([existing], reopenAsGroupEventHold: true, BookedByLabel, ct);
                logger.LogInformation("Breely webhook: event {Id} rescheduled from {OldStart} to {NewStart} - released old sheet {Sheet}, claiming new slot.",
                    evt.Id, existing.Start, start, existing.SheetMailbox);
                await appLog.LogActionAsync("BreelyBookingReleased", BookedByLabel, existing.EventId, existing.SheetMailbox,
                    $"Rescheduling Breely event {breelyId}: {existing.Start:g}-{existing.End:g} -> {start:g}-{end:g}.", ct);
            }

            var template = new SheetBooking
            {
                SheetMailbox = "",
                Start = start,
                End = end,
                Category = BookingCategory.GroupEvent,
                State = BookingState.Confirmed,
                RenterName = string.IsNullOrWhiteSpace(evt.ClientFullName) ? "Breely booking" : evt.ClientFullName,
                RenterPhone = evt.ClientPhone,
                RenterEmail = evt.ClientEmail,
                Notes = BuildNotes(evt),
                BookedBy = BookedByLabel,
                ExternalBookingId = externalId,
                BookingGroupId = groupId
            };

            var unrecognizedEventTypeReason = UnrecognizedEventTypeReason(evt, isPrimary);

            var claimed = await bookingService.ClaimHoldAsync(start, end, template, groupId, ct);
            if (claimed is not null)
            {
                logger.LogInformation("Breely webhook: event {Id} claimed hold on {Sheet} for {Start}-{End}.", evt.Id, claimed.SheetMailbox, start, end);
                await appLog.LogActionAsync("BreelyBookingClaimed", BookedByLabel, claimed.EventId, claimed.SheetMailbox, $"Breely event {breelyId}, {start:g}-{end:g}.", ct);

                if (unrecognizedEventTypeReason is not null)
                {
                    await FlagNeedsTriageAsync(start,
                        $"Breely booking {evt.Id} ({template.RenterName}, {start:h:mmtt}-{end:h:mmtt}) claimed {DisplaySheetLabel(claimed.SheetMailbox)} normally, but its {unrecognizedEventTypeReason} Admin: {evt.AdminUrl}",
                        ct);
                }
                return;
            }

            // No sheet had an open hold covering this window - write it anyway (a real booking is
            // never dropped, per the standing design), and flag it for staff instead of guessing
            // further. Offset by this event's position within the batch (0 for a standalone
            // notification) so siblings that all fail to match a hold in one multi-sheet submission
            // land on different sheets rather than stacking multiple overlapping force-bookings onto
            // sheet 1 - found live 2026-08-04 alongside the multi-sheet fix (§4.8).
            await appLog.LogDebugAsync("WebhookNoCoveringHold", BookedByLabel, breelyId, details: $"{start:g}-{end:g} - no sheet had a hold covering this window.", ct: ct);
            var fallbackSheet = facility.SheetMailboxes[batchIndex % facility.SheetMailboxes.Length];
            var forceBooked = await bookingService.ForceCreateConfirmedAsync(fallbackSheet, template, ct);
            logger.LogWarning("Breely webhook: event {Id} didn't match any open hold - force-booked onto {Sheet}.", evt.Id, fallbackSheet);
            await appLog.LogActionAsync("BreelyBookingForceBooked", BookedByLabel, forceBooked.EventId, fallbackSheet,
                $"Breely event {breelyId} matched no open hold - force-booked, flagged for review.", ct);

            // One combined marker, not two, when both conditions apply - a single booking getting two
            // separate "needs review" markers for what's really one investigation is worse than one
            // marker naming both reasons.
            var noHoldReason = $"didn't match any open hold on any sheet - booked directly onto {DisplaySheetLabel(fallbackSheet)}. Verify manually and reassign if needed.";
            var combinedReason = unrecognizedEventTypeReason is null
                ? noHoldReason
                : $"{noHoldReason} Also, its {unrecognizedEventTypeReason}";
            await FlagNeedsTriageAsync(start,
                $"Breely booking {evt.Id} ({template.RenterName}, {start:h:mmtt}-{end:h:mmtt}) {combinedReason} Admin: {evt.AdminUrl}",
                ct);
        }
        finally
        {
            sem.Release();
        }
    }

    // Debug-tier payload logging - everything Breely sent except the fields that identify a
    // specific customer (name/email/phone), so the log stays useful for troubleshooting without
    // becoming a second at-rest store of customer PII outside Exchange.
    private static string RedactedSummary(BreelyEvent evt) =>
        $"start_date={evt.StartDate} start_time={evt.StartTime} duration_in_minutes={evt.DurationInMinutes} " +
        $"booked_with={evt.BookedWith} canceled={evt.Canceled} event_type={evt.EventType} admin_url={evt.AdminUrl} " +
        "client_full_name=[redacted] client_email=[redacted] client_phone=[redacted]";

    private async Task FlagNeedsTriageAsync(DateTime date, string reason, CancellationToken ct)
    {
        try
        {
            var marker = new ClubEvent
            {
                Title = "⚠ Web booking needs review",
                Category = ClubEventCategory.Other,
                Start = date.Date,
                End = date.Date,
                IsAllDay = true,
                MarksSheetsUnavailable = false,
                Notes = reason,
                BookedBy = BookedByLabel
            };
            await clubEventService.CreateAsync(marker, BookedByLabel, ct);
        }
        catch (Exception ex)
        {
            // Best-effort - the booking itself is already written; failing to also flag it for
            // triage shouldn't be treated as a processing failure in its own right.
            logger.LogError(ex, "Breely webhook: failed to create a NeedsTriage marker for {Date}: {Reason}", date, reason);
        }
    }

    private static string BuildNotes(BreelyEvent evt) =>
        $"Booked via Breely ({evt.EventType ?? "Try Curling Group Reservation"}). Admin: {evt.AdminUrl}";

    private static string DisplaySheetLabel(string sheetMailbox)
    {
        var localPart = sheetMailbox.Split('@')[0];
        var digits = new string(localPart.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? $"Sheet {digits}" : localPart;
    }

    // Breely sends "start_date":"Sep 25, 2026" and "start_time":"9:00am" as separate fields (rather
    // than the human "start_date_&_time" string, which has an unstable property name and a day-name
    // prefix not worth stripping) plus "duration_in_minutes" - combined and parsed as facility-local
    // time, matching how the rest of this app already treats DateTime as local-without-offset. The
    // "PDT"/"PST" abbreviation Breely also sends is deliberately ignored rather than mapped, since
    // the facility's own configured time zone is already the authority on local time here.
    private static bool TryParseWindow(BreelyEvent evt, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;

        if (string.IsNullOrWhiteSpace(evt.StartDate) || string.IsNullOrWhiteSpace(evt.StartTime) || evt.DurationInMinutes <= 0)
        {
            return false;
        }

        var combined = $"{evt.StartDate} {evt.StartTime}";
        if (!DateTime.TryParseExact(combined, "MMM d, yyyy h:mmtt", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
        {
            return false;
        }

        end = start.AddMinutes(evt.DurationInMinutes);
        return end > start;
    }
}

/// <summary>
/// The subset of Breely's webhook "event" object this app actually uses - everything else in the
/// real payload (CRM/marketing fields, signed-PDF blobs, raw form-answer dumps, etc.) is
/// deliberately left unmapped; System.Text.Json ignores JSON properties with no matching member.
/// </summary>
public class BreelyEvent
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }

    [JsonPropertyName("start_time")]
    public string? StartTime { get; set; }

    [JsonPropertyName("duration_in_minutes")]
    public int DurationInMinutes { get; set; }

    [JsonPropertyName("booked_with")]
    public string? BookedWith { get; set; }

    [JsonPropertyName("canceled")]
    public bool Canceled { get; set; }

    [JsonPropertyName("client_full_name")]
    public string? ClientFullName { get; set; }

    [JsonPropertyName("client_email")]
    public string? ClientEmail { get; set; }

    [JsonPropertyName("client_phone")]
    public string? ClientPhone { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("admin_url")]
    public string? AdminUrl { get; set; }
}

public class BreelyWebhookPayload
{
    [JsonPropertyName("event")]
    public BreelyEvent? Event { get; set; }

    [JsonPropertyName("submission")]
    public BreelySubmission? Submission { get; set; }
}

/// <summary>
/// Wraps the "submission" object's "events" array - the only place a multi-sheet reservation's
/// sibling event ids appear together in one payload (live-confirmed 2026-08-03). Everything else in
/// "submission" (form answers, client CRM fields, signed-PDF blobs) is deliberately left unmapped,
/// same reasoning as BreelyEvent.
/// </summary>
public class BreelySubmission
{
    [JsonPropertyName("events")]
    public List<BreelyEvent>? Events { get; set; }
}
