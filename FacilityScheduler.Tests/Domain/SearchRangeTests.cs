using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

public class SearchRangeTests
{
    private static readonly DateTime Today = new(2026, 8, 21);

    [Fact]
    public void Resolve_BothNull_DefaultsToThirtyDaysBackThroughThreeSixtySixForward()
    {
        var (start, end, warning) = SearchRange.Resolve(null, null, Today);

        Assert.Equal(Today.AddDays(-30), start);
        Assert.Equal(Today.AddDays(366), end);
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
    public void Resolve_SpanWiderThanFourHundredDays_ClampsEndAndNamesBothDatesInWarning()
    {
        var requestedStart = new DateTime(2026, 9, 1);
        var requestedEnd = requestedStart.AddDays(500);

        var (start, end, warning) = SearchRange.Resolve(requestedStart, requestedEnd, Today);

        Assert.Equal(requestedStart, start);
        Assert.Equal(requestedStart.AddDays(400), end);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Resolve_ExactlyFourHundredDaySpan_NoWarning()
    {
        var requestedStart = new DateTime(2026, 9, 1);
        var requestedEnd = requestedStart.AddDays(400);

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
        // Start deliberately close enough to the 2-year bound that the 400-day span cap doesn't
        // also kick in and mask which clamp actually produced the warning - this isolates the
        // absolute-bound clamp specifically.
        var start = Today.AddDays(400);

        var (_, end, warning) = SearchRange.Resolve(start, new DateTime(2099, 1, 1), Today);

        Assert.Equal(Today.AddYears(2), end);
        Assert.NotNull(warning);
    }
}
