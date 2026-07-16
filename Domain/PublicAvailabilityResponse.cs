namespace FacilityScheduler.Domain;

/// <summary>
/// The public endpoint's minimized response shape - deliberately separate from SheetBooking/ClubEvent
/// rather than a reuse of the internal API with anonymous access allowed (architecture doc §5.4).
/// Only ever add a field here that's explicitly meant to be public.
/// </summary>
public record PublicAvailabilityResponse(
    DateTime GeneratedAtUtc,
    List<PublicSheetSlot> SheetSlots,
    List<PublicClubEventLabel> ClubEvents);

/// <summary>An open-for-rental window on one sheet. SheetLabel is a public-safe display name
/// ("Sheet 1") - never the underlying resource mailbox address.</summary>
public record PublicSheetSlot(string SheetLabel, DateTime Start, DateTime End);

/// <summary>A whole-club event's public label - distinct from sheet slots per the architecture
/// doc's decision (§4.4), e.g. "Aug 15-17: Summer Bonspiel - all sheets reserved".</summary>
public record PublicClubEventLabel(string Title, ClubEventCategory Category, DateTime Start, DateTime End, bool IsAllDay, bool MarksSheetsUnavailable);

/// <summary>
/// One sheet booking as shown on the public month calendar. Unlike PublicSheetSlot (which is
/// Rental+Hold "available for rental" slots only - a subordinate feature), this covers every
/// category/state, since the public calendar's primary purpose is letting members see what's going
/// on club-wide while unauthenticated. Title mirrors the same "RenterName, or Category if blank"
/// logic the internal calendar already uses (e.g. a league's name) - the one deliberate exception is
/// a confirmed rental's renter name, which is handled by staff practice rather than being stripped
/// here, per an explicit decision (2026-07-15).
/// </summary>
public record PublicMonthBooking(string Title, string CategoryLabel, DateTime Start, DateTime End, bool IsConfirmed);

/// <summary>The public month calendar's data for one visible month grid.</summary>
public record PublicMonthView(List<PublicMonthBooking> Bookings, List<PublicClubEventLabel> ClubEvents);
