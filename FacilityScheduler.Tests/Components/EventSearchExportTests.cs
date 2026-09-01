using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The "Export CSV" link on the search page. It's a plain &lt;a&gt;, not an @onclick handler - a file
/// download has to be a real HTTP response, so routing it through the SignalR circuit would be the
/// wrong tool (same reasoning as SettingsLogsEndpoint's log download). These tests cover what the
/// page controls: whether the link exists and where it points; StaffSearchExportEndpointTests and
/// SearchResultsCsvTests cover what's actually behind that URL.
/// </summary>
public class EventSearchExportTests : BunitContext
{
    private async Task<IRenderedComponent<EventSearch>> RenderSearchWithOneLeagueBookingAsync()
    {
        var registered = StaffPageServices.Register(this);
        var day = registered.Facility.Today.AddDays(1);
        await registered.BookingService.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = day.AddHours(18),
            End = day.AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League"
        }, "tester");

        return Render<EventSearch>();
    }

    private IRenderedComponent<EventSearch> RenderSearch()
    {
        StaffPageServices.Register(this);
        return Render<EventSearch>();
    }

    private static string? ExportLinkHref(IRenderedComponent<EventSearch> cut) =>
        cut.FindAll("a").FirstOrDefault(a => a.TextContent.Contains("Export CSV"))?.GetAttribute("href");

    [Fact]
    public void BeforeAnySearch_NoExportLinkExists()
    {
        var cut = RenderSearch();

        Assert.Null(ExportLinkHref(cut));
    }

    [Fact]
    public async Task AfterASearchWithResults_TheExportLinkAppears()
    {
        var cut = await RenderSearchWithOneLeagueBookingAsync();
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        Assert.NotNull(ExportLinkHref(cut));
    }

    [Fact]
    public async Task ASearchWithNoResults_ShowsNoExportLink()
    {
        // Nothing to export - offering the link would just produce a header-only file.
        var cut = RenderSearch();
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league day:monday title-that-matches-nothing-at-all-xyz");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        Assert.Null(ExportLinkHref(cut));
    }

    [Fact]
    public async Task TheExportLink_CarriesTheSearchedQueryAndResolvedRange()
    {
        var cut = await RenderSearchWithOneLeagueBookingAsync();
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        var href = ExportLinkHref(cut);

        Assert.StartsWith("/search/export.csv?", href);
        Assert.Contains("q=category%3Aleague", href);
        Assert.Contains("start=", href);
        Assert.Contains("end=", href);
    }

    [Fact]
    public async Task TheExportLink_DoesNotFollowUntypedTextInTheBox()
    {
        // It must reflect what's actually on screen (the last RESOLVED search), not whatever's been
        // typed since without pressing Search/Enter - a link that silently changed target as you
        // typed, disconnected from what you're looking at, would be its own kind of confusing bug.
        var cut = await RenderSearchWithOneLeagueBookingAsync();
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        queryInput.Input("something else entirely");

        var href = ExportLinkHref(cut);
        Assert.Contains("q=category%3Aleague", href);
    }
}
