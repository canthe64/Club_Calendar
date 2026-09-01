using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The series start/end date added to "Part of a recurring series" (staff feedback 2026-08-27).
/// SeriesRange/IsLoadingSeriesRange are fetched by the caller (Calendar.razor, via
/// SheetBookingService.GetSeriesRangeAsync) - this component only has to render whatever it's
/// handed, in each of the states that fetch can be in.
/// </summary>
public class BookingDetailModalSeriesRangeTests : BunitContext
{
    private static SheetBooking Booking(string? seriesMasterId) => new()
    {
        SheetMailbox = "sheet1@example.com",
        EventId = "evt-1",
        SeriesMasterId = seriesMasterId,
        Category = BookingCategory.League,
        State = BookingState.Confirmed,
        RenterName = "Tuesday League",
        Start = new DateTime(2026, 9, 8, 19, 0, 0),
        End = new DateTime(2026, 9, 8, 21, 0, 0)
    };

    private IRenderedComponent<BookingDetailModal> Render(SheetBooking booking, SeriesDateRange? range = null, bool isLoading = false) =>
        base.Render<BookingDetailModal>(p => p
            .Add(m => m.Group, new List<SheetBooking> { booking })
            .Add(m => m.SeriesRange, range)
            .Add(m => m.IsLoadingSeriesRange, isLoading));

    [Fact]
    public void NotASeries_NeverShowsTheRecurringSeriesSectionAtAll()
    {
        var cut = Render(Booking(seriesMasterId: null));

        Assert.DoesNotContain("Part of a recurring series", cut.Markup);
    }

    [Fact]
    public void SeriesStillLoading_ShowsTheLabelWithoutDates()
    {
        var cut = Render(Booking("master-1"), isLoading: true);

        Assert.Contains("Part of a recurring series", cut.Markup);
        Assert.Contains("loading series dates", cut.Markup);
    }

    [Fact]
    public void SeriesResolved_ShowsStartingAndEndingDates()
    {
        var range = new SeriesDateRange(new DateTime(2026, 9, 8), new DateTime(2026, 9, 29));
        var cut = Render(Booking("master-1"), range);

        Assert.Contains("starting Sep 8, 2026 and ending Sep 29, 2026", cut.Markup);
    }

    [Fact]
    public void SeriesResolvedWithNoConfiguredEndDate_ShowsStartingOnly()
    {
        // The NoEnd/Numbered-range case - hand-edited or otherwise foreign data, since this app only
        // ever creates EndDate series (CreateSeriesAsync).
        var range = new SeriesDateRange(new DateTime(2026, 9, 8), null);
        var cut = Render(Booking("master-1"), range);

        Assert.Contains("starting Sep 8, 2026", cut.Markup);
        Assert.DoesNotContain("ending", cut.Markup);
    }

    [Fact]
    public void SeriesRangeUnavailable_ShowsTheBareLabelWithNoDates()
    {
        // Not loading, and the fetch came back null - a Graph failure or a stale master reference.
        // The label alone is still correct; there's nothing to append.
        var cut = Render(Booking("master-1"), range: null, isLoading: false);

        Assert.Contains("Part of a recurring series", cut.Markup);
        Assert.DoesNotContain("starting", cut.Markup);
        Assert.DoesNotContain("loading", cut.Markup);
    }
}
