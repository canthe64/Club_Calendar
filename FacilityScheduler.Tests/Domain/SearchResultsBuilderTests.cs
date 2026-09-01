using FacilityScheduler.Domain;
using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

/// <summary>
/// The match/group/sort logic extracted out of EventSearch.razor's old private ApplyResults when the
/// CSV export needed the exact same result set (2026-08-27) - so the screen and the export can never
/// silently disagree about which rows matched. These tests were largely inherited behavior at
/// extraction time; they exist here now rather than only being reachable through a full page render.
/// </summary>
public class SearchResultsBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 27);

    private static SheetBooking Booking(string sheet, Guid groupId, DateTime start, DateTime end, string? renterName = "Smith Wedding", BookingCategory category = BookingCategory.GroupEvent, BookingState state = BookingState.Confirmed) => new()
    {
        SheetMailbox = sheet,
        EventId = Guid.NewGuid().ToString(),
        BookingGroupId = groupId,
        Category = category,
        State = state,
        RenterName = renterName,
        Start = start,
        End = end
    };

    private static ClubEvent Event(string title, DateTime start, DateTime end, bool isAllDay = false, ClubEventCategory category = ClubEventCategory.Meetings) => new()
    {
        Title = title,
        Category = category,
        IsAllDay = isAllDay,
        Start = start,
        End = end
    };

    private static SearchQuery MatchAll() => SearchQueryParser.Parse("smith");

    [Fact]
    public void MultiSheetBooking_CollapsesToOneRow()
    {
        var groupId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var end = start.AddHours(2);
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", groupId, start, end),
            Booking("sheet2@example.com", groupId, start, end),
            Booking("sheet3@example.com", groupId, start, end)
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Single(result.Upcoming);
        Assert.Equal(1, result.OnIceMatchCount);
    }

    [Fact]
    public void UnrelatedSingleSheetBookings_AreNeverMergedTogether()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.Empty, start, start.AddHours(1)),
            Booking("sheet2@example.com", Guid.Empty, start, start.AddHours(1))
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Equal(2, result.Upcoming.Count);
    }

    [Fact]
    public void EndingBeforeToday_IsPast_NotUpcoming()
    {
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.NewGuid(), Today.AddDays(-5), Today.AddDays(-5).AddHours(2))
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Empty(result.Upcoming);
        Assert.Single(result.Past);
    }

    [Fact]
    public void EndingToday_CountsAsUpcoming()
    {
        // The boundary the screen uses: r.End.Date >= today.Date, so an event still running (or
        // ending) today is upcoming, not stale.
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.NewGuid(), Today.AddHours(-1), Today.AddHours(1))
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Single(result.Upcoming);
        Assert.Empty(result.Past);
    }

    [Fact]
    public void Upcoming_SortsSoonestFirst()
    {
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.NewGuid(), Today.AddDays(5), Today.AddDays(5).AddHours(1), renterName: "Smith Later"),
            Booking("sheet2@example.com", Guid.NewGuid(), Today.AddDays(1), Today.AddDays(1).AddHours(1), renterName: "Smith Sooner")
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Equal("Smith Sooner", result.Upcoming[0].Booking!.RenterName);
        Assert.Equal("Smith Later", result.Upcoming[1].Booking!.RenterName);
    }

    [Fact]
    public void Past_SortsMostRecentFirst()
    {
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.NewGuid(), Today.AddDays(-10), Today.AddDays(-10).AddHours(1), renterName: "Smith Older"),
            Booking("sheet2@example.com", Guid.NewGuid(), Today.AddDays(-1), Today.AddDays(-1).AddHours(1), renterName: "Smith Newer")
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Equal("Smith Newer", result.Past[0].Booking!.RenterName);
        Assert.Equal("Smith Older", result.Past[1].Booking!.RenterName);
    }

    [Fact]
    public void BookingsAndClubEvents_AreBothMatchedAndCounted()
    {
        var bookings = new List<SheetBooking> { Booking("sheet1@example.com", Guid.NewGuid(), Today.AddDays(1), Today.AddDays(1).AddHours(1)) };
        var events = new List<ClubEvent> { Event("Smith Fundraiser", Today.AddDays(2), Today.AddDays(2).AddHours(1)) };

        var result = SearchResultsBuilder.Build(bookings, events, MatchAll(), Today);

        Assert.Equal(1, result.OnIceMatchCount);
        Assert.Equal(1, result.OffIceMatchCount);
        Assert.Equal(2, result.Upcoming.Count);
    }

    [Fact]
    public void NonMatchingItems_AreExcludedEntirely()
    {
        var bookings = new List<SheetBooking>
        {
            Booking("sheet1@example.com", Guid.NewGuid(), Today.AddDays(1), Today.AddDays(1).AddHours(1), renterName: "Jones Party")
        };

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Empty(result.Upcoming);
        Assert.Empty(result.Past);
    }

    [Fact]
    public void NoRowCapIsEverApplied()
    {
        // Distinct from EventSearch.MaxRenderedRows - that cap belongs to the page, not the builder.
        // The CSV export depends on this never truncating.
        var bookings = Enumerable.Range(0, 400)
            .Select(i => Booking($"sheet{i}@example.com", Guid.NewGuid(), Today.AddDays(1).AddMinutes(i), Today.AddDays(1).AddMinutes(i + 30)))
            .ToList();

        var result = SearchResultsBuilder.Build(bookings, [], MatchAll(), Today);

        Assert.Equal(400, result.Upcoming.Count);
    }
}
