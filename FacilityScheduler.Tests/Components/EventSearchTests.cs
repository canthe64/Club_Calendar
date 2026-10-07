using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain.Search;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;

namespace FacilityScheduler.Tests.Components;

public class EventSearchTests : BunitContext
{
    private CountingGraphEventGateway RegisterServices()
    {
        CountingGraphEventGateway gateway = null!;
        StaffPageServices.Register(this, fake => gateway = new CountingGraphEventGateway(fake));
        return gateway;
    }

    [Fact]
    public void Render_OnInitialLoad_IssuesNoGatewayReads()
    {
        // The cost-protection invariant this page exists to preserve: fanning out across every
        // sheet on plain navigation (rather than an explicit search) would charge that cost to
        // every stray menu click, regardless of how wide the configured range is.
        var gateway = RegisterServices();

        Render<EventSearch>();

        Assert.Equal(0, gateway.CalendarViewCalls);
    }

    [Fact]
    public void Render_OnInitialLoad_ShowsTheSyntaxHelpCardNotResults()
    {
        RegisterServices();

        var cut = Render<EventSearch>();

        Assert.Contains("Search syntax", cut.Markup);
    }

    [Fact]
    public void SelectingRangeWiderThanCap_BlocksSearchWithoutFetchingAndShowsWhy()
    {
        // The bug this guards: staff could previously click Search on a too-wide range, wait, and
        // only then see a vague "end date wasn't reached" message. The range must now block Search
        // entirely, with a specific reason, the moment an out-of-bounds range is picked.
        var gateway = RegisterServices();
        var cut = Render<EventSearch>();

        var dateInputs = cut.FindAll("input[type=date]");
        dateInputs[0].Change("2026-01-01");
        dateInputs[1].Change("2026-06-01"); // ~150 days, well past the 60-day cap

        Assert.Contains("narrow it to", cut.Markup);

        var searchButton = cut.FindAll("span").Single(s => s.TextContent.Trim() == "Search");
        searchButton.Click();

        Assert.Equal(0, gateway.CalendarViewCalls);
    }

    [Fact]
    public void SelectingEndBeforeStart_BlocksSearchWithoutFetchingAndShowsWhy()
    {
        var gateway = RegisterServices();
        var cut = Render<EventSearch>();

        var dateInputs = cut.FindAll("input[type=date]");
        dateInputs[0].Change("2026-06-10");
        dateInputs[1].Change("2026-06-01");

        Assert.Contains("End date is before start date", cut.Markup);

        var searchButton = cut.FindAll("span").Single(s => s.TextContent.Trim() == "Search");
        searchButton.Click();

        Assert.Equal(0, gateway.CalendarViewCalls);
    }

    // SearchViewState is internal - InternalsVisibleTo lets this assembly reference it, but xUnit's
    // [Theory]/[InlineData] can't put an internal type in a public method signature (CS0051), so the
    // expected value travels as a string and is compared via ToString(), same workaround used
    // elsewhere in this test suite (e.g. SearchQueryParserTests for SearchKindFilter).
    [Theory]
    [InlineData(false, false, "Idle")]
    [InlineData(false, true, "Results")]
    // The regression case: mid-search, before the very first search has ever completed. The
    // spinner used to be unreachable here because the markup nested it inside a branch gated on
    // hasSearched, which is exactly the state during a page's very first search.
    [InlineData(true, false, "Searching")]
    [InlineData(true, true, "Searching")]
    public void ResolveViewState_ReturnsTheExpectedViewForEveryCombination(bool isSearching, bool hasSearched, string expected)
    {
        Assert.Equal(expected, EventSearch.ResolveViewState(isSearching, hasSearched).ToString());
    }

    [Fact]
    public async Task PressingEnterInTheSearchBox_RunsTheSearchAndShowsResults()
    {
        // Regression guard for a live-found bug: the Enter-key path used to fire-and-forget
        // RunSearch (`_ = RunSearch();`) from a synchronous handler, so Blazor's own "await the
        // handler, then re-render" never covered the search's completion - results were computed
        // correctly but the final render was never triggered. OnQueryKeyDown now awaits RunSearch
        // directly, same as the Search button's own @onclick binding already did.
        var gateway = RegisterServices();
        var cut = Render<EventSearch>();

        var queryInput = cut.FindAll("input")[0]; // markup order: query text box, then Start date, End date
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        Assert.True(gateway.CalendarViewCalls > 0);
        Assert.Contains("result", cut.Markup);
    }
}
