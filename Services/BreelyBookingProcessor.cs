using System.Globalization;
using System.Text.Json.Serialization;
using FacilityScheduler.Domain;
using Microsoft.Extensions.Logging;

namespace FacilityScheduler.Services;

/// <summary>
/// Processes a single Breely webhook notification - the real integration that replaced
/// WebhookCaptureEndpoint once the payload shape was known empirically (a series of real
/// create/reschedule/single-sheet samples, since Breely's own documentation for this webhook was
/// too sparse to build against directly). Breely fires one webhook per sheet-booking event, not one
/// per purchase - a multi-sheet reservation is several independent calls, each with its own stable
/// `event.id` that persists across reschedules. That id is this app's only handle on "have I seen
/// this external booking before," since there is no companion database (architecture doc D7).
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
    private const string BookedByLabel = "Breely webhook";

    public async Task ProcessAsync(BreelyEvent evt, CancellationToken ct = default)
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

        var existing = await bookingService.FindByExternalIdAsync(externalId, ct);
        await appLog.LogDebugAsync("WebhookExternalIdLookup", BookedByLabel, breelyId, existing?.SheetMailbox,
            details: existing is null ? "no existing booking found" : $"found existing eventId={existing.EventId}, {existing.Start:g}-{existing.End:g}", ct: ct);

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
            ExternalBookingId = externalId
        };

        var claimed = await bookingService.ClaimHoldAsync(start, end, template, ct);
        if (claimed is not null)
        {
            logger.LogInformation("Breely webhook: event {Id} claimed hold on {Sheet} for {Start}-{End}.", evt.Id, claimed.SheetMailbox, start, end);
            await appLog.LogActionAsync("BreelyBookingClaimed", BookedByLabel, claimed.EventId, claimed.SheetMailbox, $"Breely event {breelyId}, {start:g}-{end:g}.", ct);
            return;
        }

        // No sheet had an open hold covering this window - write it anyway (a real booking is never
        // dropped, per the standing design) onto the first sheet in configured order, and flag it
        // for staff instead of guessing further.
        await appLog.LogDebugAsync("WebhookNoCoveringHold", BookedByLabel, breelyId, details: $"{start:g}-{end:g} - no sheet had a hold covering this window.", ct: ct);
        var fallbackSheet = facility.SheetMailboxes[0];
        var forceBooked = await bookingService.ForceCreateConfirmedAsync(fallbackSheet, template, ct);
        logger.LogWarning("Breely webhook: event {Id} didn't match any open hold - force-booked onto {Sheet}.", evt.Id, fallbackSheet);
        await appLog.LogActionAsync("BreelyBookingForceBooked", BookedByLabel, forceBooked.EventId, fallbackSheet,
            $"Breely event {breelyId} matched no open hold - force-booked, flagged for review.", ct);

        await FlagNeedsTriageAsync(start,
            $"Breely booking {evt.Id} ({template.RenterName}, {start:h:mmtt}-{end:h:mmtt}) didn't match any open hold on any sheet - booked directly onto {DisplaySheetLabel(fallbackSheet)}. Verify manually and reassign if needed. Admin: {evt.AdminUrl}",
            ct);
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
}
