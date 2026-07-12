namespace FacilityScheduler.Domain;

public class BookingResult
{
    public bool IsSuccess { get; private init; }
    public SheetBooking? Booking { get; private init; }
    public List<SheetBooking> Conflicts { get; private init; } = [];

    public static BookingResult Success(SheetBooking booking) => new() { IsSuccess = true, Booking = booking };

    public static BookingResult Conflict(List<SheetBooking> conflicts) => new() { IsSuccess = false, Conflicts = conflicts };
}
