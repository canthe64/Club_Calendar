namespace FacilityScheduler.Domain;

public class SheetBooking
{
    /// <summary>Graph REST id - not durable across some mailbox operations; prefer <see cref="ICalUId"/> for long-term reference.</summary>
    public string? EventId { get; set; }
    public string? ICalUId { get; set; }

    public required string SheetMailbox { get; set; }
    public required DateTime Start { get; set; }
    public required DateTime End { get; set; }
    public required BookingCategory Category { get; set; }
    public required BookingState State { get; set; }

    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }
    public string? BookedBy { get; set; }

    /// <summary>
    /// Links every sheet's event that belongs to the same conceptual booking (e.g. a rental
    /// spanning 3 sheets). Every booking gets one, even single-sheet ones, so downstream code
    /// never has to branch on single-vs-multi.
    /// </summary>
    public Guid BookingGroupId { get; set; }

    /// <summary>
    /// Non-null when this booking is one occurrence of a recurring series - the series master's
    /// EventId on this same sheet. Null for one-off bookings and for a series master itself.
    /// Used only for the "cancel entire series" backdoor, not surfaced as a primary edit path.
    /// </summary>
    public string? SeriesMasterId { get; set; }

    /// <summary>
    /// Non-null when this booking originated from an external booking platform's webhook
    /// notification (e.g. "breely:461906") rather than being entered directly by staff - the stable
    /// identity used to upsert on repeat/rescheduled notifications for the same external booking,
    /// since Graph's own EventId isn't known to the external system. Never set by staff-facing UI.
    /// </summary>
    public string? ExternalBookingId { get; set; }
}
