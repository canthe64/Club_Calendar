namespace FacilityScheduler.Domain;

/// <summary>
/// Mutable working state for the Club Event form modal, shared by reference between the parent
/// page and the modal, mirroring BookingDraft's pattern. Much simpler than BookingDraft - no
/// sheets, no Hold/Confirmed state, no renter contact fields.
/// </summary>
public class ClubEventDraft
{
    public string? Title { get; set; }
    public ClubEventCategory Category { get; set; } = ClubEventCategory.Bonspiel;

    public bool IsAllDay { get; set; } = true;
    public DateTime StartDate { get; set; } = DateTime.UtcNow.Date.AddDays(1);
    public DateTime EndDate { get; set; } = DateTime.UtcNow.Date.AddDays(1);
    public int StartHour { get; set; } = 9;
    public int EndHour { get; set; } = 17;

    public bool MarksSheetsUnavailable { get; set; }
    public string? Notes { get; set; }

    public DateTime Start => IsAllDay ? StartDate.Date : StartDate.Date.AddHours(StartHour);
    public DateTime End => IsAllDay ? EndDate.Date : EndDate.Date.AddHours(EndHour);

    /// <summary>Non-null when editing an existing club event.</summary>
    public ClubEvent? EditingEvent { get; set; }

    public void Reset()
    {
        Title = null;
        Category = ClubEventCategory.Bonspiel;
        IsAllDay = true;
        StartDate = DateTime.UtcNow.Date.AddDays(1);
        EndDate = DateTime.UtcNow.Date.AddDays(1);
        StartHour = 9;
        EndHour = 17;
        MarksSheetsUnavailable = false;
        Notes = null;
        EditingEvent = null;
    }

    public void LoadForEdit(ClubEvent clubEvent)
    {
        Title = clubEvent.Title;
        Category = clubEvent.Category;
        IsAllDay = clubEvent.IsAllDay;
        StartDate = clubEvent.Start.Date;
        EndDate = clubEvent.End.Date;
        StartHour = clubEvent.IsAllDay ? 9 : clubEvent.Start.Hour;
        EndHour = clubEvent.IsAllDay ? 17 : clubEvent.End.Hour;
        MarksSheetsUnavailable = clubEvent.MarksSheetsUnavailable;
        Notes = clubEvent.Notes;
        EditingEvent = clubEvent;
    }
}
