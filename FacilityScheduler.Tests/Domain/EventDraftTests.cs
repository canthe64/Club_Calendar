using FacilityScheduler;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Domain;

public class EventDraftTests
{
    private static readonly DateTime Today = new(2026, 8, 21);

    private static SheetBooking Booking(DateTime start, DateTime end, string sheet = "sheet1@test.onmicrosoft.com") => new()
    {
        SheetMailbox = sheet,
        Start = start,
        End = end,
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = "Tuesday Night League",
    };

    // ---- The round-trip guard -------------------------------------------------------------------

    [Fact]
    public void SetMode_RoundTripOnAMultiDayTimedEvent_NeverChangesStartOrEnd()
    {
        // The exact fixture that caught the original EndMinutes-relative-to-the-wrong-date bug
        // (see ClubEventDraftTests): a Friday-evening bonspiel running into Sunday morning. The mode
        // toggle is a new place that same bug could reappear, so it gets the same fixture.
        var clubEvent = new ClubEvent
        {
            Title = "Weekend Bonspiel",
            Category = ClubEventCategory.Competitions,
            IsAllDay = false,
            Start = new DateTime(2026, 8, 21, 18, 0, 0),
            End = new DateTime(2026, 8, 23, 8, 0, 0)
        };

        var draft = new EventDraft();
        draft.LoadForEdit(clubEvent);

        var originalStart = draft.OffIce.Start;
        var originalEnd = draft.OffIce.End;

        draft.SetMode(EventMode.OnIce);
        draft.SetMode(EventMode.OffIce);

        Assert.Equal(originalStart, draft.OffIce.Start);
        Assert.Equal(originalEnd, draft.OffIce.End);
        Assert.Contains(draft.OffIce.EndMinutes, CalendarStyles.TimeOptionsMinutes);
    }

    [Fact]
    public void SetMode_RoundTripOnAnEventEndingExactlyAtMidnight_NeverChangesStartOrEnd()
    {
        var clubEvent = new ClubEvent
        {
            Title = "Friday Night Social",
            Category = ClubEventCategory.Activities,
            IsAllDay = false,
            Start = new DateTime(2026, 8, 21, 18, 0, 0),
            End = new DateTime(2026, 8, 22, 0, 0, 0)
        };

        var draft = new EventDraft();
        draft.LoadForEdit(clubEvent);

        draft.SetMode(EventMode.OnIce);
        draft.SetMode(EventMode.OffIce);

        Assert.Equal(new DateTime(2026, 8, 22, 0, 0, 0), draft.OffIce.End);
        Assert.Contains(draft.OffIce.EndMinutes, CalendarStyles.TimeOptionsMinutes);
    }

    [Fact]
    public void SetMode_RoundTripFromOnIce_NeverChangesStartOrEnd()
    {
        var draft = new EventDraft();
        draft.LoadForEdit([Booking(new DateTime(2026, 8, 21, 18, 0, 0), new DateTime(2026, 8, 23, 8, 0, 0))]);

        var originalStart = draft.OnIce.Start;
        var originalEnd = draft.OnIce.End;

        draft.SetMode(EventMode.OffIce);
        draft.SetMode(EventMode.OnIce);

        Assert.Equal(originalStart, draft.OnIce.Start);
        Assert.Equal(originalEnd, draft.OnIce.End);
        Assert.Contains(draft.OnIce.EndMinutes, CalendarStyles.TimeOptionsMinutes);
    }

    // ---- Carry-over -----------------------------------------------------------------------------

