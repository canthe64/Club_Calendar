using AngleSharp.Dom;
using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// "Search entire season" (D111, operator request): a checkbox that bypasses SearchRange's 60-day cap
/// and searches the operator's configured Booking Season instead, for a report of every instance of
/// an event across the whole season - the thing a 60-day-at-a-time search can't produce without
/// stitching several searches together by hand.
/// </summary>
public class EventSearchSeasonTests : BunitContext
{
    /// <summary>Counts calendarView reads - same narrow purpose as EventSearchTests' own private copy
    /// (not shared between the two files; each is small enough that duplicating it beats introducing
    /// a shared test-support type for one method).</summary>
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

    private static IElement SeasonCheckbox(IRenderedComponent<EventSearch> cut) =>
        cut.Find("#search-entire-season");

    private async Task<StaffPageServices.Registered> RegisterWithSeasonAsync(DateTime start, DateTime end)
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
        var facility = TestFacility.Create();
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
        var facility = TestFacility.Create();
        var gateway = new CountingGateway(new FakeGraphEventGateway(facility.ZoneInfo));
        StaffPageServices.Register(this, gateway);
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

        await registered.BookingService.CreateConfirmedAsync(new SheetBooking
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
        await registered.BookingService.CreateConfirmedAsync(new SheetBooking
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
