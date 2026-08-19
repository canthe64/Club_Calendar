using FacilityScheduler;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Domain;

public class ClubEventDraftTests
{
    [Fact]
    public void LoadForEdit_MultiDayTimedEvent_RoundTripsThroughDraftFields()
    {
        // EndMinutes used to be computed relative to Start's date instead of End's, so a genuinely
        // multi-day event (e.g. a Friday-evening bonspiel running into Sunday morning) produced an
        // EndMinutes value outside the 0-1440 range the "To" dropdown offers, and re-saving without
        // touching that field silently pushed End multiple days later than the original.
        var clubEvent = new ClubEvent
        {
            Title = "Weekend Bonspiel",
            Category = ClubEventCategory.Competitions,
            IsAllDay = false,
            Start = new DateTime(2026, 8, 21, 18, 0, 0),
            End = new DateTime(2026, 8, 23, 8, 0, 0)
        };

        var draft = new ClubEventDraft();
        draft.LoadForEdit(clubEvent);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(clubEvent.End, draft.End);
        Assert.Equal(clubEvent.End, draft.ToClubEvent("staff@example.com").End);
    }

    [Fact]
    public void LoadForEdit_EventEndingExactlyAtMidnightTheNextDay_RoundTripsThroughDraftFields()
    {
        // Narrower case than the multi-day one above: an event ending exactly at midnight the day
        // after it starts. EndDate is already advanced to that day (End.Date), so EndMinutes must be
        // relative to that same date (0), not Start's date (which would compute 1440 and, combined
        // with the already-advanced EndDate, add a whole extra day on save).
        var clubEvent = new ClubEvent
        {
            Title = "Friday Night Social",
            Category = ClubEventCategory.Activities,
            IsAllDay = false,
            Start = new DateTime(2026, 8, 21, 18, 0, 0),
            End = new DateTime(2026, 8, 22, 0, 0, 0)
        };

        var draft = new ClubEventDraft();
        draft.LoadForEdit(clubEvent);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(clubEvent.End, draft.End);
        Assert.Equal(clubEvent.End, draft.ToClubEvent("staff@example.com").End);
    }

    [Fact]
    public void LoadForEdit_SameDayTimedEvent_StillRoundTripsThroughDraftFields()
    {
        // Sanity check that the fix (computing EndMinutes relative to End's own date) doesn't regress
        // the common same-day case, which happened to work before only because Start.Date and
        // End.Date were the same value.
        var clubEvent = new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            IsAllDay = false,
            Start = new DateTime(2026, 8, 21, 9, 0, 0),
            End = new DateTime(2026, 8, 21, 17, 0, 0)
        };

        var draft = new ClubEventDraft();
        draft.LoadForEdit(clubEvent);

        Assert.Contains(draft.EndMinutes, CalendarStyles.TimeOptionsMinutes);
        Assert.Equal(clubEvent.End, draft.End);
        Assert.Equal(clubEvent.End, draft.ToClubEvent("staff@example.com").End);
    }
}
