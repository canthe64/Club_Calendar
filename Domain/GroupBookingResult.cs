namespace FacilityScheduler.Domain;

/// <summary>
/// Result of a multi-sheet booking operation (a booking spanning several sheets at once).
/// All-or-nothing: on conflict, nothing is created and every conflicting existing booking
/// across every requested sheet is reported so the caller can deselect a sheet or change time.
/// </summary>
public class GroupBookingResult
{
    public bool IsSuccess { get; private init; }
    public List<SheetBooking> Bookings { get; private init; } = [];
    public List<SheetBooking> Conflicts { get; private init; } = [];

    public static GroupBookingResult Success(List<SheetBooking> bookings) => new() { IsSuccess = true, Bookings = bookings };

    public static GroupBookingResult Conflict(List<SheetBooking> conflicts) => new() { IsSuccess = false, Conflicts = conflicts };
}