    [Fact]
    public void SetMode_OnIceToOffIce_CarriesDatesTimesTitleAndNotes()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);
        draft.OnIce.StartDate = new DateTime(2026, 9, 4);
        draft.OnIce.EndDate = new DateTime(2026, 9, 6);
        draft.OnIce.StartMinutes = 19 * 60;
        draft.OnIce.EndMinutes = 21 * 60;
        draft.OnIce.RenterName = "Fall Classic";
        draft.OnIce.Notes = "sponsor banners";

        draft.SetMode(EventMode.OffIce);

        Assert.Equal(new DateTime(2026, 9, 4), draft.OffIce.StartDate);
        Assert.Equal(new DateTime(2026, 9, 6), draft.OffIce.EndDate);
        Assert.Equal(19 * 60, draft.OffIce.StartMinutes);
        Assert.Equal(21 * 60, draft.OffIce.EndMinutes);
        Assert.Equal("Fall Classic", draft.OffIce.Title);
        Assert.Equal("sponsor banners", draft.OffIce.Notes);
    }

    [Fact]
    public void SetMode_OnIceToOffIce_TurnsOffAllDay_SoTheCarriedTimesStayVisible()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);
        Assert.True(draft.OffIce.IsAllDay); // the off-ice default

        draft.SetMode(EventMode.OffIce);

        Assert.False(draft.OffIce.IsAllDay);
    }

    [Fact]
    public void SetMode_OffIceToOnIce_AllDayEvent_SeedsTheOnIceDefaultTimes()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.IsAllDay = true;
        draft.OffIce.StartMinutes = 9 * 60;
        draft.OffIce.EndMinutes = 17 * 60;

        draft.SetMode(EventMode.OnIce);

        // Not 9-to-5: an all-day event's Minutes are seed values nobody picked, so presenting them
        // as a chosen ice time would be inventing a decision the staff member never made.
        Assert.Equal(18 * 60, draft.OnIce.StartMinutes);
        Assert.Equal(20 * 60, draft.OnIce.EndMinutes);
    }

    [Fact]
    public void SetMode_OffIceToOnIce_TimedEvent_CarriesTheExactMinutes()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.IsAllDay = false;
        draft.OffIce.StartMinutes = 7 * 60 + 30;
        draft.OffIce.EndMinutes = 9 * 60;

        draft.SetMode(EventMode.OnIce);

        Assert.Equal(7 * 60 + 30, draft.OnIce.StartMinutes);
        Assert.Equal(9 * 60, draft.OnIce.EndMinutes);
    }

    [Fact]
    public void SetMode_RoundTrip_PreservesModePrivateFields()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);
        draft.OnIce.SelectedSheets = ["sheet1@test.onmicrosoft.com", "sheet2@test.onmicrosoft.com"];
        draft.OnIce.SetCategory(BookingCategory.Bonspiel);
        draft.OnIce.RenterPhone = "555-0101";
        draft.OffIce.Category = ClubEventCategory.Meetings;
        draft.OffIce.MarksSheetsUnavailable = true;

        draft.SetMode(EventMode.OffIce);
        draft.SetMode(EventMode.OnIce);

        Assert.Equal(2, draft.OnIce.SelectedSheets.Count);
        Assert.Equal(BookingCategory.Bonspiel, draft.OnIce.Category);
        Assert.Equal("555-0101", draft.OnIce.RenterPhone);
        Assert.Equal(ClubEventCategory.Meetings, draft.OffIce.Category);
        Assert.True(draft.OffIce.MarksSheetsUnavailable);
    }

    [Fact]
    public void SetMode_ToTheModeAlreadySet_IsANoOp()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);
        draft.OffIce.Title = "untouched";

        draft.SetMode(EventMode.OnIce);

        Assert.Equal("untouched", draft.OffIce.Title);
        Assert.Equal(EventMode.OnIce, draft.Mode);
    }

    // ---- Reset ----------------------------------------------------------------------------------

    [Fact]
    public void ResetForCreate_SeedsBothInnerDrafts_SoTheFirstToggleCarriesRealDates()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);

        // The trap: resetting only the opening mode leaves the other side at its DateTime.MinValue
        // placeholder, and the very first toggle carries year-1 dates into the date inputs.
        Assert.NotEqual(DateTime.MinValue, draft.OffIce.StartDate);
        Assert.NotEqual(DateTime.MinValue, draft.OffIce.EndDate);
        Assert.NotEqual(DateTime.MinValue, draft.OnIce.StartDate);
    }

    [Fact]
    public void ResetForCreate_BothModesOpenOnTheSameDay()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today, initialStart: new DateTime(2026, 9, 4, 18, 0, 0));

        Assert.Equal(new DateTime(2026, 9, 4), draft.OnIce.StartDate);
        Assert.Equal(new DateTime(2026, 9, 4), draft.OffIce.StartDate);
    }

    [Fact]
    public void ResetForCreate_WithNoExplicitStart_BothModesDefaultToTomorrow()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);

        Assert.Equal(Today.AddDays(1).Date, draft.OnIce.StartDate.Date);
        Assert.Equal(Today.AddDays(1).Date, draft.OffIce.StartDate.Date);
    }

    [Fact]
    public void ResetForCreate_CarriesTheInitialSheetsIntoTheOnIceDraft()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today, initialSheets: ["sheet2@test.onmicrosoft.com"]);

        Assert.Equal(["sheet2@test.onmicrosoft.com"], draft.OnIce.SelectedSheets);
    }

    [Fact]
    public void ResetForCreate_IsNotEditing()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);

        Assert.False(draft.IsEditing);
    }

    // ---- LoadForEdit ----------------------------------------------------------------------------

    [Fact]
    public void LoadForEdit_Group_SetsOnIceModeAndMarksIsEditing()
    {
        var draft = new EventDraft();
        draft.LoadForEdit([Booking(Today.AddHours(18), Today.AddHours(20))]);

        Assert.Equal(EventMode.OnIce, draft.Mode);
        Assert.True(draft.IsEditing);
    }

    [Fact]
    public void LoadForEdit_ClubEvent_SetsOffIceModeAndMarksIsEditing()
    {
        var draft = new EventDraft();
        draft.LoadForEdit(new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            Start = Today,
            End = Today,
        });

        Assert.Equal(EventMode.OffIce, draft.Mode);
        Assert.True(draft.IsEditing);
    }

    // ---- Validate -------------------------------------------------------------------------------

    [Fact]
    public void Validate_FreshOnIceDraft_RequiresACategoryFirst()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Equal("Choose a category.", message);
    }

    [Fact]
    public void Validate_OnIceWithNoSheets_RequiresASheet()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today);
        draft.OnIce.SetCategory(BookingCategory.League);
        draft.OnIce.RenterName = "League";

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Equal("Select at least one sheet.", message);
    }

    [Fact]
    public void Validate_OnIceGroupEventWithoutHoldOrConfirmed_IsBlocked()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today, initialSheets: ["sheet1@test.onmicrosoft.com"]);
        draft.OnIce.SetCategory(BookingCategory.GroupEvent);
        draft.OnIce.RenterName = "Corporate outing";

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Equal("Choose Hold for future group event or Confirmed booking.", message);
    }

    [Fact]
    public void Validate_CompleteOnIceDraft_CanSave()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today, initialSheets: ["sheet1@test.onmicrosoft.com"]);
        draft.OnIce.SetCategory(BookingCategory.League);
        draft.OnIce.RenterName = "Tuesday Night League";

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.True(canSave);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void Validate_OnIceSpanBeyondTheCap_IsBlocked()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OnIce, Today, initialSheets: ["sheet1@test.onmicrosoft.com"]);
        draft.OnIce.SetCategory(BookingCategory.League);
        draft.OnIce.RenterName = "Very long league";
        draft.OnIce.EndDate = draft.OnIce.StartDate.AddDays(EventDraft.MaxOnIceSpanDays + 1);

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Contains($"{EventDraft.MaxOnIceSpanDays} days", message);
    }

    [Fact]
    public void Validate_OffIceEventSpanningMonths_IsAllowed()
    {
        // The span cap is on-ice only - a summer-long closure is a legitimate off-ice event.
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.Category = ClubEventCategory.Closure;
        draft.OffIce.Title = "Summer shutdown";
        draft.OffIce.EndDate = draft.OffIce.StartDate.AddDays(120);

        var (canSave, _) = EventDraft.Validate(draft);

        Assert.True(canSave);
    }

    [Fact]
    public void Validate_OffIceAllDaySingleDay_IsAllowed()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.Category = ClubEventCategory.Meetings;
        draft.OffIce.Title = "Board Meeting";
        draft.OffIce.IsAllDay = true;
        draft.OffIce.EndDate = draft.OffIce.StartDate;

        var (canSave, _) = EventDraft.Validate(draft);

        Assert.True(canSave);
    }

    [Fact]
    public void Validate_OffIceTimedEndBeforeStart_IsBlocked()
    {
        // The check ClubEvents.razor's own copy of this rule had dropped.
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.Category = ClubEventCategory.Meetings;
        draft.OffIce.Title = "Board Meeting";
        draft.OffIce.IsAllDay = false;
        draft.OffIce.StartMinutes = 17 * 60;
        draft.OffIce.EndMinutes = 9 * 60;

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Equal("The end time must be after the start time.", message);
    }

    [Fact]
    public void Validate_OffIceMissingTitle_IsBlocked()
    {
        var draft = new EventDraft();
        draft.ResetForCreate(EventMode.OffIce, Today);
        draft.OffIce.Category = ClubEventCategory.Meetings;

        var (canSave, message) = EventDraft.Validate(draft);

        Assert.False(canSave);
        Assert.Equal("Event title is required.", message);
    }
}
