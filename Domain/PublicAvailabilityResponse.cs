using System.Text.Json.Serialization;

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

/// <summary>An open-for-group-event window on one sheet. SheetLabel is a public-safe display name
/// ("Sheet 1") - never the underlying resource mailbox address.</summary>
public record PublicSheetSlot(string SheetLabel, DateTime Start, DateTime End);

/// <summary>A whole-club event's public label - distinct from sheet slots per the architecture
/// doc's decision (§4.4), e.g. "Aug 15-17: Summer Bonspiel - all sheets closed".
///
/// This type backs two different surfaces (<see cref="PublicAvailabilityResponse"/>'s JSON feed and
/// <see cref="PublicMonthView"/>'s HTML calendar), and <see cref="Notes"/> is deliberately not treated
/// the same on both: <c>[JsonIgnore]</c> keeps it off the JSON feed's wire format entirely (D108,
/// 2026-09-01 - the feed has no per-viewer detail popup the way the calendar page does, so there's no
/// place to show it and no reason to publish it there), while the calendar page still reads it
/// directly in C# (never through this type's JSON serialization) to populate its click-to-detail
/// popup. When populated for the calendar page, it's null except when a staff member wrote it - never
/// for a Club Event this app itself created via the Breely webhook
/// (<c>BookedBy == BreelyBookingProcessor.BookedByLabel</c>), most importantly the "⚠ Web booking
/// needs review" triage marker, whose Notes embeds the real customer name Breely sent
/// (<c>ClientFullName</c>) plus an internal admin URL - see <c>PublicAvailabilityService</c>'s mapping
/// for where that gate is actually applied; this type has no way to enforce it itself. Trailing
/// optional parameter (default null) so existing positional construction sites keep compiling.</summary>
public record PublicClubEventLabel(string Title, ClubEventCategory Category, DateTime Start, DateTime End, bool IsAllDay, bool MarksSheetsUnavailable, [property: JsonIgnore] string? Notes = null);

/// <summary>
/// One sheet booking as shown on the public calendar (Month/Week/Day). Unlike PublicSheetSlot
/// (which is GroupEvent+Hold "available for group event" slots only - a subordinate feature), this
/// covers every category/state, since the public calendar's primary purpose is letting members see
/// what's going on club-wide while unauthenticated. Title mirrors the same "RenterName, or Category
/// if blank" logic the internal calendar already uses (e.g. a league's name) - the one deliberate
/// exception is a confirmed group event's renter name, which is handled by staff practice rather
/// than being stripped here, per an explicit decision (2026-07-15).
/// </summary>
/// <summary><see cref="Notes"/> is null except when a staff member wrote it (D108) - never for a
/// Breely-originated booking (<c>ExternalBookingId is not null</c>), whose title is already
/// suppressed for the identical reason (D52: a customer's real name, auto-populated with no staff
/// opportunity to redact it). This DTO exists only for the calendar page's own view - unlike
/// <see cref="PublicClubEventLabel"/>, there is currently no booking-shaped entry in the JSON feed
/// (<c>/api/public/availability</c> only ever describes open slots and Club Events, never an
/// occupied booking), so this Notes field has no JSON-feed counterpart to keep in sync with.</summary>
public record PublicMonthBooking(string Title, string CategoryLabel, DateTime Start, DateTime End, bool IsConfirmed, string? Notes = null);

/// <summary>The public calendar's data for a given date range - despite the name (kept from when
/// the public calendar had only a Month view), this same shape now backs Week and Day too
/// (PublicAvailabilityService.GetWeekViewAsync/GetDayViewAsync); only the range queried differs.</summary>
public record PublicMonthView(List<PublicMonthBooking> Bookings, List<PublicClubEventLabel> ClubEvents);

/// <summary>A date/time window where at least the searched-for number of sheets have an open
/// (GroupEvent+Hold) slot simultaneously - the /public/search result shape. Deliberately doesn't
/// name which sheets, only the window - the search only ever promises "at least N", not specific
/// sheet identities.</summary>
public record PublicAvailabilityWindow(DateTime Start, DateTime End);
