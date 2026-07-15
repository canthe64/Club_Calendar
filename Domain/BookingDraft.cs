namespace FacilityScheduler.Domain;

/// <summary>
/// Mutable working state for the booking form modal, shared by reference between the parent
/// page and the modal so form inputs can bind directly to it without formal two-way-binding
/// plumbing. Translated into a CreateAcrossSheetsAsync/UpdateGroupAsync call on save.
/// </summary>
public class BookingDraft
{
    public HashSet<string> SelectedSheets { get; set; } = [];
    public BookingCategory Category { get; set; } = BookingCategory.Rental;

    /// <summary>Null until staff explicitly picks Hold or Confirmed - never silently defaults to
    /// one or the other on a new booking. Only meaningful while <see cref="Category"/> is Rental;
    /// forced to true otherwise.</summary>
    public bool? CreateAsConfirmed { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public int StartHour { get; set; } = 18;
    public int EndHour { get; set; } = 20;
    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }

    public DateTime Start => Date.Date.AddHours(StartHour);
    public DateTime End => Date.Date.AddHours(EndHour);

    /// <summary>Non-null when editing an existing booking - the sibling events sharing its BookingGroupId.</summary>
    public List<SheetBooking>? EditingGroup { get; set; }

    /// <summary>
    /// Only Rentals can be a soft "hold" - every other category is a hard booking. Call this
    /// from the category picker so switching away from Rental coerces the state immediately,
    /// not just visually (the checkbox is hidden for non-Rental categories in the UI, but this
    /// keeps the underlying value honest even if something else reads it first). Switching INTO
    /// Rental resets the choice to unset, forcing staff to explicitly pick Hold or Confirmed again -
    /// but re-clicking Rental while already on Rental leaves an existing choice alone.
    /// </summary>
    public void SetCategory(BookingCategory category)
    {
        var wasRental = Category == BookingCategory.Rental;
        Category = category;
        if (category != BookingCategory.Rental)
        {
            CreateAsConfirmed = true;
        }
        else if (!wasRental)
        {
            CreateAsConfirmed = null;
        }
    }

    public void Reset(IEnumerable<string>? initialSheets = null, DateTime? initialStart = null)
    {
        SelectedSheets = initialSheets is null ? [] : [.. initialSheets];
        Category = BookingCategory.Rental;
        CreateAsConfirmed = null;
        Date = (initialStart ?? DateTime.UtcNow.Date.AddDays(1)).Date;
        StartHour = initialStart?.Hour ?? 18;
        EndHour = StartHour + 2;
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
        CreateAsConfirmed = first.Category != BookingCategory.Rental || first.State == BookingState.Confirmed;
        Date = first.Start.Date;
        StartHour = first.Start.Hour;
        EndHour = first.End.Hour;
        RenterName = first.RenterName;
        RenterPhone = first.RenterPhone;
        RenterEmail = first.RenterEmail;
        Notes = first.Notes;
        EditingGroup = group;
    }
}
