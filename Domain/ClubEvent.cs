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
    public required DateTime End { get; set; }
    public bool IsAllDay { get; set; } = true;

    /// <summary>Whether this event closes all 5 sheets for its duration - a per-event staff choice,
    /// not implied by category (e.g. a promotional tournament listing might not close the ice).</summary>
    public bool MarksSheetsUnavailable { get; set; }

    public string? Notes { get; set; }
    public string? BookedBy { get; set; }
}
