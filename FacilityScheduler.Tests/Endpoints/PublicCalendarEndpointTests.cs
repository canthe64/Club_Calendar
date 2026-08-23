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
    public void ResolveMonthAnchor_MonthParamWins_WhenBothArePresent()
    {
        // Every Prev/Today/Next and Month-tab link emits month=; the date-jump form emits date=.
        // When a link carries both, the explicit month is the intent.
        var result = PublicCalendarEndpoint.ResolveMonthAnchor("2026-09", "2026-11-15", Today);

        Assert.Equal(new DateTime(2026, 9, 1), result);
    }

    [Fact]
    public void ResolveMonthAnchor_FallsBackToDate_SoTheDatePickerWorksInMonthView()
    {
        // Regression test: the date-jump picker submits date= in every view. Month view originally
        // read only month=, so a jump there silently landed back on today.
        var result = PublicCalendarEndpoint.ResolveMonthAnchor(null, "2026-11-15", Today);

        Assert.Equal(new DateTime(2026, 11, 15), result);
    }

    [Fact]
    public void ResolveMonthAnchor_NeitherPresent_FallsBackToToday()
    {
        Assert.Equal(Today, PublicCalendarEndpoint.ResolveMonthAnchor(null, null, Today));
    }

    [Fact]
    public void ResolveMonthAnchor_StillClampsAnOutOfRangeDate()
    {
        // The date= fallback must not become a way around the clamping that bounds the anonymous
        // cache-key/Graph-fanout surface.
        var result = PublicCalendarEndpoint.ResolveMonthAnchor(null, "2099-01-01", Today);

        Assert.Equal(Today.AddYears(2), result);
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
        Assert.Empty(filter.ClubCategories);
    }

    [Fact]
    public void ParseFilter_SubmittedWithClubEventsChecked_ShowsClubEvents()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: "1");

        Assert.True(filter.ShowClubEvents);
    }

    // ---- Off-ice per-category filtering, and its legacy-URL back-compat -------------------------

    [Fact]
    public void ParseFilter_LegacyShowClubEventsWithoutClubFiltered_StillShowsEveryOffIceCategory()
    {
        // The back-compat case this whole clubFiltered marker exists for: a bookmark or embed made
        // before off-ice gained per-category filtering carries only showClubEvents=1. It has to keep
        // meaning "show everything off-ice", not parse as "zero categories selected".
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: "1",
            clubFiltered: null, clubCategories: null);

        Assert.Equal(PublicCalendarEndpoint.AllClubCategories, filter.ClubCategories);
    }

    [Fact]
    public void ParseFilter_LegacyWithoutShowClubEvents_StillHidesEveryOffIceCategory()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: null,
            clubFiltered: null, clubCategories: null);

        Assert.Empty(filter.ClubCategories);
    }

    [Fact]
    public void ParseFilter_ClubFilteredWithNoClubCategories_HidesEveryOffIceEvent()
    {
        // Deliberately NOT the fall-back-to-all the sheet categories get: this family replaced a
        // single on/off toggle, and D67's rule still applies - "none checked" has to mean off.
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: null,
            clubFiltered: "1", clubCategories: null);

        Assert.Empty(filter.ClubCategories);
        Assert.False(filter.ShowClubEvents);
    }

    [Fact]
    public void ParseFilter_ClubFilteredWithSomeClubCategories_KeepsOnlyThose()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: "1",
            clubFiltered: "1", clubCategories: ["Meetings", "Closure"]);

        Assert.Equal(
            new HashSet<ClubEventCategory> { ClubEventCategory.Meetings, ClubEventCategory.Closure },
            filter.ClubCategories);
    }

    [Fact]
    public void ParseClubCategories_UnknownOrGarbageValues_AreIgnored()
    {
        var result = PublicCalendarEndpoint.ParseClubCategories(["Meetings", "NotACategory", ""]);

        Assert.Equal(new HashSet<ClubEventCategory> { ClubEventCategory.Meetings }, result);
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
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: "1",
            clubFiltered: "1", clubCategories: [.. PublicCalendarEndpoint.AllClubCategories.Select(c => c.ToString())]);

        // clubFiltered is what distinguishes "submitted with every off-ice box unchecked" from "a URL
        // that predates off-ice category filtering", so it is emitted even when nothing is excluded.
        Assert.Equal("&filtered=1&showClubEvents=1&clubFiltered=1", PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_PartialClubCategorySelection_ListsEachSelectedClubCategory()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: "1",
            clubFiltered: "1", clubCategories: ["Meetings"]);

        Assert.Equal("&filtered=1&showClubEvents=1&clubFiltered=1&clubCategories=Meetings",
            PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_EveryOffIceCategoryUnchecked_OmitsShowClubEventsEntirely()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", categories: null, showClubEvents: null,
            clubFiltered: "1", clubCategories: null);

        Assert.Equal("&filtered=1&clubFiltered=1", PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_PartialCategorySelection_ListsEachSelectedCategory()
    {
        var filter = PublicCalendarEndpoint.ParseFilter("1", ["League"], showClubEvents: null);

        // clubFiltered=1 is emitted for any filtered state, so a link copied out of this page always
        // round-trips through the modern parse path rather than the legacy one.
        Assert.Equal("&filtered=1&categories=League&clubFiltered=1", PublicCalendarEndpoint.FilterQuery(filter));
    }

    [Fact]
    public void FilterQuery_RoundTripsThroughParseFilter_ForEveryShape()
    {
        // The emitted query string has to parse back to the same state, or a Prev/Next click would
        // quietly change what the member is looking at.
        (string? Filtered, string[]? Cats, string? Show, string? ClubFiltered, string[]? ClubCats)[] cases =
        [
            ("1", null, "1", "1", null),
            ("1", ["League"], null, "1", null),
            ("1", null, "1", "1", ["Meetings"]),
            ("1", ["League", "Bonspiel"], "1", "1", ["Meetings", "Closure"]),
        ];

        foreach (var (filtered, cats, show, clubFiltered, clubCats) in cases)
        {
            var original = PublicCalendarEndpoint.ParseFilter(filtered, cats, show, clubFiltered, clubCats);
            var query = PublicCalendarEndpoint.FilterQuery(original);

            var pairs = query.TrimStart('&').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToList();
            string? Single(string name) => pairs.FirstOrDefault(p => p[0] == name)?[1];
            string[]? Many(string name)
            {
                var values = pairs.Where(p => p[0] == name).Select(p => p[1]).ToArray();
                return values.Length == 0 ? null : values;
            }

            var reparsed = PublicCalendarEndpoint.ParseFilter(
                Single("filtered"), Many("categories"), Single("showClubEvents"),
                Single("clubFiltered"), Many("clubCategories"));

            Assert.Equal(original.Categories, reparsed.Categories);
            Assert.Equal(original.ClubCategories, reparsed.ClubCategories);
            Assert.Equal(original.IsFiltered, reparsed.IsFiltered);
        }
    }
}
