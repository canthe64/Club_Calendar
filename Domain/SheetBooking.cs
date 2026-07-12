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
    public string? RenterContact { get; set; }
    public decimal? Price { get; set; }
    public string? Notes { get; set; }
    public string? BookedBy { get; set; }
}
