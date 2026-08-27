using FacilityScheduler;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Domain;

public class BookingDraftTests
{
    [Fact]
    public void Reset_DefaultsEndDateToStartDate()
    {
        // The common case (a same-day booking) must cost nothing extra: EndDate should follow
        // StartDate automatically until staff deliberately picks a different end date.
        var draft = new BookingDraft();
        var today = new DateTime(2026, 8, 19);

        draft.Reset(today);

        Assert.Equal(draft.StartDate, draft.EndDate);
        Assert.Equal(draft.StartDate, draft.Start.Date);
        Assert.Equal(draft.EndDate, draft.End.Date);
    }

    [Fact]
    public void StartAndEnd_SpanMultipleDays_WhenDatesDiffer()
    {
        var draft = new BookingDraft();
        draft.Reset(new DateTime(2026, 8, 19));
        draft.StartDate = new DateTime(2026, 8, 21);
        draft.EndDate = new DateTime(2026, 8, 23);
        draft.StartMinutes = 18 * 60;
        draft.EndMinutes = 20 * 60;

        Assert.Equal(new DateTime(2026, 8, 21, 18, 0, 0), draft.Start);
        Assert.Equal(new DateTime(2026, 8, 23, 20, 0, 0), draft.End);
    }

    [Fact]
    public void LoadForEdit_MultiDayBooking_RoundTripsThroughDraftFields()
    {
        // Mirrors the exact bug found and fixed in ClubEventDraft.LoadForEdit (see
        // ClubEventDraftTests): EndMinutes must be computed relative to End's own date, not
        // Start's, or a multi-day booking's end time silently drifts on re-save.
        var group = new List<SheetBooking>
        {
            new()
            {
                SheetMailbox = "sheet1@example.com",
                Start = new DateTime(2026, 8, 21, 18, 0, 0),
                End = new DateTime(2026, 8, 23, 20, 0, 0),
                Category = BookingCategory.Bonspiel,
                State = BookingState.Confirmed,
                RenterName = "Fall Mixed Bonspiel"
            }
        };

        var draft = new BookingDraft();
        draft.LoadForEdit(group);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(group[0].Start, draft.Start);
        Assert.Equal(group[0].End, draft.End);
    }

    [Fact]
    public void LoadForEdit_BookingEndingExactlyAtMidnightTheNextDay_RoundTripsThroughDraftFields()
    {
        var group = new List<SheetBooking>
        {
            new()
            {
                SheetMailbox = "sheet1@example.com",
                Start = new DateTime(2026, 8, 21, 18, 0, 0),
                End = new DateTime(2026, 8, 22, 0, 0, 0),
                Category = BookingCategory.GroupEvent,
                State = BookingState.Hold,
                RenterName = "Late Social"
            }
        };

        var draft = new BookingDraft();
        draft.LoadForEdit(group);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(group[0].End, draft.End);
    }

    [Fact]
    public void LoadForEdit_SameDayBooking_StillRoundTripsThroughDraftFields()
    {
        var group = new List<SheetBooking>
        {
            new()
            {
                SheetMailbox = "sheet1@example.com",
                Start = new DateTime(2026, 8, 21, 18, 0, 0),
                End = new DateTime(2026, 8, 21, 20, 0, 0),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Tuesday League"
            }
        };

        var draft = new BookingDraft();
        draft.LoadForEdit(group);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(group[0].Start, draft.Start);
        Assert.Equal(group[0].End, draft.End);
    }

    [Fact]
    public void LoadForEdit_TimeEditedOutsideTheApp_SnapsOntoTheQuarterHourGrid()
    {
        // An Outlook-side edit can put a booking at any minute. The picker only offers quarters, and
        // a <select> holding a value none of its options match displays its FIRST option - 12 AM -
        // so an unsnapped 6:07 would show as midnight and save as midnight.
        var group = new List<SheetBooking>
        {
            new()
            {
                SheetMailbox = "sheet1@example.com",
                Start = new DateTime(2026, 8, 21, 18, 7, 0),
                End = new DateTime(2026, 8, 21, 20, 22, 0),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Edited in Outlook"
            }
        };

        var draft = new BookingDraft();
        draft.LoadForEdit(group);

        Assert.Contains(draft.StartMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(new DateTime(2026, 8, 21, 18, 0, 0), draft.Start);
        Assert.Equal(new DateTime(2026, 8, 21, 20, 15, 0), draft.End);
    }

    [Fact]
    public void Reset_LateEveningSlot_SeedsAnEndTimeThePickerCanDisplay()
    {
        // 11 PM + the default two hours would be 1500, past the day's last option; it used to
        // display as 12 AM. Clamped to end-of-day instead.
        var draft = new BookingDraft();
        draft.Reset(new DateTime(2026, 8, 19), initialStart: new DateTime(2026, 8, 20, 23, 0, 0));

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(24 * 60, draft.EndMinutes);
        Assert.True(draft.End > draft.Start);
    }
}
