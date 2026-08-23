using Bunit;
using FacilityScheduler;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Components.Web;

namespace FacilityScheduler.Tests.Components;

/// <summary>First coverage for the unified event dialog - the surface that replaces BookingFormModal
/// and ClubEventFormModal, neither of which ever had render tests.</summary>
public class EventFormModalTests : BunitContext
{
    private static readonly DateTime Today = new(2026, 8, 21);

    private IRenderedComponent<EventFormModal> RenderModal(EventDraft draft)
    {
        StaffPageServices.Register(this);
        return Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, draft));
    }

    private static EventDraft CreateDraft(EventMode mode)
    {
        var draft = new EventDraft();
        draft.ResetForCreate(mode, Today);
        return draft;
    }

    [Fact]
    public void OnIceMode_RendersSheetCategories_NotOffIceCategories()
    {
        var cut = RenderModal(CreateDraft(EventMode.OnIce));

        Assert.Contains(CalendarStyles.CategoryLabel(BookingCategory.League), cut.Markup);
        Assert.DoesNotContain(CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Meetings), cut.Markup);
    }

    [Fact]
    public void OffIceMode_RendersOffIceCategoriesAndTheExplanatoryNote()
    {
        var cut = RenderModal(CreateDraft(EventMode.OffIce));

        Assert.Contains(CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.Meetings), cut.Markup);
        Assert.Contains("aren't checked against the booking season", cut.Markup);
    }

    [Fact]
    public void OffIceMode_StillRendersTheSheetChips_ButGreyedOut()
    {
        var cut = RenderModal(CreateDraft(EventMode.OffIce));

        // Greyed rather than hidden, so the layout doesn't jump on toggle and the concept stays visible.
        Assert.Contains("Sheet 1", cut.Markup);
        Assert.Contains("opacity:.45", cut.Markup);
    }

    [Fact]
    public void OnIceMode_RendersTheSheetChipsAtFullOpacity()
    {
        var cut = RenderModal(CreateDraft(EventMode.OnIce));

        Assert.Contains("Sheet 1", cut.Markup);
        Assert.DoesNotContain("opacity:.45", cut.Markup);
    }

    [Fact]
    public void OffIceMode_ClickingASheetChipDoesNothing()
    {
        var draft = CreateDraft(EventMode.OffIce);
        var cut = RenderModal(draft);

        cut.FindAll("span").First(s => s.TextContent.Trim() == "Sheet 1").Click();

        Assert.Empty(draft.OnIce.SelectedSheets);
    }

    [Fact]
    public void OffIceMode_ShowsTheClosesAllSheetsCheckbox_OnIceModeDoesNot()
    {
        Assert.Contains("Closes all sheets for this time", RenderModal(CreateDraft(EventMode.OffIce)).Markup);

        // Fresh context: one BunitContext can't render two independent service graphs.
        using var onIce = new BunitContext();
        StaffPageServices.Register(onIce);
        var cut = onIce.Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OnIce)));

        Assert.DoesNotContain("Closes all sheets for this time", cut.Markup);
    }

    [Fact]
    public void OffIceMode_ShowsTheAllDayCheckbox_OnIceModeDoesNot()
    {
        Assert.Contains("All day", RenderModal(CreateDraft(EventMode.OffIce)).Markup);

        using var onIce = new BunitContext();
        StaffPageServices.Register(onIce);
        var cut = onIce.Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OnIce)));

        Assert.DoesNotContain("All day", cut.Markup);
    }

    [Fact]
    public void TogglingToOffIce_KeepsTheTypedTitleAndPickedDates()
    {
        var draft = CreateDraft(EventMode.OnIce);
        var cut = RenderModal(draft);

        cut.FindAll("input").First(i => i.GetAttribute("type") is null or "text")
            .Input("Annual General Meeting");
        cut.FindAll("input[type=date]")[0].Change("2026-09-04");

        cut.FindAll("span").First(s => s.TextContent.Trim() == "Off the ice").Click();

        Assert.Equal(EventMode.OffIce, draft.Mode);
        Assert.Equal("Annual General Meeting", draft.OffIce.Title);
        Assert.Equal(new DateTime(2026, 9, 4), draft.OffIce.StartDate);
        Assert.Contains("Annual General Meeting", cut.Markup);
    }

    [Fact]
    public void TogglingBackToOnIce_RestoresTheSheetsAndCategoryAlreadyPicked()
    {
        var draft = CreateDraft(EventMode.OnIce);
        var cut = RenderModal(draft);

        cut.FindAll("span").First(s => s.TextContent.Trim() == "Sheet 2").Click();
        cut.FindAll("span").First(s => s.TextContent.Trim() == CalendarStyles.CategoryLabel(BookingCategory.Bonspiel)).Click();

        cut.FindAll("span").First(s => s.TextContent.Trim() == "Off the ice").Click();
        cut.FindAll("span").First(s => s.TextContent.Trim() == "On the ice").Click();

        Assert.Single(draft.OnIce.SelectedSheets);
        Assert.Equal(BookingCategory.Bonspiel, draft.OnIce.Category);
    }

    [Fact]
    public void EditingAnExistingEvent_RendersTheModeAsAStaticBadge_WithNoToggle()
    {
        var draft = new EventDraft();
        draft.LoadForEdit(new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            Start = Today,
            End = Today,
        });

        var cut = RenderModal(draft);

        Assert.Contains("Off the ice", cut.Markup);
        Assert.DoesNotContain("On the ice", cut.Markup);
        Assert.Contains("can't be moved between on-ice and off-ice", cut.Markup);
    }

    [Fact]
    public void EditingAnExistingEvent_TheModeBadgeCarriesNoClickHandlerAtAll()
    {
        var draft = new EventDraft();
        draft.LoadForEdit(new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            Start = Today,
            End = Today,
        });
        var cut = RenderModal(draft);

        var badge = cut.FindAll("span").First(s => s.TextContent.Trim() == "Off the ice");

        // Not merely a no-op handler - the badge is inert markup, so there is nothing to click and
        // no code path that could convert a saved event between modes.
        Assert.Throws<Bunit.MissingEventHandlerException>(() => badge.Click());
        Assert.Equal(EventMode.OffIce, draft.Mode);
    }

    [Fact]
    public void AllowModeToggleFalse_LocksTheModeEvenWhenCreating()
    {
        // The Off-Ice Events page saves off-ice unconditionally, so a live toggle there would let a
        // save land somewhere that page can't show.
        var draft = CreateDraft(EventMode.OffIce);
        StaffPageServices.Register(this);
        var cut = Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, draft)
            .Add(m => m.AllowModeToggle, false));

        Assert.DoesNotContain("On the ice", cut.Markup);
        var badge = cut.FindAll("span").First(s => s.TextContent.Trim() == "Off the ice");
        Assert.Throws<Bunit.MissingEventHandlerException>(() => badge.Click());
        Assert.Equal(EventMode.OffIce, draft.Mode);
    }

    [Fact]
    public void CreatingAnEvent_ShowsBothToggleOptions()
    {
        var cut = RenderModal(CreateDraft(EventMode.OnIce));

        Assert.Contains("On the ice", cut.Markup);
        Assert.Contains("Off the ice", cut.Markup);
    }

    [Fact]
    public void Header_ReadsNewEventWhenCreatingAndEditEventWhenEditing()
    {
        Assert.Contains("New Event", RenderModal(CreateDraft(EventMode.OnIce)).Markup);

        var editing = new EventDraft();
        editing.LoadForEdit(new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            Start = Today,
            End = Today,
        });

        using var ctx = new BunitContext();
        StaffPageServices.Register(ctx);
        var cut = ctx.Render<EventFormModal>(p => p.Add(m => m.IsOpen, true).Add(m => m.Draft, editing));

        Assert.Contains("Edit Event", cut.Markup);
    }

    [Fact]
    public void OnIceMode_GroupEventCategory_ShowsHoldConfirmedAndContactFields()
    {
        var draft = CreateDraft(EventMode.OnIce);
        draft.OnIce.SetCategory(BookingCategory.GroupEvent);
        var cut = RenderModal(draft);

        Assert.Contains("Hold for future group event", cut.Markup);
        Assert.Contains("Phone", cut.Markup);
        Assert.Contains("Email", cut.Markup);
    }

    [Fact]
    public void OnIceMode_NonGroupEventCategory_HidesHoldConfirmedAndContactFields()
    {
        var draft = CreateDraft(EventMode.OnIce);
        draft.OnIce.SetCategory(BookingCategory.League);
        var cut = RenderModal(draft);

        Assert.DoesNotContain("Hold for future group event", cut.Markup);
        Assert.DoesNotContain("Phone", cut.Markup);
    }

    [Fact]
    public void OffIceAllDay_HidesTheTimePickers()
    {
        var draft = CreateDraft(EventMode.OffIce);
        draft.OffIce.IsAllDay = true;
        var cut = RenderModal(draft);

        Assert.Empty(cut.FindAll("select"));
    }

    [Fact]
    public void OffIceTimed_ShowsTheTimePickers()
    {
        var draft = CreateDraft(EventMode.OffIce);
        draft.OffIce.IsAllDay = false;
        var cut = RenderModal(draft);

        Assert.Equal(2, cut.FindAll("select").Count);
    }

    [Fact]
    public void ValidationMessage_ComesFromEventDraftValidate()
    {
        var cut = RenderModal(CreateDraft(EventMode.OnIce));

        Assert.Contains("Choose a category.", cut.Markup);
    }

    [Fact]
    public void ConflictsPanel_IsNeverRenderedInOffIceMode()
    {
        StaffPageServices.Register(this);
        var cut = Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OffIce))
            .Add(m => m.Conflicts, [new SheetBooking
            {
                SheetMailbox = "sheet1@test.onmicrosoft.com",
                Start = Today.AddHours(18),
                End = Today.AddHours(20),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
            }]));

        Assert.DoesNotContain("This time conflicts with:", cut.Markup);
    }

    [Fact]
    public void ConflictsPanel_RendersInOnIceMode()
    {
        StaffPageServices.Register(this);
        var cut = Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OnIce))
            .Add(m => m.Conflicts, [new SheetBooking
            {
                SheetMailbox = "sheet1@test.onmicrosoft.com",
                Start = Today.AddHours(18),
                End = Today.AddHours(20),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
            }]));

        Assert.Contains("This time conflicts with:", cut.Markup);
        Assert.Contains("Sheet 1", cut.Markup);
    }

    [Fact]
    public void ClosureConflict_ReadsAsAnOffIceEvent_NotAClubEvent()
    {
        StaffPageServices.Register(this);
        var cut = Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OnIce))
            .Add(m => m.Conflicts, [new SheetBooking
            {
                SheetMailbox = "", // the closure sentinel
                Start = Today.AddHours(18),
                End = Today.AddHours(20),
                Category = BookingCategory.Other,
                State = BookingState.Confirmed,
                RenterName = "Ice plant maintenance",
            }]));

        Assert.Contains("Off-ice event \"Ice plant maintenance\"", cut.Markup);
    }

    [Fact]
    public void DeleteLink_ShowsOnlyWhenEditingAnOffIceEvent()
    {
        var draft = new EventDraft();
        draft.LoadForEdit(new ClubEvent
        {
            Title = "Board Meeting",
            Category = ClubEventCategory.Meetings,
            Start = Today,
            End = Today,
        });

        Assert.Contains("Delete Event", RenderModal(draft).Markup);

        using var creating = new BunitContext();
        StaffPageServices.Register(creating);
        var cut = creating.Render<EventFormModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Draft, CreateDraft(EventMode.OffIce)));

        Assert.DoesNotContain("Delete Event", cut.Markup);
    }

    [Fact]
    public void StartDatePastEndDate_PullsEndDateForward()
    {
        var draft = CreateDraft(EventMode.OnIce);
        var cut = RenderModal(draft);

        cut.FindAll("input[type=date]")[0].Change("2026-12-25");

        Assert.Equal(new DateTime(2026, 12, 25), draft.OnIce.EndDate.Date);
    }
}
