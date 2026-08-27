namespace FacilityScheduler.Domain;

/// <summary>
/// Mutable working state for the booking form modal, shared by reference between the parent
/// page and the modal so form inputs can bind directly to it without formal two-way-binding
/// plumbing. Translated into a CreateAcrossSheetsAsync/UpdateGroupAsync call on save.
/// </summary>
public class BookingDraft
{
    public HashSet<string> SelectedSheets { get; set; } = [];

    /// <summary>Null until staff explicitly picks a category - never silently defaults to one on a
    /// new booking (avoids staff mistakenly leaving a booking under whatever category happened to
    /// be selected last).</summary>
    public BookingCategory? Category { get; set; }

    /// <summary>Null until staff explicitly picks Hold or Confirmed - never silently defaults to
    /// one or the other on a new booking. Only meaningful while <see cref="Category"/> is GroupEvent;
    /// forced to true otherwise.</summary>
    public bool? CreateAsConfirmed { get; set; }

    // Placeholders only - always overwritten by Reset()/LoadForEdit() before this draft is ever
    // shown (every construction site calls one or the other immediately). Deliberately not defaulted
    // from DateTime.UtcNow here - this class has no DI access to the facility's time zone, and a
    // domain object silently reaching for server-UTC "today" is exactly the bug class fixed elsewhere
    // (FacilityConfiguration.Today).
    //
    // Separate StartDate/EndDate (not one Date field) so a booking can span multiple calendar days -
    // added for multi-day bookings (e.g. a weekend bonspiel), mirroring ClubEventDraft's own
    // StartDate/EndDate split. EndDate defaults to StartDate in Reset(), so the by-far-more-common
    // single-day booking costs nothing extra.
    public DateTime StartDate { get; set; } = DateTime.MinValue;
    public DateTime EndDate { get; set; } = DateTime.MinValue;

    /// <summary>Minutes from midnight, in 15-minute steps (see <see cref="CalendarStyles.TimeOptionsMinutes"/>).
    /// 1440 represents midnight at the end of <see cref="StartDate"/>/<see cref="EndDate"/>
    /// respectively, not the start of it.</summary>
    public int StartMinutes { get; set; } = 18 * 60;
    public int EndMinutes { get; set; } = 20 * 60;
    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }

    public DateTime Start => StartDate.Date.AddMinutes(StartMinutes);
    public DateTime End => EndDate.Date.AddMinutes(EndMinutes);

    /// <summary>Non-null when editing an existing booking - the sibling events sharing its BookingGroupId.</summary>
    public List<SheetBooking>? EditingGroup { get; set; }

    /// <summary>
    /// Only Group Events can be a soft "hold" - every other category is a hard booking. Call this
    /// from the category picker so switching away from GroupEvent coerces the state immediately,
    /// not just visually (the checkbox is hidden for non-GroupEvent categories in the UI, but this
    /// keeps the underlying value honest even if something else reads it first). Switching INTO
    /// GroupEvent resets the choice to unset, forcing staff to explicitly pick Hold or Confirmed
    /// again - but re-clicking GroupEvent while already on GroupEvent leaves an existing choice alone.
    /// </summary>
    public void SetCategory(BookingCategory category)
    {
        var wasGroupEvent = Category == BookingCategory.GroupEvent;
        Category = category;
        if (category != BookingCategory.GroupEvent)
        {
            CreateAsConfirmed = true;
        }
        else if (!wasGroupEvent)
        {
            CreateAsConfirmed = null;
        }
    }

    /// <param name="today">The facility-local "today" (<see cref="FacilityConfiguration.Today"/>) -
    /// required, not defaulted internally, so this always reflects the facility's own time zone
    /// rather than the server's UTC clock.</param>
    public void Reset(DateTime today, IEnumerable<string>? initialSheets = null, DateTime? initialStart = null)
    {
        SelectedSheets = initialSheets is null ? [] : [.. initialSheets];
        Category = null;
        CreateAsConfirmed = null;
        var effectiveStart = initialStart ?? today.AddDays(1);
        StartDate = effectiveStart.Date;
        EndDate = effectiveStart.Date;
        // Snapped: the clicked slot comes from a grid the picker doesn't control, so it isn't
        // guaranteed to land on the quarter-hour grid the way the 18*60 fallback is.
        StartMinutes = initialStart.HasValue
            ? CalendarStyles.SnapToQuarter(initialStart.Value.Hour * 60 + initialStart.Value.Minute)
            : 18 * 60;
        // Clamped so a late-evening slot can't seed an end past the day's last option: clicking the
        // 11 PM slot used to produce 1500, which matched nothing and displayed as 12 AM. A shorter
        // default beats an invalid one - staff can still extend it onto the next date.
        EndMinutes = Math.Min(StartMinutes + 120, 24 * 60);
        RenterName = null;
        RenterPhone = null;
        RenterEmail = null;
        Notes = null;
        EditingGroup = null;
    }

    public void LoadForEdit(List<SheetBooking> group)
    {
        var first = group[0];
        SelectedSheets = [.. group.Select(b => b.SheetMailbox)];
        Category = first.Category;
        CreateAsConfirmed = first.Category != BookingCategory.GroupEvent || first.State == BookingState.Confirmed;
        StartDate = first.Start.Date;
        EndDate = first.End.Date;
        // Each Minutes field relative to its OWN Date field, not the other side's - otherwise a
        // multi-day booking's EndMinutes would carry the day offset while EndDate is already
        // advanced, double-counting it back into a wrong End on save. (This exact bug shipped in
        // ClubEventDraft.LoadForEdit and was found/fixed while designing this - not repeated here.)
        // Snapped, because a booking's stored time need not sit on the picker's grid - an
        // Outlook-side edit, or an event created under the old 30-minute grid. An off-grid value
        // matches no option and a <select> would silently display its first entry (12 AM), rewriting
        // the time on the next save.
        StartMinutes = CalendarStyles.SnapToQuarter((int)(first.Start - first.Start.Date).TotalMinutes);
        EndMinutes = CalendarStyles.SnapToQuarter((int)(first.End - first.End.Date).TotalMinutes);
        RenterName = first.RenterName;
        RenterPhone = first.RenterPhone;
        RenterEmail = first.RenterEmail;
        Notes = first.Notes;
        EditingGroup = group;
    }
}
