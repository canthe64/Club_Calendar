namespace FacilityScheduler.Domain;

/// <summary>Result of a practice ice hosting submission - distinguishes a caller mistake/stale
/// slot (Invalid, safe to show verbatim) from a genuine race against another booking (Conflict).</summary>
public class PracticeIceSubmitResult
{
    public bool IsSuccess { get; private init; }
    public bool IsConflict { get; private init; }
    public string? Message { get; private init; }

    /// <summary>True unless the booking itself succeeded but the approver notification email
    /// failed to send - the write is always the source of truth for IsSuccess; a downstream mail
    /// failure degrades this flag instead of turning a real success into an apparent failure.</summary>
    public bool NotificationSent { get; private init; } = true;

    public static PracticeIceSubmitResult Success(bool notificationSent = true) => new() { IsSuccess = true, NotificationSent = notificationSent };
    public static PracticeIceSubmitResult Invalid(string message) => new() { Message = message };
    public static PracticeIceSubmitResult Conflict() => new() { IsConflict = true, Message = "That slot was just claimed by someone else. Please choose another." };
}

/// <summary>One pending practice ice request as shown on the staff approval queue - the group's
/// sheet-events collapsed into a single row, same spirit as the public calendar's own dedupe.</summary>
public record PracticeIceRequestSummary(Guid BookingGroupId, DateTime Start, DateTime End, string HostName, string? HostEmail, string? Notes, int SheetCount);

/// <summary>Result of an approve/decline action - same NotificationSent split as
/// PracticeIceSubmitResult, and for the same reason: a downstream mail failure must be visible to
/// the staff member who acted, not silently swallowed the way it was before this type existed
/// (live-found 2026-08-09 - the approvals page discarded the outcome entirely).</summary>
public record PracticeIceActionResult(bool Success, bool NotificationSent)
{
    public static PracticeIceActionResult Failed() => new(false, false);
    public static PracticeIceActionResult Done(bool notificationSent) => new(true, notificationSent);
}
