using FacilityScheduler.Components.Pages;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// Calendar.ApplySeasonToCandidates - identifies which series-wizard candidate dates fall outside
/// the configured season and summarizes it, without ever touching the First/Last dates staff
/// actually typed. Live bug (2026-08-18): an earlier version clipped FirstDate/LastDate in place,
/// which silently rewrote the picker's displayed end date while also showing a warning that dates
/// were "removed" - two contradictory signals for the same edit staff never made. This version only
/// ever reports which of the given candidates are out of season; the caller is responsible for
/// excluding them from creation (Calendar.razor folds them into Draft.SkippedDates, non-toggleable)
/// without ever writing to Draft.FirstDate/LastDate. internal static, reached via
/// InternalsVisibleTo, so this is testable directly rather than only through a full render (same
/// reasoning as PublicCalendarEndpoint.ResolveMonthAnchor).
/// </summary>
public class SeriesSeasonClipTests
{
    private static List<DateTime> WeeklyDates(DateTime first, DateTime last)
    {
        var dates = new List<DateTime>();
        for (var d = first.Date; d <= last.Date; d = d.AddDays(7))
        {
            dates.Add(d);
        }
        return dates;
    }

    [Fact]
    public void NoSeasonConfigured_NothingExcluded_NoWarning()
    {
        var candidates = WeeklyDates(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, null, null);

        Assert.Empty(excluded);
        Assert.Null(warning);
    }

    [Fact]
    public void EveryCandidateInsideTheSeason_NothingExcluded_NoWarning()
    {
        var candidates = WeeklyDates(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, new DateTime(2026, 10, 15), new DateTime(2027, 3, 15));

        Assert.Empty(excluded);
        Assert.Null(warning);
    }

    [Fact]
    public void CandidatesRunPastSeasonEnd_OnlyTheLateOnesAreExcluded()
    {
        var seasonEnd = new DateTime(2027, 3, 1);
        // Weekly from Feb 15 -> Mar 15: Feb 15, Feb 22, Mar 1 (in season), Mar 8, Mar 15 (past end).
        var candidates = WeeklyDates(new DateTime(2027, 2, 15), new DateTime(2027, 3, 15));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, new DateTime(2026, 10, 15), seasonEnd);

        Assert.Equal(2, excluded.Count);
        Assert.Contains(new DateTime(2027, 3, 8), excluded);
        Assert.Contains(new DateTime(2027, 3, 15), excluded);
        Assert.DoesNotContain(new DateTime(2027, 3, 1), excluded); // exactly on the end date - still in season
        Assert.Contains("2 dates", warning);
        Assert.Contains("Mar 1, 2027", warning);
    }

    [Fact]
    public void CandidatesStartBeforeSeasonStart_OnlyTheEarlyOnesAreExcluded()
    {
        var seasonStart = new DateTime(2026, 10, 15);
        var candidates = WeeklyDates(new DateTime(2026, 10, 1), new DateTime(2026, 10, 29));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, seasonStart, new DateTime(2027, 3, 15));

        Assert.Equal(2, excluded.Count); // Oct 1 and Oct 8
        Assert.Contains(new DateTime(2026, 10, 1), excluded);
        Assert.Contains(new DateTime(2026, 10, 8), excluded);
        Assert.DoesNotContain(new DateTime(2026, 10, 15), excluded); // exactly on start - in season
        Assert.Contains("2 dates", warning);
    }

    [Fact]
    public void SingleDateExcluded_MessageIsSingularNotPlural()
    {
        var candidates = WeeklyDates(new DateTime(2026, 12, 20), new DateTime(2027, 1, 3));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, new DateTime(2026, 10, 15), new DateTime(2026, 12, 27));

        Assert.Single(excluded);
        Assert.Contains("1 date ", warning); // "1 date below", not "1 dates below"
    }

    [Fact]
    public void EveryCandidateOutsideTheSeason_AllExcluded()
    {
        var candidates = WeeklyDates(new DateTime(2026, 4, 1), new DateTime(2026, 4, 29));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, new DateTime(2026, 10, 15), new DateTime(2027, 3, 15));

        Assert.Equal(candidates.Count, excluded.Count);
        Assert.NotNull(warning);
    }

    [Fact]
    public void OnlySeasonStartConfigured_OnlyThatBoundApplies()
    {
        var candidates = WeeklyDates(new DateTime(2026, 10, 1), new DateTime(2099, 1, 1));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, new DateTime(2026, 10, 15), null);

        Assert.DoesNotContain(candidates[^1], excluded); // far future date - no end bound, never excluded
        Assert.Contains(new DateTime(2026, 10, 1), excluded);
        Assert.Contains("starts", warning);
    }

    [Fact]
    public void OnlySeasonEndConfigured_OnlyThatBoundApplies()
    {
        var candidates = WeeklyDates(new DateTime(2000, 1, 1), new DateTime(2000, 1, 29));

        var (excluded, warning) = Calendar.ApplySeasonToCandidates(candidates, null, new DateTime(2026, 3, 15));

        // No start bound configured - every one of these far-past dates is still "in season".
        Assert.Empty(excluded);
        Assert.Null(warning);
    }

    [Fact]
    public void EmptyCandidateList_NothingExcluded_NoWarning()
    {
        var (excluded, warning) = Calendar.ApplySeasonToCandidates([], new DateTime(2026, 10, 15), new DateTime(2027, 3, 15));

        Assert.Empty(excluded);
        Assert.Null(warning);
    }
}
