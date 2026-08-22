using Bunit;
using Microsoft.Extensions.DependencyInjection;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain.Search;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Components;

public class EventSearchTests : BunitContext
{
    /// <summary>Counts calendarView reads specifically - the expensive, per-sheet-fan-out call a
    /// wide search range triggers. The other IGraphEventGateway members just delegate untouched;
    /// nothing under test here calls them.</summary>
    private sealed class CountingGateway(IGraphEventGateway inner) : IGraphEventGateway
    {
        public int CalendarViewCalls { get; private set; }

        public Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
            IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
        {
            CalendarViewCalls++;
            return inner.GetCalendarViewAsync(mailbox, startUtc, endUtc, expand, extraHeaders, ct);
        }

        public Task<Event?> GetEventAsync(string mailbox, string eventId, string[]? expand = null, CancellationToken ct = default) =>
            inner.GetEventAsync(mailbox, eventId, expand, ct);

        public Task<List<Event>> FindEventsAsync(string mailbox, string filter, string[] expand, CancellationToken ct = default) =>
            inner.FindEventsAsync(mailbox, filter, expand, ct);

        public Task<Event?> CreateEventAsync(string mailbox, Event graphEvent, CancellationToken ct = default) =>
            inner.CreateEventAsync(mailbox, graphEvent, ct);

        public Task PatchEventAsync(string mailbox, string eventId, Event patch, CancellationToken ct = default) =>
            inner.PatchEventAsync(mailbox, eventId, patch, ct);

        public Task DeleteEventAsync(string mailbox, string eventId, CancellationToken ct = default) =>
            inner.DeleteEventAsync(mailbox, eventId, ct);

        public Task<List<Event>> GetInstancesAsync(string mailbox, string eventId, string startUtc, string endUtc, CancellationToken ct = default) =>
            inner.GetInstancesAsync(mailbox, eventId, startUtc, endUtc, ct);
    }

    private CountingGateway RegisterServices()
    {
        var facility = TestFacility.Create();
        var gateway = new CountingGateway(new FakeGraphEventGateway(facility.ZoneInfo));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var bookingService = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        var clubEventService = new ClubEventService(gateway, cache, facility, appLog, viewCache);

        Services.AddSingleton(facility);
        Services.AddSingleton(bookingService);
        Services.AddSingleton(clubEventService);
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

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
    public void Render_OnInitialLoad_ShowsTheDefaultRangeCapHint()
    {
        RegisterServices();

        var cut = Render<EventSearch>();

        Assert.Contains($"Searches up to {SearchRange.MaxSpanDays} days at a time.", cut.Markup);
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
