using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

public class SearchRangeTests
{
    private static readonly DateTime Today = new(2026, 8, 21);

    [Fact]
    public void Resolve_BothNull_DefaultsToFourteenDaysBackThroughFortySixForward()
    {
        var (start, end, warning) = SearchRange.Resolve(null, null, Today);

        Assert.Equal(Today.AddDays(-14), start);
        Assert.Equal(Today.AddDays(46), end);
        Assert.Null(warning);
    }

    [Fact]
    public void Resolve_InBoundsExplicitRange_IsUsedVerbatim()
    {
        var requestedStart = new DateTime(2026, 9, 1);
        var requestedEnd = new DateTime(2026, 9, 10);

        var (start, end, warning) = SearchRange.Resolve(requestedStart, requestedEnd, Today);

        Assert.Equal(requestedStart, start);
        Assert.Equal(requestedEnd, end);
        Assert.Null(warning);
    }

    [Fact]
    public void Resolve_SpanWiderThanSixtyDays_ClampsEndAndNamesBothDatesInWarning()
    {
        // 60 days matches PublicSearchEndpoint.MaxRangeDays - live-tested 2026-08-21: calendarView's
        // per-call cost scales with range width when recurring series are involved, not just with
        // round-trip count, so this cap is load-bearing, not arbitrary.
        var requestedStart = new DateTime(2026, 9, 1);
        var requestedEnd = requestedStart.AddDays(120);

        var (start, end, warning) = SearchRange.Resolve(requestedStart, requestedEnd, Today);

        Assert.Equal(requestedStart, start);
        Assert.Equal(requestedStart.AddDays(60), end);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Resolve_ExactlySixtyDaySpan_NoWarning()
    {
        var requestedStart = new DateTime(2026, 9, 1);
        var requestedEnd = requestedStart.AddDays(60);

        var (start, end, warning) = SearchRange.Resolve(requestedStart, requestedEnd, Today);

        Assert.Equal(requestedStart, start);
        Assert.Equal(requestedEnd, end);
        Assert.Null(warning);
    }

    [Fact]
    public void Resolve_EndBeforeStart_WarnsWithoutSwappingOrExpandingTheRange()
    {
        var requestedStart = new DateTime(2026, 9, 10);
        var requestedEnd = new DateTime(2026, 9, 1);

        var (start, end, warning) = SearchRange.Resolve(requestedStart, requestedEnd, Today);

        Assert.Equal(requestedStart, start);
        Assert.Equal(requestedStart, end);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Resolve_StartBeforeOneYearBack_ClampsWithWarning()
    {
        var (start, _, warning) = SearchRange.Resolve(new DateTime(2000, 1, 1), null, Today);

        Assert.Equal(Today.AddYears(-1), start);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Resolve_EndAfterTwoYearsForward_ClampsWithWarning()
    {
        // Start deliberately close enough to the 2-year bound that the 60-day span cap doesn't also
        // kick in and mask which clamp actually produced the warning - this isolates the
        // absolute-bound clamp specifically.
        var start = Today.AddYears(2).AddDays(-SearchRange.MaxSpanDays);

        var (_, end, warning) = SearchRange.Resolve(start, new DateTime(2099, 1, 1), Today);

        Assert.Equal(Today.AddYears(2), end);
        Assert.NotNull(warning);
    }
}
