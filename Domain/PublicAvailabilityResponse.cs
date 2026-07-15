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
public record PublicClubEventLabel(string Title, DateTime Start, DateTime End, bool IsAllDay, bool MarksSheetsUnavailable);
