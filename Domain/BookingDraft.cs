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

    // Placeholder only - always overwritten by Reset()/LoadForEdit() before this draft is ever
    // shown (every construction site calls one or the other immediately). Deliberately not defaulted
    // from DateTime.UtcNow here - this class has no DI access to the facility's time zone, and a
    // domain object silently reaching for server-UTC "today" is exactly the bug class fixed elsewhere
    // (FacilityConfiguration.Today).
    public DateTime Date { get; set; } = DateTime.MinValue;

    /// <summary>Minutes from midnight, in 30-minute steps (see <see cref="CalendarStyles.TimeOptionsMinutes"/>).
    /// 1440 represents midnight at the end of <see cref="Date"/>, not the start of it.</summary>
    public int StartMinutes { get; set; } = 18 * 60;
    public int EndMinutes { get; set; } = 20 * 60;
    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }

    public DateTime Start => Date.Date.AddMinutes(StartMinutes);
    public DateTime End => Date.Date.AddMinutes(EndMinutes);

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
        Date = effectiveStart.Date;
        StartMinutes = initialStart.HasValue ? initialStart.Value.Hour * 60 + initialStart.Value.Minute : 18 * 60;
        EndMinutes = StartMinutes + 120;
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
        Date = first.Start.Date;
        // Relative to Start's own date (not End's Hour/Minute) so an end time that rolls into the
        // next calendar day - i.e. midnight - reads as 1440, not 0.
        StartMinutes = (int)(first.Start - first.Start.Date).TotalMinutes;
        EndMinutes = (int)(first.End - first.Start.Date).TotalMinutes;
        RenterName = first.RenterName;
        RenterPhone = first.RenterPhone;
        RenterEmail = first.RenterEmail;
        Notes = first.Notes;
        EditingGroup = group;
    }
}
