namespace FacilityScheduler.Domain;

/// <summary>
/// Mutable working state for the Club Event form modal, shared by reference between the parent
/// page and the modal, mirroring BookingDraft's pattern. Much simpler than BookingDraft - no
/// sheets, no Hold/Confirmed state, no renter contact fields.
/// </summary>
public class ClubEventDraft
{
    public string? Title { get; set; }

    /// <summary>Null until staff explicitly picks a category - never silently defaults to one on a
    /// new club event.</summary>
    public ClubEventCategory? Category { get; set; }

    public bool IsAllDay { get; set; } = true;

    // Placeholders only - always overwritten by Reset()/LoadForEdit() before display; see
    // BookingDraft.Date for why this doesn't default from DateTime.UtcNow.
    public DateTime StartDate { get; set; } = DateTime.MinValue;
    public DateTime EndDate { get; set; } = DateTime.MinValue;

    /// <summary>Minutes from midnight, in 30-minute steps (see <see cref="CalendarStyles.TimeOptionsMinutes"/>).
    /// 1440 represents midnight at the end of the day, not the start of it. Only meaningful when
    /// <see cref="IsAllDay"/> is false.</summary>
    public int StartMinutes { get; set; } = 9 * 60;
    public int EndMinutes { get; set; } = 17 * 60;

    public bool MarksSheetsUnavailable { get; set; }
    public string? Notes { get; set; }

    public DateTime Start => IsAllDay ? StartDate.Date : StartDate.Date.AddMinutes(StartMinutes);
    public DateTime End => IsAllDay ? EndDate.Date : EndDate.Date.AddMinutes(EndMinutes);

    /// <summary>Non-null when editing an existing club event.</summary>
    public ClubEvent? EditingEvent { get; set; }

    /// <param name="today">The facility-local "today" (<see cref="FacilityConfiguration.Today"/>).</param>
    public void Reset(DateTime today)
    {
        Title = null;
        Category = null;
        IsAllDay = true;
        StartDate = today.AddDays(1);
        EndDate = today.AddDays(1);
        StartMinutes = 9 * 60;
        EndMinutes = 17 * 60;
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
        // Relative to Start's own date so an end time crossing midnight reads as 1440, not 0.
        StartMinutes = clubEvent.IsAllDay ? 9 * 60 : (int)(clubEvent.Start - clubEvent.Start.Date).TotalMinutes;
        EndMinutes = clubEvent.IsAllDay ? 17 * 60 : (int)(clubEvent.End - clubEvent.Start.Date).TotalMinutes;
        MarksSheetsUnavailable = clubEvent.MarksSheetsUnavailable;
        Notes = clubEvent.Notes;
        EditingEvent = clubEvent;
    }

    /// <summary>Builds the ClubEvent to send to CreateAsync/UpdateAsync - same mapping regardless of
    /// caller, so every call site (currently ClubEvents.razor and Calendar.razor) stays in sync
    /// automatically if a field is ever added. <paramref name="currentUser"/> is only used for a new
    /// event; editing preserves the original BookedBy rather than overwriting it with whoever made
    /// this particular edit.</summary>
    public ClubEvent ToClubEvent(string currentUser) => new()
    {
        EventId = EditingEvent?.EventId,
        ICalUId = EditingEvent?.ICalUId,
        Title = Title!,
        Category = Category!.Value,
        Start = Start,
        End = End,
        IsAllDay = IsAllDay,
        MarksSheetsUnavailable = MarksSheetsUnavailable,
        Notes = Notes,
        BookedBy = EditingEvent?.BookedBy ?? currentUser
    };
}
