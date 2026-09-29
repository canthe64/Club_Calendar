using FacilityScheduler.Domain;
using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Services;

/// <summary>
/// Members cancelling their own practice ice (pending or approved) and make-up games from the link
/// in their booking emails (operator request 2026-09-29). The link opens /my-booking/cancel, which
/// requires the member to be signed in as the account that booked; cancelling happens only on that
/// page's button, never on opening the link - mail scanners (Safe Links and the like) open every
/// link in a message automatically. Everything is re-checked at the moment of cancelling.
/// </summary>
public class MemberBookingCancellationService(
    SheetBookingService bookingService,
    IGraphMailGateway mail,
    FacilityConfiguration facility,
    AppLogService log)
{
    public const string PagePath = "/my-booking/cancel";

    /// <summary>The line appended to a member's booking email - empty (and logged, so staff can
    /// see why links are missing) when Facility:PublicBaseUrl isn't configured, rather than
    /// sending a link that can't work.</summary>
    public async Task<string> CancelLinkTextAsync(Guid bookingGroupId, string actingUser, CancellationToken ct = default)
    {
        if (facility.PublicBaseUrl is null)
        {
            await log.LogActionAsync("CancelLinkOmitted", actingUser,
                details: "Facility:PublicBaseUrl isn't set - a booking email went out without its cancel link.", ct: ct);
            return "";
        }

        return $"\n\nNeed to cancel? Cancel this booking: {facility.PublicBaseUrl}{PagePath}?id={bookingGroupId}";
    }

    /// <summary>What the link for <paramref name="bookingGroupId"/> points at, for the member
    /// signed in with <paramref name="signedInEmail"/>.</summary>
    public async Task<MemberBookingLookup> FindAsync(Guid bookingGroupId, string signedInEmail, CancellationToken ct = default)
    {
        if (bookingGroupId == Guid.Empty)
        {
            return new MemberBookingLookup(MemberBookingStatus.NotFound);
        }

        // Member bookings are made inside the practice ice horizon, so that's the window to look
        // in; a booking that has already started today is still found (and reported as Started).
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(
            facility.Today, facility.Today.AddDays(facility.PracticeIceMaxHorizonDays + 1), ct);

        // Only bookings a member made: practice ice or a make-up game (League), carrying the
        // booker's email. Staff-made bookings never carry one for these categories, so a staff
        // booking is never reachable here - it reads as not found rather than revealing it exists.
        var members = bookings
            .Where(b => b.BookingGroupId == bookingGroupId)
            .Where(b => b.Category is BookingCategory.PracticeIce or BookingCategory.League)
            .Where(b => !string.IsNullOrWhiteSpace(b.RenterEmail))
            .OrderBy(b => b.SheetMailbox, StringComparer.Ordinal)
            .ToList();
        if (members.Count == 0)
        {
            return new MemberBookingLookup(MemberBookingStatus.NotFound);
        }

        var first = members[0];
        if (string.IsNullOrWhiteSpace(signedInEmail)
            || !string.Equals(first.RenterEmail!.Trim(), signedInEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new MemberBookingLookup(MemberBookingStatus.NotYours);
        }

        var details = new MemberBookingDetails(
            first.Category == BookingCategory.League ? MemberBookingKind.MakeUpGame : MemberBookingKind.PracticeIce,
            members.Min(m => m.Start),
            members.Max(m => m.End),
            members.Select(m => m.SheetMailbox).ToList(),
            IsPending: members.Any(m => m.State == BookingState.Hold),
            members);

        return new MemberBookingLookup(
            details.Start <= facility.Now ? MemberBookingStatus.Started : MemberBookingStatus.Cancellable,
            details);
    }

    /// <summary>Cancels the member's own booking if it's still theirs and hasn't started - re-checked
    /// here, not trusted from whatever the page showed. A hard delete on every sheet, same as a
    /// staff cancel; any group event hold time the booking took is not given back (D148). Emails the
    /// calendar team and the member; a failed email never undoes the cancellation.</summary>
    public async Task<MemberCancelResult> CancelAsync(Guid bookingGroupId, string signedInName, string signedInEmail, CancellationToken ct = default)
    {
        var lookup = await FindAsync(bookingGroupId, signedInEmail, ct);
        if (lookup.Status != MemberBookingStatus.Cancellable || lookup.Details is not { } booking)
        {
            return new MemberCancelResult(lookup.Status, null, false, false);
        }

        await bookingService.CancelGroupAsync(booking.Members, reopenAsGroupEventHold: false, signedInName, ct);
        await log.LogActionAsync("MemberBookingCancelled", signedInName, string.Join(",", booking.Members.Select(m => m.EventId)),
            string.Join(",", booking.SheetMailboxes), $"{booking.Description}, {booking.Start:g}-{booking.End:g}, cancelled by {signedInEmail}", ct);

        var when = $"{booking.Start:dddd, MMM d} from {booking.Start:h:mmtt} to {booking.End:h:mmtt} on {CalendarStyles.SheetListLabel(booking.SheetMailboxes)}";
        var staffNotified = false;
        var memberNotified = false;
        if (facility.PracticeIceMailConfigured)
        {
            staffNotified = await TrySendMailAsync(facility.PracticeIceApproverEmail, signedInEmail,
                $"{Capitalize(booking.Description)} cancelled",
                $"{signedInName} ({signedInEmail}) cancelled their {booking.Description} on {when}. It has been removed from the calendar.",
                "MemberCancelStaffNotificationFailed", signedInName, ct);
            memberNotified = await TrySendMailAsync(signedInEmail, facility.PracticeIceApproverEmail,
                $"Your {booking.Description} is cancelled",
                $"Your {booking.Description} on {when} has been cancelled and removed from the calendar.",
                "MemberCancelConfirmationFailed", signedInName, ct);
        }

        return new MemberCancelResult(MemberBookingStatus.Cancellable, booking, staffNotified, memberNotified);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private async Task<bool> TrySendMailAsync(string to, string? replyTo, string subject, string body, string failureLogAction, string actingUser, CancellationToken ct)
    {
        try
        {
            await mail.SendMailAsync(facility.PracticeIceMailerMailbox, to, replyTo, subject, body, ct);
            return true;
        }
        catch (Exception ex)
        {
            await log.LogActionAsync(failureLogAction, actingUser, details: ex.Message, ct: ct);
            return false;
        }
    }
}
