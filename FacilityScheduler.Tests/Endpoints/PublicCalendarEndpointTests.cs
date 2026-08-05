using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class PublicCalendarEndpointTests
{
    private static readonly DateTime Today = new(2026, 8, 3);

    // ViewMode is internal, so it can't appear in a public [Theory] method's signature (even with
    // InternalsVisibleTo, a public member still can't expose a less-accessible type) - compared via
    // ToString() instead of taking ViewMode itself as a Theory parameter.
    [Theory]
    [InlineData("week", "Week")]
    [InlineData("WEEK", "Week")]
    [InlineData("day", "Day")]
    [InlineData(null, "Month")]
    [InlineData("nonsense", "Month")]
    public void ParseView_MapsKnownValuesCaseInsensitively_DefaultsToMonth(string? view, string expected)
    {
        Assert.Equal(expected, PublicCalendarEndpoint.ParseView(view).ToString());
    }

    [Fact]
    public void ParseMonth_MissingOrUnparsable_ReturnsNull()
    {
        Assert.Null(PublicCalendarEndpoint.ParseMonth(null, Today));
        Assert.Null(PublicCalendarEndpoint.ParseMonth("not-a-month", Today));
    }

    [Fact]
    public void ParseMonth_WithinWindow_ReturnsRequestedMonth()
    {
        var result = PublicCalendarEndpoint.ParseMonth("2026-09", Today);
        Assert.Equal(new DateTime(2026, 9, 1), result);
    }

    [Fact]
    public void ParseMonth_FarInThePast_ClampsToOneYearBack()
    {
        // Anonymous, unauthenticated surface - an unclamped month is a cache-growth/Graph-quota
        // vector (every distinct month is a live fan-out across every mailbox and a new
        // never-evicted cache entry).
        var result = PublicCalendarEndpoint.ParseMonth("2001-01", Today);
        Assert.Equal(new DateTime(Today.Year - 1, Today.Month, 1), result);
    }

    [Fact]
    public void ParseMonth_FarInTheFuture_ClampsToTwoYearsForward()
    {
        var result = PublicCalendarEndpoint.ParseMonth("2099-01", Today);
        Assert.Equal(new DateTime(Today.Year + 2, Today.Month, 1), result);
    }

    [Fact]
    public void ParseDate_MissingOrUnparsable_ReturnsNull()
    {
        Assert.Null(PublicCalendarEndpoint.ParseDate(null, Today));
        Assert.Null(PublicCalendarEndpoint.ParseDate("garbage", Today));
    }

    [Fact]
    public void ParseDate_WithinWindow_ReturnsRequestedDate()
    {
        Assert.Equal(new DateTime(2026, 9, 15), PublicCalendarEndpoint.ParseDate("2026-09-15", Today));
    }

    [Fact]
    public void ParseDate_OutOfWindow_ClampsToNearestBound()
    {
        Assert.Equal(Today.AddYears(-1), PublicCalendarEndpoint.ParseDate("1999-01-01", Today));
        Assert.Equal(Today.AddYears(2), PublicCalendarEndpoint.ParseDate("2099-01-01", Today));
    }

    [Fact]
    public void ParseFilter_NoFilterMarker_DefaultsToEverythingShown()
    {
        // A bare link (no ?filtered= at all) must behave exactly like it did before category
        // filters existed - old bookmarks/embeds/shared links keep working unchanged.
        var filter = PublicCalendarEndpoint.ParseFilter(filtered: null, categories: null, showClubEvents: null);

        Assert.False(filter.IsFiltered);
        Assert.True(filter.ShowClubEvents);
        Assert.Equal(PublicCalendarEndpoint.AllCategories, filter.Categories);
    }

    [Fact]
    public void ParseFilter_Submitted_OnlyIncludesCheckedCategories()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League", "Bonspiel"], showClubEvents: null);

        Assert.True(filter.IsFiltered);
        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.League, BookingCategory.Bonspiel }, filter.Categories);
    }

    [Fact]
    public void ParseFilter_SubmittedWithNoCategoriesChecked_FallsBackToAll()
    {
        // Every category box unchecked (or none of the recognized values present at all) falls
        // back to "all" rather than "none" - a blank calendar from a stray misclick isn't a real
        // use case worth supporting, and "all" is a friendlier failure mode than an empty grid.
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: null);

        Assert.True(filter.IsFiltered);
        Assert.Equal(PublicCalendarEndpoint.AllCategories, filter.Categories);
    }

    [Fact]
    public void ParseFilter_SubmittedWithoutClubEventsChecked_HidesClubEvents()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: null);

        Assert.False(filter.ShowClubEvents);
    }

    [Fact]
    public void ParseFilter_SubmittedWithClubEventsChecked_ShowsClubEvents()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: "1");

        Assert.True(filter.ShowClubEvents);
    }

    [Fact]
    public void ParseCategories_UnknownOrGarbageValues_AreIgnored()
    {
        var result = PublicCalendarEndpoint.ParseCategories(["League", "NotACategory", ""]);

        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.League }, result);
    }

    [Fact]
    public void ParseCategories_EventCategory_IsExcludedEvenIfRequested()
    {
        // Event is reserved for Club Events (CalendarStyles.SheetCategories), never a sheet-booking
        // category a member could filter by - a crafted/malformed query string naming it must not
        // slip through.
        var result = PublicCalendarEndpoint.ParseCategories(["Event", "League"]);

        Assert.DoesNotContain(BookingCategory.Event, result);
        Assert.Contains(BookingCategory.League, result);
    }

    [Fact]
    public void FilterQuery_NotFiltered_ReturnsEmptyString()
    {
        var filter = PublicCalendarEndpoint.ParseFilter(filtered: null, categories: null, showClubEvents: null);

        Assert.Equal("", PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_AllCategoriesSelected_OmitsCategoriesFromTheQueryString()
    {
        // The common case - a member only toggled Club Events, or hasn't excluded anything - should
        // produce a short URL, since ParseFilter already treats "no categories present" as "all".
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: "1");

        Assert.Equal("&filtered=1&showClubEvents=1", PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_PartialCategorySelection_ListsEachSelectedCategory()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: null);

        Assert.Equal("&filtered=1&categories=League", PublicCalendarEndpoint.FilterQuery(filter));
    }
}
