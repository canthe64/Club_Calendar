using FacilityScheduler.Domain;
using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Services;

/// <summary>
/// The make-up game write path (staff request 2026-09-28): a signed-in member picks an offered
/// two-hour slot, acknowledges the policy, and the game is booked straight onto the calendar -
/// auto-approved, no staff step - as a Confirmed League booking on the highest-numbered free sheet.
/// The calendar team's distribution list and the requester are both emailed. Like practice ice,
/// everything the page sends is re-validated here: the start comes from a query string and the
/// offer can be stale by the time the member confirms.
/// </summary>
public class MakeUpGameService(
    SheetBookingService bookingService,
    PublicAvailabilityService availability,
    IGraphMailGateway mail,
    FacilityConfiguration facility,
    AppLogService log)
{
    public async Task<MakeUpGameSubmitResult> SubmitAsync(DateTime start, string requesterName, string requesterEmail, bool acknowledged, CancellationToken ct = default)
    {
        // Auto-approval means the confirmation email is staff's only notice that a game was
        // booked - so, as with practice ice, refuse rather than book silently until it's set up.
        if (!facility.PracticeIceMailConfigured)
        {
            return MakeUpGameSubmitResult.Invalid("Make-up game requests aren't being accepted yet - notifications haven't been configured. Please contact the calendar team directly.");
        }
        if (!acknowledged)
        {
            return MakeUpGameSubmitResult.Invalid("Please confirm you've read and agree to the make-up game policy.");
        }
        if (string.IsNullOrWhiteSpace(requesterName) || string.IsNullOrWhiteSpace(requesterEmail))
        {
            return MakeUpGameSubmitResult.Invalid("Your sign-in didn't provide a name and email address - please contact the calendar team.");
        }

        var option = await availability.GetMakeUpGameOptionAsync(start, ct);
        if (option is null)
        {
            return MakeUpGameSubmitResult.Invalid("That time is no longer available. Please choose another.");
        }

        var template = new SheetBooking
        {
            SheetMailbox = "",
            Start = option.Start,
            End = option.End,
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = MakeUpGameRules.BookingTitle(requesterName),
            RenterEmail = requesterEmail,
            BookedBy = requesterName
        };

        // The live, per-sheet-locked conflict check inside the create call is the real safety net;
        // GetMakeUpGameOptionAsync above is a courtesy check against a cached view. Open Group Event
        // hold time guests can no longer book is taken and trimmed, the same as practice ice.
        var result = await bookingService.CreateTakingReleasedHoldsAsync([option.SheetMailbox], template, facility.GroupEventHoldReleaseCutoff, requesterName, ct);
        if (!result.IsSuccess)
        {
            return MakeUpGameSubmitResult.Conflict();
        }

        var sheetLabel = CalendarStyles.SheetLabel(option.SheetMailbox);
        await log.LogActionAsync("MakeUpGameBooked", requesterName, result.Bookings[0].EventId, option.SheetMailbox,
            $"{option.Start:g}-{option.End:g}, requested by {requesterEmail}", ct);

        var when = $"{option.Start:dddd, MMM d} from {option.Start:h:mmtt} to {option.End:h:mmtt} on {sheetLabel}";
        var staffNotified = await TrySendMailAsync(facility.PracticeIceApproverEmail, requesterEmail,
            "Make-up game booked",
            $"{requesterName} ({requesterEmail}) booked a make-up game on {when}. It's on the calendar as confirmed - no approval needed.",
            "MakeUpGameStaffNotificationFailed", requesterName, ct);
        var requesterNotified = await TrySendMailAsync(requesterEmail, facility.PracticeIceApproverEmail,
            "Your make-up game is booked",
            $"Your make-up game is booked for {when}. Reply to this email if you need to change or cancel it.",
            "MakeUpGameRequesterNotificationFailed", requesterName, ct);

        return MakeUpGameSubmitResult.Success(option, staffNotified, requesterNotified);
    }

    // Same contract as PracticeIceRequestService's: the booking already exists, so a mail failure
    // is logged and reported back, never thrown.
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
