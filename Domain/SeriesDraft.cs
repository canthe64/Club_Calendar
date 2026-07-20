namespace FacilityScheduler.Domain;

/// <summary>
/// Working state for the two-step recurring series wizard. Same reference-sharing pattern as
/// BookingDraft. Step 1 configures the pattern; step 2 reviews the computed dates with a
/// per-date skip toggle (conflicts are flagged, never auto-skipped).
/// </summary>
public class SeriesDraft
{
    public HashSet<string> SelectedSheets { get; set; } = [];

    /// <summary>Null until staff explicitly picks a category - never silently defaults to one on a
    /// new series.</summary>
    public BookingCategory? Category { get; set; }

    /// <summary>Null until staff explicitly picks Hold or Confirmed - never silently defaults to
    /// one or the other. Only meaningful while <see cref="Category"/> is GroupEvent; forced to true
    /// otherwise.</summary>
    public bool? CreateAsConfirmed { get; set; }
    public DateTime FirstDate { get; set; } = DateTime.UtcNow.Date.AddDays(7);
    public DateTime LastDate { get; set; } = DateTime.UtcNow.Date.AddDays(7 * 8);

    /// <summary>Minutes from midnight, in 30-minute steps (see <see cref="CalendarStyles.TimeOptionsMinutes"/>).
    /// 1440 represents midnight at the end of the day, not the start of it.</summary>
    public int StartMinutes { get; set; } = 19 * 60;
    public int EndMinutes { get; set; } = 21 * 60;
    public string? RenterName { get; set; }
    public string? RenterPhone { get; set; }
    public string? RenterEmail { get; set; }
    public string? Notes { get; set; }

    public HashSet<DateTime> SkippedDates { get; set; } = [];

    public DateTime FirstOccurrenceStart => FirstDate.Date.AddMinutes(StartMinutes);
    public DateTime FirstOccurrenceEnd => FirstDate.Date.AddMinutes(EndMinutes);

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

    public void Reset()
    {
        SelectedSheets = [];
        Category = null;
        CreateAsConfirmed = null;
        FirstDate = DateTime.UtcNow.Date.AddDays(7);
        LastDate = DateTime.UtcNow.Date.AddDays(7 * 8);
        StartMinutes = 19 * 60;
        EndMinutes = 21 * 60;
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
