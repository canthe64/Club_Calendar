using AngleSharp.Dom;
using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The "+ New Event" dropdown's dismiss behaviour. Reported 2026-08-27: opening it and then clicking
/// anywhere else left it open, and the only way to close it was to hit the button a second time.
/// It had no outside-click handling at all - MainLayout's header menu had solved this with a
/// full-viewport transparent backdrop, and this menu never picked the pattern up.
/// </summary>
public class CalendarNewMenuTests : BunitContext
{
    private IRenderedComponent<Calendar> RenderCalendar()
    {
        StaffPageServices.Register(this);
        return Render<Calendar>();
    }

    private static IElement NewEventButton(IRenderedComponent<Calendar> cut) =>
        cut.FindAll("span").First(el => el.TextContent.Contains("+ New Event"));

    /// <summary>The dismiss backdrop: fixed, full-viewport, and behind the panel in z-order.</summary>
    private static IElement? Backdrop(IRenderedComponent<Calendar> cut) =>
        cut.FindAll("div").FirstOrDefault(el =>
        {
            var style = el.GetAttribute("style") ?? "";
            return style.Contains("position:fixed") && style.Contains("inset:0");
        });

    private static bool MenuIsOpen(IRenderedComponent<Calendar> cut) =>
        cut.Markup.Contains("New Off-Ice Event");

    [Fact]
    public void MenuStartsClosed()
    {
        var cut = RenderCalendar();

        Assert.False(MenuIsOpen(cut));
        Assert.Null(Backdrop(cut));
    }

    [Fact]
    public void OpeningTheMenu_AlsoRendersTheDismissBackdrop()
    {
        var cut = RenderCalendar();

        NewEventButton(cut).Click();

        Assert.True(MenuIsOpen(cut));
        Assert.NotNull(Backdrop(cut));
    }

    [Fact]
    public void ClickingTheBackdrop_ClosesTheMenu()
    {
        // The actual bug: this is what a click anywhere else on the page lands on.
        var cut = RenderCalendar();
        NewEventButton(cut).Click();

        Backdrop(cut)!.Click();

        Assert.False(MenuIsOpen(cut));
        Assert.Null(Backdrop(cut));
    }

    [Fact]
    public void TheBackdropSitsBehindTheMenuPanel()
    {
        // If the backdrop were on top of the panel, dismissing would work but choosing an item
        // never would - the backdrop would swallow that click too.
        var cut = RenderCalendar();
        NewEventButton(cut).Click();

        var backdropZ = ZIndexOf(Backdrop(cut)!);
        var panelZ = ZIndexOf(cut.FindAll("div").First(el =>
            (el.GetAttribute("style") ?? "").Contains("position:absolute;top:100%")));

        Assert.True(backdropZ < panelZ, $"backdrop z-index {backdropZ} must sit below the panel's {panelZ}");
    }

    [Fact]
    public void ChoosingAnItem_StillClosesTheMenu()
    {
        var cut = RenderCalendar();
        NewEventButton(cut).Click();

        cut.FindAll("div").First(el => el.TextContent.Trim() == "New Off-Ice Event").Click();

        Assert.False(MenuIsOpen(cut));
    }

    private static int ZIndexOf(IElement el)
    {
        var style = el.GetAttribute("style") ?? "";
        return int.Parse(style.Split("z-index:")[1].Split(';')[0].Trim());
    }
}
