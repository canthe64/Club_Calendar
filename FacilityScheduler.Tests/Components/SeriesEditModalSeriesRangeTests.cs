using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The series start/end date added to the descriptive text at the top of "Edit recurring series"
/// (staff feedback 2026-08-27) - same SeriesRange/IsLoadingSeriesRange parameters as
/// BookingDetailModalSeriesRangeTests, fetched by the caller and just rendered here.
/// </summary>
public class SeriesEditModalSeriesRangeTests : BunitContext
{
    private static List<SheetBooking> Group() =>
    [
        new()
        {
            SheetMailbox = "sheet1@example.com",
            EventId = "evt-1",
            SeriesMasterId = "master-1",
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday League",
            Start = new DateTime(2026, 9, 8, 19, 0, 0),
            End = new DateTime(2026, 9, 8, 21, 0, 0)
        }
    ];

    private IRenderedComponent<SeriesEditModal> Render(SeriesDateRange? range = null, bool isLoading = false)
    {
        StaffPageServices.Register(this);
        return base.Render<SeriesEditModal>(p => p
            .Add(m => m.Group, Group())
            .Add(m => m.SeriesRange, range)
            .Add(m => m.IsLoadingSeriesRange, isLoading));
    }

    [Fact]
    public void Loading_ShowsLoadingText()
    {
        var cut = Render(isLoading: true);

        Assert.Contains("Loading series dates", cut.Markup);
    }

    [Fact]
    public void Resolved_ShowsStartingAndEndingDates()
    {
        var range = new SeriesDateRange(new DateTime(2026, 9, 8), new DateTime(2026, 9, 29));
        var cut = Render(range);

        Assert.Contains("Starting Sep 8, 2026 and ending Sep 29, 2026.", cut.Markup);
    }

    [Fact]
    public void ResolvedWithNoConfiguredEndDate_ShowsStartingOnly()
    {
        var range = new SeriesDateRange(new DateTime(2026, 9, 8), null);
        var cut = Render(range);

        Assert.Contains("Starting Sep 8, 2026.", cut.Markup);
        Assert.DoesNotContain("ending", cut.Markup);
    }

    [Fact]
    public void Unavailable_AddsNeitherLoadingNorStartingText()
    {
        var cut = Render(range: null, isLoading: false);

        Assert.DoesNotContain("Loading series dates", cut.Markup);
        Assert.DoesNotContain("Starting", cut.Markup);
    }

    [Fact]
    public void ScheduleSummary_StillRendersAlongsideTheRange()
    {
        // The pre-existing "Tuesdays, 6:00PM-8:00PM." text must survive this addition unchanged.
        var range = new SeriesDateRange(new DateTime(2026, 9, 8), new DateTime(2026, 9, 29));
        var cut = Render(range);

        Assert.Contains("Tuesdays, 7:00PM–9:00PM.", cut.Markup);
    }
}
