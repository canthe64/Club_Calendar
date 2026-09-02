using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// Live-found 2026-09-01: the "+ New Event"/"New Off-Ice Event" dropdown items seeded a brand-new
/// event's Start AND End date from _anchorDate - whatever date happened to be in view, which can sit
/// weeks ahead of today after Prev/Next navigation or a date-jump. A staff member who then corrected
/// only Start (the date they actually meant to book) left End stranded on the far-future anchor date,
/// silently turning what should have been a one-day booking into a multi-day/week span that blocked
/// ice or saved wrong. An actual clicked grid slot (OpenSlotForm/OpenWeekSlotForm, covered elsewhere)
/// is a genuine, unambiguous date choice and is unaffected by this fix - only the dropdown's "no date
/// was ever clicked" path changes.
/// </summary>
public class CalendarCreateFormDefaultDateTests : BunitContext
{
    private static IRenderedComponent<Calendar> RenderCalendarAnchoredFarInTheFuture(
        BunitContext ctx, out FacilityScheduler.Services.FacilityConfiguration facility, out DateTime farAnchor)
    {
        var registered = StaffPageServices.Register(ctx);
        var resolvedFacility = registered.Facility;
        // Three weeks out - well past both "today" and "tomorrow" (BookingDraft.Reset's own no-date
        // default), so a leftover anchor-date bug and the correct fallback can never coincide by luck.
        var resolvedFarAnchor = resolvedFacility.Today.AddDays(21);
        // Calendar.razor's QueryDate is [SupplyParameterFromQuery] - bUnit refuses to set it as an
        // ordinary component parameter and requires navigating the fake NavigationManager instead,
        // exactly as it would be supplied by the real router from a Prev/Next click or a date jump.
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("date", resolvedFarAnchor.ToString("yyyy-MM-dd")));
        var cut = ctx.Render<Calendar>();
        facility = resolvedFacility;
        farAnchor = resolvedFarAnchor;
        return cut;
    }

    private static void OpenNewEventMenuItem(IRenderedComponent<Calendar> cut, string itemText)
    {
        cut.FindAll("span").First(el => el.TextContent.Contains("+ New Event")).Click();
        cut.FindAll("div").First(el => el.TextContent.Trim() == itemText).Click();
    }

    // The page's own "Jump to date" input (line ~31 of Calendar.razor) is also input[type=date] and
    // renders before the modal in DOM order - a plain type-selector query would silently pick that up
    // instead of the modal's Start/End fields (it's bound to _anchorDate, so a mistaken match here
    // would look exactly like the bug this test exists to catch). Excluded by its aria-label instead.
    private static List<AngleSharp.Dom.IElement> ModalDateInputs(IRenderedComponent<Calendar> cut) =>
        [.. cut.FindAll("input[type=date]").Where(el => el.GetAttribute("aria-label") != "Jump to date")];

    private static DateTime StartDateInputValue(IRenderedComponent<Calendar> cut) =>
        DateTime.Parse(ModalDateInputs(cut)[0].GetAttribute("value")!);

    [Fact]
    public void NewEvent_FromDropdown_DoesNotSeedFromTheCalendarsCurrentlyViewedDate()
    {
        var cut = RenderCalendarAnchoredFarInTheFuture(this, out var facility, out var farAnchor);

        OpenNewEventMenuItem(cut, "New Event");

        var seededStart = StartDateInputValue(cut);
        Assert.NotEqual(farAnchor.Date, seededStart);
    }

    [Fact]
    public void NewEvent_FromDropdown_DefaultsToTomorrow_MatchingBookingDraftsOwnNoDateFallback()
    {
        var cut = RenderCalendarAnchoredFarInTheFuture(this, out var facility, out _);

        OpenNewEventMenuItem(cut, "New Event");

        Assert.Equal(facility.Today.AddDays(1), StartDateInputValue(cut));
    }

    [Fact]
    public void NewOffIceEvent_FromDropdown_DoesNotSeedFromTheCalendarsCurrentlyViewedDate()
    {
        var cut = RenderCalendarAnchoredFarInTheFuture(this, out var facility, out var farAnchor);

        OpenNewEventMenuItem(cut, "New Off-Ice Event");

        var seededStart = StartDateInputValue(cut);
        Assert.NotEqual(farAnchor.Date, seededStart);
        Assert.Equal(facility.Today.AddDays(1), seededStart);
    }

    [Fact]
    public void NewEvent_FromDropdown_StartAndEndDateMatch_SoThereIsNoAccidentalMultiDaySpan()
    {
        // The actual reported symptom: Start and End landing on different dates before staff typed
        // anything at all. They must always agree on open.
        var cut = RenderCalendarAnchoredFarInTheFuture(this, out _, out _);

        OpenNewEventMenuItem(cut, "New Event");

        var dateInputs = ModalDateInputs(cut);
        var startValue = dateInputs[0].GetAttribute("value");
        var endValue = dateInputs[1].GetAttribute("value");
        Assert.Equal(startValue, endValue);
    }
}
