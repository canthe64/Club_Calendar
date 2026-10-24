using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>Editing a booking leaves it on exactly the selected sheets (2026-10-24): the draft works
/// out which sheets are kept, added and removed - the same answer the save uses and the form warns
/// with - and the form says which sheets saving will remove.</summary>
public class EditSheetRemovalTests : BunitContext
{
    private static readonly DateTime Day = new(2026, 10, 24);
    private static readonly string[] Sheets = TestFacility.SheetMailboxes;

    private static List<SheetBooking> Group(params string[] sheets)
    {
        var groupId = Guid.NewGuid();
        return [.. sheets.Select((s, i) => new SheetBooking
        {
            SheetMailbox = s, EventId = $"evt-{i}", BookingGroupId = groupId, Category = BookingCategory.PracticeIce,
            State = BookingState.Confirmed, RenterName = "Lani Dubberstein (Guest)", Start = Day.AddHours(19.5), End = Day.AddHours(21.5)
        })];
    }

    [Fact]
    public void SheetChanges_SplitsTheSelectionIntoKeptRemovedAndAdded()
    {
        var draft = new BookingDraft();
        draft.LoadForEdit(Group(Sheets[0], Sheets[1]));
        draft.SelectedSheets = [Sheets[1], Sheets[2]];

        var (kept, removed, added) = draft.SheetChanges();

        Assert.Equal([Sheets[1]], kept.Select(m => m.SheetMailbox));
        Assert.Equal([Sheets[0]], removed.Select(m => m.SheetMailbox));
        Assert.Equal([Sheets[2]], added);
    }

    [Fact]
    public void DeselectingASheet_WarnsThatSavingRemovesIt()
    {
        StaffPageServices.Register(this);
        var draft = new EventDraft();
        draft.LoadForEdit(Group(Sheets[0], Sheets[1], Sheets[2]));
        var cut = Render<EventFormModal>(p => p.Add(m => m.IsOpen, true).Add(m => m.Draft, draft));
        Assert.Empty(cut.FindAll(".sheets-removed-warning"));

        cut.FindAll("span").First(s => s.TextContent.Trim() == CalendarStyles.SheetLabel(Sheets[0])).Click();

        Assert.Contains("Sheet 1 will be removed from this booking when you save.", cut.Find(".sheets-removed-warning").TextContent);
    }

    [Fact]
    public async Task SavingAnEditWithASheetDeselected_TakesThatSheetOffTheBooking()
    {
        // The whole staff path, end to end: open the booking, deselect a sheet, save.
        var services = StaffPageServices.Register(this);
        var day = services.Facility.Today;
        Assert.True((await services.Bookings.CreateAcrossSheetsAsync([Sheets[0], Sheets[1], Sheets[2]], new SheetBooking
        {
            SheetMailbox = "", Start = day.AddHours(19), End = day.AddHours(21), Category = BookingCategory.League,
            State = BookingState.Confirmed, RenterName = "Tuesday League"
        }, "tester")).IsSuccess);

        var cut = Render<FacilityScheduler.Components.Pages.Calendar>();
        cut.WaitForAssertion(() => Assert.Contains("Tuesday League", cut.Markup));
        cut.FindAll("div").First(d => d.TextContent.Contains("Tuesday League") && d.Children.Length == 0).Click();
        cut.FindAll("span").First(s => s.TextContent.Trim() == "Edit event…").Click();
        cut.FindAll("span").First(s => s.TextContent.Trim() == CalendarStyles.SheetLabel(Sheets[0])).Click();
        cut.FindAll("span").First(s => s.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Empty(services.Gateway.Events(Sheets[0])));
        Assert.Single(services.Gateway.Events(Sheets[1]));
        Assert.Single(services.Gateway.Events(Sheets[2]));
    }
}
