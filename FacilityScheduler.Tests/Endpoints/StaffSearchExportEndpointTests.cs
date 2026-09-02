using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class StaffSearchExportEndpointTests
{
    [Fact]
    public void ParseDate_ValidString_ReturnsIt()
    {
        Assert.Equal(new DateTime(2026, 9, 1), StaffSearchExportEndpoint.ParseDate("2026-09-01"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void ParseDate_MissingOrUnparsable_ReturnsNull(string? value)
    {
        Assert.Null(StaffSearchExportEndpoint.ParseDate(value));
    }

    private static readonly DateTime Today = new(2026, 9, 2);

    [Fact]
    public void ResolveExportRange_SeasonRequested_ButNotConfigured_ReturnsAnError()
    {
        var result = StaffSearchExportEndpoint.ResolveExportRange(
            season: true, start: null, end: null, seasonStart: null, seasonEnd: null, today: Today);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ResolveExportRange_SeasonRequested_OnlyHalfConfigured_ReturnsAnError()
    {
        // Mirrors EventSearch.razor's own SeasonNotConfigured - a search needs a closed window on
        // both sides, unlike the Settings page's own Booking Season toggle where either half being
        // blank is a legitimate "unrestricted on that side" state.
        var result = StaffSearchExportEndpoint.ResolveExportRange(
            season: true, start: null, end: null, seasonStart: new DateTime(2026, 10, 1), seasonEnd: null, today: Today);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ResolveExportRange_SeasonRequested_AndConfigured_UsesTheSeasonDates_IgnoringStartEnd()
    {
        var seasonStart = new DateTime(2026, 10, 1);
        var seasonEnd = new DateTime(2027, 3, 31);

        var result = StaffSearchExportEndpoint.ResolveExportRange(
            season: true, start: new DateTime(2026, 1, 1), end: new DateTime(2026, 1, 2), // would-be query string, ignored
            seasonStart: seasonStart, seasonEnd: seasonEnd, today: Today);

        Assert.Null(result.Error);
        Assert.Equal(seasonStart, result.Start);
        Assert.Equal(seasonEnd, result.End);
    }

    [Fact]
    public void ResolveExportRange_SeasonNotRequested_UsesTheExplicitStartEnd_EvenWithASeasonConfigured()
    {
        var start = new DateTime(2026, 6, 1);
        var end = new DateTime(2026, 6, 10);

        var result = StaffSearchExportEndpoint.ResolveExportRange(
            season: false, start: start, end: end,
            seasonStart: new DateTime(2026, 10, 1), seasonEnd: new DateTime(2027, 3, 31), today: Today);

        Assert.Null(result.Error);
        Assert.Equal(start, result.Start);
        Assert.Equal(end, result.End);
    }
}
