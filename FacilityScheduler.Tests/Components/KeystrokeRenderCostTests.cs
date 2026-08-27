using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// What a single keystroke costs in the two free-text controls staff actually type into. This is a
/// Blazor Server app, so every bound DOM event is a SignalR round trip and every render is server
/// work - a staff member on a high-latency connection reported both controls as laggy (2026-08-27).
///
/// Measured before the fix: the query box cost 3 renders per character (onkeydown fired two, because
/// an `async Task` handler yields even when it does nothing, plus one for oninput), each re-rendering
/// the whole results list up to MaxRenderedRows = 300. The event title cost 1 render of the entire
/// dialog, 58 &lt;option&gt; elements included.
///
/// These pin the render count itself rather than any timing, so they're deterministic - a regression
/// here is someone reintroducing a render, not a slow machine.
/// </summary>
public class KeystrokeRenderCostTests : BunitContext
{
    // ---- Search query box ------------------------------------------------------------------

    private IRenderedComponent<EventSearch> RenderSearch()
    {
        StaffPageServices.Register(this);
        return Render<EventSearch>();
    }

    // Re-found on each use rather than cached: a render replaces the element, and a stale reference
    // would dispatch into a detached node.
    private static AngleSharp.Dom.IElement QueryBox(IRenderedComponent<EventSearch> cut) =>
        cut.Find("input[placeholder]");

    [Fact]
    public void TypingInTheQueryBox_CostsNoRenders()
    {
        var cut = RenderSearch();
        var before = cut.RenderCount;

        // One character as the browser delivers it: keydown, then input.
        QueryBox(cut).KeyDown("j");
        QueryBox(cut).Input("j");

        Assert.Equal(before, cut.RenderCount);
    }

    [Fact]
    public void TypingStillUpdatesTheQueryTheSearchWillRun()
    {
        // Suppressing the render must not suppress the state change - the whole point of the
        // oninput binding is that RunSearch has the text when Enter is finally pressed.
        var cut = RenderSearch();

        QueryBox(cut).Input("junior");
        QueryBox(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // The results header echoes the range a search actually ran over; reaching it at all means
        // Enter got through and ran with the typed query rather than an empty one.
        Assert.DoesNotContain("Search syntax", cut.Markup);
    }

    [Fact]
    public void PressingEnter_StillRenders()
    {
        var cut = RenderSearch();
        var before = cut.RenderCount;

        QueryBox(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.True(cut.RenderCount > before, "Enter must still re-render - it runs the search");
    }

    // ---- Event dialog title ----------------------------------------------------------------

    private IRenderedComponent<EventFormModal> RenderModal(out EventDraft draft)
    {
        StaffPageServices.Register(this);
        var d = new EventDraft();
        d.ResetForCreate(EventMode.OnIce, new DateTime(2026, 8, 27));
        draft = d;
        return Render<EventFormModal>(p => p.Add(m => m.IsOpen, true).Add(m => m.Draft, d));
    }

    private static AngleSharp.Dom.IElement TitleInput(IRenderedComponent<EventFormModal> cut) =>
        cut.FindAll("input:not([type])")[0];

    [Fact]
    public void TypingIntoTheTitle_RendersOnlyWhenValidationFlips()
    {
        var cut = RenderModal(out _);

        // First character: the title goes from empty to non-empty, so the Save button's state
        // genuinely changes and the dialog must re-render.
        var beforeFirst = cut.RenderCount;
        TitleInput(cut).Input("A");
        Assert.True(cut.RenderCount > beforeFirst, "the first character changes CanSave and must render");

        // Every character after that changes nothing on screen.
        var beforeRest = cut.RenderCount;
        TitleInput(cut).Input("Anthe corporate bonspiel");
        Assert.Equal(beforeRest, cut.RenderCount);
    }

    [Fact]
    public void TypingIntoNotes_NeverRenders()
    {
        // Notes aren't validated at all, so no keystroke in it can change what's displayed.
        var cut = RenderModal(out _);
        TitleInput(cut).Input("Titled");

        var notes = cut.FindAll("input:not([type])")[^1];
        var before = cut.RenderCount;
        notes.Input("rocks out by 6:10");

        Assert.Equal(before, cut.RenderCount);
    }

    [Fact]
    public void TypingStillReachesTheDraft()
    {
        var cut = RenderModal(out var draft);

        TitleInput(cut).Input("Anthe corporate bonspiel");

        Assert.Equal("Anthe corporate bonspiel", draft.OnIce.RenterName);
    }

    [Fact]
    public void ANonTextInteraction_StillRenders()
    {
        // The suppression is scoped to the free-text handlers; everything else must be unaffected.
        var cut = RenderModal(out _);
        TitleInput(cut).Input("Titled");

        var before = cut.RenderCount;
        cut.FindAll("span").First(s => s.TextContent.Trim() == "League").Click();

        Assert.True(cut.RenderCount > before, "clicking a category chip must still re-render");
    }
}
