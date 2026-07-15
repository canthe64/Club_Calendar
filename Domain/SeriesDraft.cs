namespace FacilityScheduler.Domain;

/// <summary>
/// Working state for the two-step recurring series wizard. Same reference-sharing pattern as
/// BookingDraft. Step 1 configures the pattern; step 2 reviews the computed dates with a
/// per-date skip toggle (conflicts are flagged, never auto-skipped).
/// </summary>
public class SeriesDraft
{
    public HashSet<string> SelectedSheets { get; set; } = [];
    public BookingCategory Category { get; set; } = BookingCategory.League;

    /// <summary>Null until staff explicitly picks Hold or Confirmed - never silently defaults to
    /// one or the other. Only meaningful while <see cref="Category"/> is Rental; forced to true
    /// otherwise.</summary>
    public bool? CreateAsConfirmed { get; set; } = true;
    public DateTime FirstDate { get; set; } = DateTime.UtcNow.Date.AddDays(7);
    public DateTime LastDate { get; set; } = DateTime.UtcNow.Date.AddDays(7 * 8);
    public int StartHour { get; set; } = 19;
    public int EndHour { get; set; } = 21;
    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }

    public HashSet<DateTime> SkippedDates { get; set; } = [];

    public DateTime FirstOccurrenceStart => FirstDate.Date.AddHours(StartHour);
    public DateTime FirstOccurrenceEnd => FirstDate.Date.AddHours(EndHour);

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

    public void Reset()
    {
        SelectedSheets = [];
        Category = BookingCategory.League;
        CreateAsConfirmed = true;
        FirstDate = DateTime.UtcNow.Date.AddDays(7);
        LastDate = DateTime.UtcNow.Date.AddDays(7 * 8);
        StartHour = 19;
        EndHour = 21;
        RenterName = null;
        RenterPhone = null;
        RenterEmail = null;
        Notes = null;
        SkippedDates = [];
    }

    public List<DateTime> ComputeCandidateDates()
    {
        var dates = new List<DateTime>();
        for (var d = FirstDate.Date; d <= LastDate.Date; d = d.AddDays(7))
        {
            dates.Add(d);
        }
        return dates;
    }
}
