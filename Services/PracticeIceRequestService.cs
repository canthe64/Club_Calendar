using FacilityScheduler.Domain;
using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Services;

/// <summary>
/// The practice ice write path: submit, approve, decline, and the pending-request queue - kept
/// separate from PublicAvailabilityService (read-only) the same way SheetBookingService is kept
/// separate from it today. Every validation here re-checks server-side rather than trusting a
/// caller-supplied value, since the request page's inputs (a query-string start time, a duration
/// dropdown) are untrusted and can be stale by the time a request actually arrives
/// (docs/practice-ice-hosting-design.md §3.8).
/// </summary>
public class PracticeIceRequestService(
    SheetBookingService bookingService,
    PublicAvailabilityService availability,
    IGraphMailGateway mail,
    FacilityConfiguration facility,
    AppLogService log)
{
    /// <summary>Writes the hold immediately on submission (D7 - there is nowhere else a pending
    /// request could live) and notifies the approver group. Blocks outright, rather than silently
    /// creating an unnoticed hold, when the notification mailbox isn't configured yet - visibility
    /// is the entire mitigation this design relies on against stale requests (§3.7).</summary>
    public async Task<PracticeIceSubmitResult> SubmitAsync(DateTime start, int durationMinutes, string hostName, string hostEmail, bool certified, string? notes, CancellationToken ct = default)
    {
        if (!facility.PracticeIceMailConfigured)
        {
            return PracticeIceSubmitResult.Invalid("Practice ice requests aren't being accepted yet - notifications haven't been configured. Please contact staff directly.");
        }
        if (!certified)
        {
            return PracticeIceSubmitResult.Invalid("You must certify that you're qualified to host practice ice.");
        }
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(hostEmail))
        {
            return PracticeIceSubmitResult.Invalid("Your sign-in didn't provide a name and email address - please contact staff.");
        }

        var window = await availability.FindPracticeIceWindowContainingAsync(start, ct);
        if (window is null)
        {
            return PracticeIceSubmitResult.Invalid("That slot is no longer available. Please choose another.");
        }

        var maxMinutes = (int)(window.End - start).TotalMinutes;
        if (durationMinutes % PracticeIceRules.SlotIntervalMinutes != 0 || durationMinutes < PracticeIceRules.MinSessionMinutes || durationMinutes > maxMinutes)
        {
            return PracticeIceSubmitResult.Invalid("That duration no longer fits the available window. Please choose another.");
        }

        var template = new SheetBooking
        {
            SheetMailbox = "",
            Start = start,
            End = start.AddMinutes(durationMinutes),
            Category = BookingCategory.PracticeIce,
            State = BookingState.Hold,
            RenterName = hostName,
            RenterEmail = hostEmail,
            BookedBy = hostName,
            Notes = notes
        };

        // The real safety net - a live, per-sheet-locked conflict check - lives inside
        // CreateAcrossSheetsAsync itself; FindPracticeIceWindowContainingAsync above is only a
        // courtesy pre-check against an up-to-60s-cached view (§4.3).
        var result = await bookingService.CreateAcrossSheetsAsync(facility.SheetMailboxes, template, hostName, ct);
        if (!result.IsSuccess)
        {
            return PracticeIceSubmitResult.Conflict();
        }

        // Logged immediately, before the notification is even attempted - the write already
        // happened and is the source of truth for what occurred here, regardless of what the mail
        // step below does. Live-found 2026-08-09: logging this after the mail send meant a mail
        // failure left NO audit trail for a request that had, in fact, succeeded.
        await log.LogActionAsync("PracticeIceRequested", hostName, string.Join(",", result.Bookings.Select(b => b.EventId)), string.Join(",", facility.SheetMailboxes),
            $"{template.Start:g}-{template.End:g}", ct);

        var notified = await TrySendMailAsync(facility.PracticeIceMailerMailbox, facility.PracticeIceApproverEmail, hostEmail,
            "Practice ice hosting request",
            $"{hostName} ({hostEmail}) has requested to host practice ice on {template.Start:dddd, MMM d} from {template.Start:h:mmtt} to {template.End:h:mmtt}." +
            (string.IsNullOrWhiteSpace(notes) ? "" : $"\n\nNotes: {notes}") +
            "\n\nReview at /practice-ice/approvals.",
            "PracticeIceRequestNotificationFailed", hostName, ct);

        return PracticeIceSubmitResult.Success(notified);
    }

    /// <summary>Every pending (Hold) practice ice request, one row per booking group, ordered by
    /// upcoming start time. Nothing here captures when a request was *submitted* - SheetBooking
    /// carries no created-at field - so this orders by how soon the slot itself arrives rather than
    /// by request age; in practice that's the more useful queue order anyway, since a request whose
    /// slot is imminent is the one that most needs a decision.</summary>
    public async Task<List<PracticeIceRequestSummary>> GetPendingAsync(CancellationToken ct = default)
    {
        var bookings = await GetHorizonBookingsAsync(ct);

        return bookings
            .Where(b => b.Category == BookingCategory.PracticeIce && b.State == BookingState.Hold)
            .GroupBy(b => b.BookingGroupId)
            .Select(g =>
            {
                var first = g.First();
                return new PracticeIceRequestSummary(g.Key, first.Start, first.End, first.RenterName ?? "Unknown", first.RenterEmail, first.Notes, g.Count());
            })
            .OrderBy(s => s.Start)
            .ToList();
    }

    public async Task<PracticeIceActionResult> ApproveAsync(Guid bookingGroupId, string actingUser, CancellationToken ct = default)
    {
        var members = await GetGroupMembersAsync(bookingGroupId, ct);
        if (members.Count == 0)
        {
            return PracticeIceActionResult.Failed();
        }

        var first = members[0];
        var updatedFields = new SheetBooking
        {
            SheetMailbox = "",
            Start = first.Start,
            End = first.End,
            Category = BookingCategory.PracticeIce,
            State = BookingState.Confirmed,
            RenterName = first.RenterName,
            RenterEmail = first.RenterEmail,
            Notes = first.Notes,
            BookedBy = first.BookedBy
        };

        var result = await bookingService.UpdateGroupAsync(members, updatedFields, actingUser, ct: ct);
        if (!result.IsSuccess)
        {
            return PracticeIceActionResult.Failed();
        }

        // The approval itself already succeeded and is logged inside UpdateGroupAsync - a failed
        // (or skipped, e.g. no email on file) notification here must not make this look like the
        // approval failed. Whether it went out IS surfaced back to the caller, though - discarding
        // it here left the previous bug (live-found 2026-08-09): staff had no way to know a decline
        // or approval notification silently didn't reach the volunteer.
        var notified = facility.PracticeIceMailConfigured && !string.IsNullOrWhiteSpace(first.RenterEmail)
            && await TrySendMailAsync(facility.PracticeIceMailerMailbox, first.RenterEmail, facility.PracticeIceApproverEmail,
                "Your practice ice request was approved",
                $"Your request to host practice ice on {first.Start:dddd, MMM d} from {first.Start:h:mmtt} to {first.End:h:mmtt} has been approved. See you on the ice!",
                "PracticeIceApprovalNotificationFailed", actingUser, ct);

        return PracticeIceActionResult.Done(notified);
    }

    public async Task<PracticeIceActionResult> DeclineAsync(Guid bookingGroupId, string reason, string actingUser, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A decline reason is required.", nameof(reason));
        }

        var members = await GetGroupMembersAsync(bookingGroupId, ct);
        if (members.Count == 0)
        {
            return PracticeIceActionResult.Failed();
        }

        var first = members[0];
        await bookingService.CancelGroupAsync(members, reopenAsGroupEventHold: false, actingUser, ct);

        var notified = facility.PracticeIceMailConfigured && !string.IsNullOrWhiteSpace(first.RenterEmail)
            && await TrySendMailAsync(facility.PracticeIceMailerMailbox, first.RenterEmail, facility.PracticeIceApproverEmail,
                "Your practice ice request was declined",
                $"Your request to host practice ice on {first.Start:dddd, MMM d} from {first.Start:h:mmtt} to {first.End:h:mmtt} was declined.\n\nReason: {reason}",
                "PracticeIceDeclineNotificationFailed", actingUser, ct);

        return PracticeIceActionResult.Done(notified);
    }

    private Task<List<SheetBooking>> GetHorizonBookingsAsync(CancellationToken ct) =>
        bookingService.GetBookingsForAllSheetsAsync(facility.Today, facility.Today.AddDays(facility.PracticeIceMaxHorizonDays + 1), ct);

    private async Task<List<SheetBooking>> GetGroupMembersAsync(Guid bookingGroupId, CancellationToken ct)
    {
        var bookings = await GetHorizonBookingsAsync(ct);
        return bookings.Where(b => b.BookingGroupId == bookingGroupId && b.Category == BookingCategory.PracticeIce && b.State == BookingState.Hold).ToList();
    }

    // A notification failure (missing Mail.Send consent, an Application Access Policy that doesn't
    // yet scope the mailer mailbox, a typo'd address) must never surface as an unhandled exception -
    // every caller above has already committed a real write by this point, and letting a mail
    // exception propagate turned a genuine success into what looked like a failed request, with no
    // audit trail, live-found 2026-08-09 during local testing. Returns whether the mail actually went out.
    private async Task<bool> TrySendMailAsync(string from, string to, string? replyTo, string subject, string body, string failureLogAction, string actingUser, CancellationToken ct)
    {
        try
        {
            await mail.SendMailAsync(from, to, replyTo, subject, body, ct);
            return true;
        }
        catch (Exception ex)
        {
            await log.LogActionAsync(failureLogAction, actingUser, details: ex.Message, ct: ct);
            return false;
        }
    }
}
