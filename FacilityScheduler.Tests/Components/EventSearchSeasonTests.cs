using AngleSharp.Dom;
using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// "Search entire season" (D111, operator request): a checkbox that bypasses SearchRange's 60-day cap
/// and searches the operator's configured Booking Season instead, for a report of every instance of
/// an event across the whole season - the thing a 60-day-at-a-time search can't produce without
/// stitching several searches together by hand.
/// </summary>
public class EventSearchSeasonTests : BunitContext
{
    private static IElement SeasonCheckbox(IRenderedComponent<EventSearch> cut) =>
        cut.Find("#search-entire-season");

    private async Task<ServiceHarness> RegisterWithSeasonAsync(DateTime start, DateTime end)
    {
        var registered = StaffPageServices.Register(this);
        await registered.Window.SetSeasonWindowAsync(start, end, "tester");
        return registered;
    }

    [Fact]
    public void NoSeasonConfigured_CheckboxIsDisabled_AndSaysSo()
    {
        StaffPageServices.Register(this);
        var cut = Render<EventSearch>();

        Assert.True(SeasonCheckbox(cut).HasAttribute("disabled"));
        Assert.Contains("no booking season is configured", cut.Markup);
    }

    [Fact]
    public async Task SeasonConfigured_CheckboxIsEnabled_AndShowsTheSeasonDatesAndALongSearchWarning()
    {
        await RegisterWithSeasonAsync(new DateTime(2026, 10, 1), new DateTime(2027, 3, 31));
        var cut = Render<EventSearch>();

        Assert.False(SeasonCheckbox(cut).HasAttribute("disabled"));
        Assert.Contains("Oct 1, 2026", cut.Markup);
        Assert.Contains("Mar 31, 2027", cut.Markup);
        // The user's own explicit ask: the checkbox's description must warn this can be slow.
        Assert.Contains("may take a long time", cut.Markup);
    }

    [Fact]
    public async Task CheckingTheBox_DisablesTheStartAndEndDateInputs()
    {
        await RegisterWithSeasonAsync(new DateTime(2026, 10, 1), new DateTime(2027, 3, 31));
        var cut = Render<EventSearch>();
        var dateInputs = cut.FindAll("input[type=date]");

        SeasonCheckbox(cut).Change(true);

        dateInputs = cut.FindAll("input[type=date]");
        Assert.True(dateInputs[0].HasAttribute("disabled"));
        Assert.True(dateInputs[1].HasAttribute("disabled"));
    }

    [Fact]
    public async Task CheckedWithNoSeasonConfigured_BlocksSearchWithoutFetching()
    {
        // Defensive path: the checkbox is disabled once no season exists, but if a season is cleared
        // out from under an already-checked box (a second browser tab, a stale render), Search must
        // still refuse to run rather than falling back to some other range silently.
        CountingGraphEventGateway gateway = null!;
        StaffPageServices.Register(this, fake => gateway = new CountingGraphEventGateway(fake));
        var cut = Render<EventSearch>();

        // The checkbox itself is disabled with no season configured (covered separately above), but
        // bUnit's synthetic Change() dispatches straight through Blazor's event pipeline regardless of
        // the rendered `disabled` attribute - unlike a real browser, nothing stops this call from
        // flipping _searchEntireSeason anyway. That's exactly what makes it a fair stand-in for the
        // scenario this guards: a season cleared out from under an already-checked box (a second
        // browser tab, a stale render) rather than one requiring browser-level input tampering.
        SeasonCheckbox(cut).Change(true);
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        var searchButton = cut.FindAll("span").Single(s => s.TextContent.Trim() == "Search");
        searchButton.Click();

        Assert.Equal(0, gateway.CalendarViewCalls);
    }

    [Fact]
    public async Task CheckedAndConfigured_SearchesTheFullSeason_BypassingTheSixtyDayCap()
    {
        var facility = TestFacility.Create();
        var seasonStart = facility.Today.AddDays(10);
        var seasonEnd = seasonStart.AddDays(150); // well past SearchRange.MaxSpanDays
        var registered = StaffPageServices.Register(this);
        await registered.Window.SetSeasonWindowAsync(seasonStart, seasonEnd, "tester");

        await registered.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = seasonEnd.AddDays(-5).AddHours(18),
            End = seasonEnd.AddDays(-5).AddHours(19),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Late-Season League"
        }, "tester");

        var cut = Render<EventSearch>();
        SeasonCheckbox(cut).Change(true);
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        // A booking 145 days out was found - only possible if the fetch actually covered the full
        // season span, not just the 60-day default window.
        Assert.Contains("Late-Season League", cut.Markup);
    }

    [Fact]
    public async Task ExportLink_WhenSeasonChecked_CarriesTheSeasonFlag_NotStartEnd()
    {
        var registered = await RegisterWithSeasonAsync(new DateTime(2026, 10, 1), new DateTime(2027, 3, 31));
        await registered.Bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0],
            Start = new DateTime(2026, 11, 1, 18, 0, 0),
            End = new DateTime(2026, 11, 1, 19, 0, 0),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "League"
        }, "tester");

        var cut = Render<EventSearch>();
        SeasonCheckbox(cut).Change(true);
        var queryInput = cut.FindAll("input")[0];
        queryInput.Input("category:league");
        await cut.InvokeAsync(() => queryInput.KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        var href = cut.FindAll("a").First(a => a.TextContent.Contains("Export CSV")).GetAttribute("href");

        Assert.Contains("season=1", href);
        Assert.DoesNotContain("start=", href);
        Assert.DoesNotContain("end=", href);
    }
}
