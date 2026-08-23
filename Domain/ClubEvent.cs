namespace FacilityScheduler.Domain;

/// <summary>
/// A whole-club-scale event (bonspiel, tournament, closure) on the single dedicated Club Events
/// mailbox - not tied to any sheet, no per-sheet locking or BookingGroupId/multi-sheet grouping
/// concept, no conflict checking against sheet bookings or other club events (architecture doc D13).
/// </summary>
public class ClubEvent
{
    public string? EventId { get; set; }
    public string? ICalUId { get; set; }

    public required string Title { get; set; }
    public required ClubEventCategory Category { get; set; }

    public required DateTime Start { get; set; }

    /// <summary>The INCLUSIVE last day at midnight when <see cref="IsAllDay"/>, an ordinary exclusive
    /// instant otherwise. Two different meanings on one property - use <see cref="ExclusiveEnd"/> for
    /// any overlap/containment test rather than comparing against this directly.</summary>
    public required DateTime End { get; set; }

    public bool IsAllDay { get; set; } = true;

    /// <summary><see cref="End"/> normalized to a real exclusive instant, so overlap tests read the
    /// same for all-day and timed events. An all-day event's End is the inclusive last DAY, so the
    /// instant it actually runs until is the following midnight.
    ///
    /// Live-found 2026-08-23: the staff calendar's closure cross-check compared against raw End, so a
    /// single-day all-day closure (Start == End == that day's midnight) never overlapped anything
    /// later that day - and since IsAllDay defaults to true, the most natural way to record "we're
    /// closed Tuesday" silently failed to block Tuesday bookings. PublicAvailabilityService had
    /// already open-coded this same fixup for practice ice; it lives here now so there's one
    /// definition rather than a copy per caller.</summary>
    public DateTime ExclusiveEnd => IsAllDay ? End.Date.AddDays(1) : End;

    /// <summary>Whether this event closes every sheet for its duration - a per-event staff choice,
    /// not implied by category (e.g. a promotional tournament listing might not close the ice).</summary>
    public bool MarksSheetsUnavailable { get; set; }

    public string? Notes { get; set; }
    public string? BookedBy { get; set; }
}
