namespace FacilityScheduler.Domain;

/// <summary>Result of a make-up game request. Requests are auto-approved, so success means the
/// game is booked (Confirmed) - there's no pending state. Same Invalid/Conflict split as
/// <see cref="PracticeIceSubmitResult"/>, and the same rule that a failed email never turns a real
/// booking into an apparent failure: each notification's outcome is reported separately.</summary>
public class MakeUpGameSubmitResult
{
    public bool IsSuccess { get; private init; }
    public bool IsConflict { get; private init; }
    public string? Message { get; private init; }
    public MakeUpGameOption? Booked { get; private init; }
    public bool StaffNotified { get; private init; }
    public bool RequesterNotified { get; private init; }

    public static MakeUpGameSubmitResult Success(MakeUpGameOption booked, bool staffNotified, bool requesterNotified) =>
        new() { IsSuccess = true, Booked = booked, StaffNotified = staffNotified, RequesterNotified = requesterNotified };
    public static MakeUpGameSubmitResult Invalid(string message) => new() { Message = message };
    public static MakeUpGameSubmitResult Conflict() => new() { IsConflict = true, Message = "That time was just taken by someone else. Please choose another." };
}
