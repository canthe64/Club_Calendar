using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class PublicSearchEndpointTests
{
    private static readonly DateTime Today = new(2026, 8, 3);

    [Fact]
    public void ParseRange_NoInput_DefaultsToTodayThroughSevenDaysOut()
    {
        var (start, end) = InvokeParseRange(null, null, Today);
        Assert.Equal(Today, start);
        Assert.Equal(Today.AddDays(7), end);
    }

    [Fact]
    public void ParseRange_ValidExplicitRange_IsUsedAsGiven()
    {
        var (start, end) = InvokeParseRange("2026-09-01", "2026-09-10", Today);
        Assert.Equal(new DateTime(2026, 9, 1), start);
        Assert.Equal(new DateTime(2026, 9, 10), end);
    }

    [Fact]
    public void ParseRange_EndBeforeStart_ClampsEndToStart()
    {
        var (start, end) = InvokeParseRange("2026-09-10", "2026-09-01", Today);
        Assert.Equal(start, end);
    }

    [Fact]
    public void ParseRange_SpanWiderThanSixtyDays_ClampsToSixtyDays()
    {
        var (start, end) = InvokeParseRange("2026-09-01", "2027-01-01", Today);
        Assert.Equal(start.AddDays(60), end);
    }

    [Fact]
    public void ParseRange_StartFarInThePast_ClampsToOneYearBack()
    {
        var (start, _) = InvokeParseRange("2001-01-01", null, Today);
        Assert.Equal(Today.AddYears(-1), start);
    }

    [Fact]
    public void ParseRange_StartFarInTheFuture_ClampsToTwoYearsForward()
    {
        var (start, _) = InvokeParseRange("2099-01-01", null, Today);
        Assert.Equal(Today.AddYears(2), start);
    }

    private static (DateTime Start, DateTime End) InvokeParseRange(string? start, string? end, DateTime today) =>
        PublicSearchEndpoint.ParseRange(start, end, today);

    // Operator request, 2026-09-03: a sentence pointing visitors at curlingseattle.org/group-events
    // to actually confirm/inquire, placed between the search form and its results - added because the
    // search only ever tells someone a window LOOKS open, not that the club can actually deliver it.
    [Fact]
    public void RenderPage_ShowsTheGroupEventsContactSentence_BetweenTheFormAndTheResults()
    {
        var html = PublicSearchEndpoint.RenderPage(Today, Today.AddDays(7), minSheets: 1, maxSheets: 3, windows: []);

        var formEnd = html.IndexOf("</form>", StringComparison.Ordinal);
        var sentenceStart = html.IndexOf("To confirm availability and inquire about a group event", StringComparison.Ordinal);
        var resultsStart = html.IndexOf("No windows found", StringComparison.Ordinal);

        Assert.True(formEnd >= 0 && sentenceStart >= 0 && resultsStart >= 0);
        Assert.True(formEnd < sentenceStart, "the sentence must come after the search form");
        Assert.True(sentenceStart < resultsStart, "the sentence must come before the results");
        Assert.Contains("""<a href="https://curlingseattle.org/group-events" target="_blank" rel="noopener" """, html);
    }
}
