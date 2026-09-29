namespace FacilityScheduler.Domain;

/// <summary>What a member's cancel link points at, from that member's point of view.</summary>
public enum MemberBookingStatus
{
    /// <summary>No such booking on the calendar any more (cancelled, declined, or never existed).</summary>
    NotFound,

    /// <summary>It exists, but was booked by a different account. Nothing else is revealed.</summary>
    NotYours,

    /// <summary>The member's own booking, but it has already started.</summary>
    Started,

    /// <summary>The member's own booking, not yet started - cancellable.</summary>
    Cancellable
}

/// <summary>The two member-made booking kinds a cancel link can point at.</summary>
public enum MemberBookingKind
{
    PracticeIce,
    MakeUpGame
}

/// <summary>A member-made booking as the cancel page shows it. <see cref="Details"/> is only
/// populated for the member's own booking (Started or Cancellable).</summary>
public sealed record MemberBookingLookup(MemberBookingStatus Status, MemberBookingDetails? Details = null);

/// <summary>One member-made booking group, collapsed across its sheets.</summary>
public sealed record MemberBookingDetails(
    MemberBookingKind Kind,
    DateTime Start,
    DateTime End,
    IReadOnlyList<string> SheetMailboxes,
    bool IsPending,
    IReadOnlyList<SheetBooking> Members)
{
    /// <summary>"make-up game", "practice ice", or "practice ice request" (still awaiting approval).</summary>
    public string Description => Kind switch
    {
        MemberBookingKind.MakeUpGame => "make-up game",
        _ => IsPending ? "practice ice request" : "practice ice"
    };
}

/// <summary>Outcome of a member cancelling their own booking. Emails are reported separately and
/// never undo the cancellation, same rule as every other member write path.</summary>
public sealed record MemberCancelResult(MemberBookingStatus Status, MemberBookingDetails? Cancelled, bool StaffNotified, bool MemberNotified)
{
    public bool IsCancelled => Cancelled is not null;
}
