using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Services.Graph;
using FacilityScheduler.Tests.TestSupport;
using Event = Microsoft.Graph.Models.Event;

namespace FacilityScheduler.Tests.Components;

/// <summary>First render coverage for Calendar.razor. It had none before the unified-event refactor,
/// which rewrites this page substantially - these are the smoke tests that make that rewrite visible
/// if it breaks the page's basic structure, not a full behavioral suite.</summary>
public class CalendarRenderTests : BunitContext
{
    /// <summary>Throws on GetCalendarViewAsync only when armed - lets a test render successfully once,
    /// then simulate a Graph failure on a *later* load (a Today/Prev/Next click) without fighting
    /// bUnit's handling of a first-render exception.</summary>
    private sealed class ToggleableThrowGateway(IGraphEventGateway inner) : DelegatingGraphEventGateway(inner)
    {
        public bool ShouldThrow { get; set; }

        public override Task<List<Event>> GetCalendarViewAsync(string mailbox, string startUtc, string endUtc, string[] expand,
            IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default) =>
            ShouldThrow
                ? throw new InvalidOperationException("simulated Graph failure")
                : base.GetCalendarViewAsync(mailbox, startUtc, endUtc, expand, extraHeaders, ct);
    }

    [Fact]
    public void LoadAsync_GraphFailureMidLoad_DoesNotLeaveTheLoadingStateStuckTrue()
    {
        // Code review O4: LoadAsync previously set _loading=true/false with nothing guaranteeing the
        // false half ran on a Graph failure - the page would be stuck showing "Loading..." forever
        // instead of falling back to whatever was already on screen (the same try/finally pattern
        // SaveOnIceEvent already used for its own _isSaving flag).
        ToggleableThrowGateway gateway = null!;
        StaffPageServices.Register(this, fake => gateway = new ToggleableThrowGateway(fake));

        var cut = Render<Calendar>();
        Assert.DoesNotContain("Loading...", cut.Markup); // settled after the successful initial load

        gateway.ShouldThrow = true;
        // GoNext, not GoToday/GoPrevious - GetBookingsForAllSheetsAsync's own 30-second view cache
        // (keyed by exact start/end) means re-requesting the *same* window LoadAsync already just
        // loaded successfully would be served from cache and never reach the gateway at all, hiding
        // the failure path entirely. Navigating to a new month is a genuinely different cache key.
        var next = cut.FindAll("span").Single(e => e.TextContent.Trim() == "›");
        // bUnit re-throws the handler's exception from Click() itself (there's no ErrorBoundary
        // wrapping the bare component under test the way MainLayout wraps it in the real app) - what
        // this test actually pins is that LoadAsync's finally block still runs before that exception
        // gets here, so _loading isn't left stuck true regardless of what ultimately catches it.
        Assert.Throws<InvalidOperationException>(() => next.Click());

        Assert.DoesNotContain("Loading...", cut.Markup);
    }

    [Fact]
    public void Render_ShowsTheToolbar()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        // The filter row is CalendarFilterTests' concern.
        var markup = cut.Markup;
        Assert.Contains("Today", markup);
        Assert.Contains("Month", markup);
        Assert.Contains("Week", markup);
        Assert.Contains("Day", markup);
    }

    [Fact]
    public void Render_ShowsTheNewMenuButtonAndTheOffIceEventsLink()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        Assert.Contains("New", cut.Markup);
        Assert.Contains("/club-events", cut.Markup);
        Assert.Contains("/search", cut.Markup);
    }
}
