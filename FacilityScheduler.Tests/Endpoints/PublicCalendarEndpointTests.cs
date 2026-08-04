using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class PublicCalendarEndpointTests
{
    private static readonly DateTime Today = new(2026, 8, 3);

    // ViewMode is internal, so it can't appear in a public [Theory] method's signature (even with
    // InternalsVisibleTo, a public member still can't expose a less-accessible type) - compared via
    // ToString() instead of taking ViewMode itself as a Theory parameter.
    [Theory]
    [InlineData("week", "Week")]
    [InlineData("WEEK", "Week")]
    [InlineData("day", "Day")]
    [InlineData(null, "Month")]
    [InlineData("nonsense", "Month")]
    public void ParseView_MapsKnownValuesCaseInsensitively_DefaultsToMonth(string? view, string expected)
    {
        Assert.Equal(expected, PublicCalendarEndpoint.ParseView(view).ToString());
    }

    [Fact]
    public void ParseMonth_MissingOrUnparsable_ReturnsNull()
    {
        Assert.Null(PublicCalendarEndpoint.ParseMonth(null, Today));
        Assert.Null(PublicCalendarEndpoint.ParseMonth("not-a-month", Today));
    }

    [Fact]
    public void ParseMonth_WithinWindow_ReturnsRequestedMonth()
    {
        var result = PublicCalendarEndpoint.ParseMonth("2026-09", Today);
        Assert.Equal(new DateTime(2026, 9, 1), result);
    }

    [Fact]
    public void ParseMonth_FarInThePast_ClampsToOneYearBack()
    {
        // Anonymous, unauthenticated surface - an unclamped month is a cache-growth/Graph-quota
        // vector (every distinct month is a live fan-out across every mailbox and a new
        // never-evicted cache entry).
        var result = PublicCalendarEndpoint.ParseMonth("2001-01", Today);
        Assert.Equal(new DateTime(Today.Year - 1, Today.Month, 1), result);
    }

    [Fact]
    public void ParseMonth_FarInTheFuture_ClampsToTwoYearsForward()
    {
        var result = PublicCalendarEndpoint.ParseMonth("2099-01", Today);
        Assert.Equal(new DateTime(Today.Year + 2, Today.Month, 1), result);
    }

    [Fact]
    public void ParseDate_MissingOrUnparsable_ReturnsNull()
    {
        Assert.Null(PublicCalendarEndpoint.ParseDate(null, Today));
        Assert.Null(PublicCalendarEndpoint.ParseDate("garbage", Today));
    }

    [Fact]
    public void ParseDate_WithinWindow_ReturnsRequestedDate()
    {
        Assert.Equal(new DateTime(2026, 9, 15), PublicCalendarEndpoint.ParseDate("2026-09-15", Today));
    }

    [Fact]
    public void ParseDate_OutOfWindow_ClampsToNearestBound()
    {
        Assert.Equal(Today.AddYears(-1), PublicCalendarEndpoint.ParseDate("1999-01-01", Today));
        Assert.Equal(Today.AddYears(2), PublicCalendarEndpoint.ParseDate("2099-01-01", Today));
    }
}
