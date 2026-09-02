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

    // Live-found 2026-09-03: `season` used to be bound as `bool?`, whose framework TryParse-based
    // binding accepts only "true"/"false" - not "1", the literal value EventSearch.razor's ExportUrl
    // actually sends. A binding failure on a value that WAS present (not merely absent) returns 400
    // with an empty body regardless of nullability, which UseStatusCodePagesWithReExecute then turns
    // into NotFound.razor's "content cannot be found" page - indistinguishable from a real 404 to
    // whoever clicked Export CSV. "1" is the one value this regression test exists to pin.
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", true)] // presence-based, same convention as ParseFilter's filtered/showClubEvents -
                             // any non-empty value means "on", not a true/false comparison.
    public void ParseSeason_TreatsAnyNonEmptyValueAsRequested(string? value, bool expected)
    {
        Assert.Equal(expected, StaffSearchExportEndpoint.ParseSeason(value));
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
