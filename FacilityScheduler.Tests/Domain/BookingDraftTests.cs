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
}
