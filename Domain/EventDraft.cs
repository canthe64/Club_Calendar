namespace FacilityScheduler.Domain;

/// <summary>
/// Working state for the one unified event form, which presents on-ice bookings and off-ice club
/// events as a single "Event" concept with a mode toggle (architecture doc §4.4).
///
/// Deliberately COMPOSES <see cref="BookingDraft"/> and <see cref="ClubEventDraft"/> rather than
/// replacing them. Both already encode the same hard-won rule - each Minutes field is relative to its
/// OWN Date field, or a multi-day event double-counts the day offset on save - and both have tests
/// pinning it. Absorbing them would mean re-deriving that DateTime-to-minutes arithmetic here and
/// stranding those tests, which is exactly how that bug would come back. Because both inner drafts
/// use identical field conventions and identical Start/End getters, carrying state across a mode
/// switch is a plain scalar copy: this class performs no date arithmetic at all.
/// </summary>
public class EventDraft
{
    /// <summary>A soft guard against a fat-fingered end date, not a technical limit. On-ice only -
    /// a multi-month off-ice closure or season-long listing is entirely legitimate.</summary>
    public const int MaxOnIceSpanDays = 14;

    public EventMode Mode { get; private set; } = EventMode.OnIce;

    public BookingDraft OnIce { get; } = new();
    public ClubEventDraft OffIce { get; } = new();

    public bool IsEditing => OnIce.EditingGroup is not null || OffIce.EditingEvent is not null;

    /// <param name="today">The facility-local "today" (<see cref="FacilityConfiguration.Today"/>).</param>
    /// <param name="initialStart">The moment the event should open on - the date currently in view,
    /// or the exact slot that was clicked. When null, both modes fall back to tomorrow.</param>
    public void ResetForCreate(EventMode mode, DateTime today, IEnumerable<string>? initialSheets = null, DateTime? initialStart = null)
    {
        Mode = mode;

        // BOTH sides get reset, not just the opening mode's. Each draft's Date fields sit at a
        // DateTime.MinValue placeholder until Reset runs, so resetting only one side would leave the
        // other holding year-1 dates - which the very first mode toggle would then carry straight
        // into the date inputs.
        OnIce.Reset(today, initialSheets, initialStart);
        OffIce.Reset(today);

        // BookingDraft.Reset has already resolved the "no explicit start" fallback; mirroring its
        // result keeps both modes opening on the same day, so which mode you happened to start in
        // never changes the default date.
        OffIce.StartDate = OnIce.StartDate;
        OffIce.EndDate = OnIce.EndDate;
    }

    public void LoadForEdit(List<SheetBooking> group)
    {
        Mode = EventMode.OnIce;
        OnIce.LoadForEdit(group);
    }

    public void LoadForEdit(ClubEvent clubEvent)
    {
        Mode = EventMode.OffIce;
        OffIce.LoadForEdit(clubEvent);
    }

    /// <summary>Switches mode, carrying the fields the two kinds genuinely share. Mode-private fields
    /// (sheets, either category, Hold/Confirmed, contacts, the closure flag) are deliberately left
    /// where they are rather than cleared, so toggling away and back is lossless.</summary>
    public void SetMode(EventMode mode)
    {
        if (mode == Mode)
        {
            return;
        }

        if (mode == EventMode.OffIce)
        {
            CarryOver(OnIce, OffIce);
        }
        else
        {
            CarryOver(OffIce, OnIce);
        }

        Mode = mode;
    }

    internal static void CarryOver(BookingDraft from, ClubEventDraft to)
    {
        to.StartDate = from.StartDate;
        to.EndDate = from.EndDate;
        to.StartMinutes = from.StartMinutes;
        to.EndMinutes = from.EndMinutes;
        // An on-ice booking is always timed, so landing on the all-day default would hide the times
        // just carried over and silently widen the event to whole days.
        to.IsAllDay = false;
        to.Title = from.RenterName;
        to.Notes = from.Notes;
    }

    internal static void CarryOver(ClubEventDraft from, BookingDraft to)
    {
        to.StartDate = from.StartDate;
        to.EndDate = from.EndDate;
        // An all-day event has no meaningful times to carry - its Minutes fields are whatever the
        // draft was seeded with, not something the staff member chose - so fall back to the on-ice
        // defaults rather than presenting 9-to-5 as if it had been picked.
        to.StartMinutes = from.IsAllDay ? 18 * 60 : from.StartMinutes;
        to.EndMinutes = from.IsAllDay ? 20 * 60 : from.EndMinutes;
        to.RenterName = from.Title;
        to.Notes = from.Notes;
    }

    /// <summary>The single save rule for both modes. Previously duplicated across BookingFormModal,
    /// ClubEventFormModal, Calendar.razor and ClubEvents.razor - four copies that had already drifted
    /// (one dropped the span cap, another dropped the timed end-after-start check).</summary>
    internal static (bool CanSave, string Message) Validate(EventDraft draft) =>
        draft.Mode == EventMode.OnIce ? ValidateOnIce(draft.OnIce) : ValidateOffIce(draft.OffIce);

    private static (bool, string) ValidateOnIce(BookingDraft d)
    {
        if (!d.Category.HasValue) { return (false, "Choose a category."); }
        if (d.SelectedSheets.Count == 0) { return (false, "Select at least one sheet."); }
        if (string.IsNullOrWhiteSpace(d.RenterName)) { return (false, "Event title is required."); }
        if (d.End <= d.Start) { return (false, "The end time must be after the start time."); }
        if ((d.EndDate.Date - d.StartDate.Date).Days > MaxOnIceSpanDays)
        {
            return (false, $"An on-ice event can span at most {MaxOnIceSpanDays} days.");
        }
        if (d.Category == BookingCategory.GroupEvent && !d.CreateAsConfirmed.HasValue)
        {
            return (false, "Choose Hold for future group event or Confirmed booking.");
        }
        return (true, string.Empty);
    }

    // All-day: only the calendar date matters, and Start==End is a valid single-day event. Timed:
    // an out-of-order date/time (End before Start) must be rejected, but End == Start is allowed -
    // staff feedback 2026-08-27: a zero-duration off-ice event (a point-in-time marker - a ribbon
    // cutting, an announcement - with no real span) is legitimate and shouldn't need a fake minute
    // of padding just to pass validation. On-ice bookings keep the stricter End > Start rule in
    // ValidateOnIce above, unchanged - occupying a sheet for zero minutes isn't a real booking.
    private static (bool, string) ValidateOffIce(ClubEventDraft d)
    {
        if (!d.Category.HasValue) { return (false, "Choose a category."); }
        if (string.IsNullOrWhiteSpace(d.Title)) { return (false, "Event title is required."); }
        if (d.IsAllDay && d.EndDate.Date < d.StartDate.Date)
        {
            return (false, "The end date must be on or after the start date.");
        }
        if (!d.IsAllDay && d.End < d.Start)
        {
            return (false, "The end time can't be before the start time.");
        }
        return (true, string.Empty);
    }
}
