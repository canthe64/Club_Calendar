using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// Two requests from 2026-10-07 on the public calendar: today highlighted (every view), and All/None
/// links per filter row (between the row label and its categories). The staff calendar's today
/// highlight is CalendarTodayHighlightTests.
/// </summary>
public class PublicCalendarTodayAndToggleTests
{
    private static readonly DateTime Today = new(2026, 10, 7);
    private static readonly PublicMonthView EmptyView = new([], []);

    // ---- Today: public calendar -----------------------------------------------------------------

    [Fact]
    public void PublicMonthCell_Today_IsHighlighted_OtherDaysAreNot()
    {
        var today = new StringBuilder();
        var other = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(today, Today, Today, EmptyView, Today);
        PublicCalendarEndpoint.AppendDayCell(other, Today.AddDays(1), Today, EmptyView, Today);

        // Only the date number is highlighted - the cell itself keeps its normal look.
        Assert.Contains($"""<span class="pub-cal-today" style="{CalendarStyles.TodayDayNumberStyle}">7</span>""", today.ToString());
        Assert.DoesNotContain(CalendarStyles.TodayBg, today.ToString());
        Assert.DoesNotContain("pub-cal-today", other.ToString());
    }

    [Fact]
    public void PublicWeekHeader_Today_IsHighlighted()
    {
        var today = new StringBuilder();
        var other = new StringBuilder();

        PublicCalendarEndpoint.AppendDayColumn(today, Today, EmptyView, 0, showHeader: true, isMultiDay: true, Today);
        PublicCalendarEndpoint.AppendDayColumn(other, Today.AddDays(-1), EmptyView, 0, showHeader: true, isMultiDay: true, Today);

        Assert.Contains("pub-cal-colhead pub-cal-today", today.ToString());
        Assert.DoesNotContain("pub-cal-today", other.ToString());
    }

    // ---- All/None per filter row ----------------------------------------------------------------

    private static string FilterHtml(PublicCalendarEndpoint.FilterState filter) =>
        PublicCalendarEndpoint.AppendCategoryFilterForm(PublicCalendarEndpoint.ViewMode.Week, Today, filter);

    private static List<(string Text, string Href)> RowToggles(string html)
    {
        var links = new List<(string, string)>();
        foreach (var label in new[] { "ON ICE", "OFF ICE" })
        {
            var row = html[html.IndexOf($">{label}</span>", StringComparison.Ordinal)..];
            var a = row.IndexOf("<a href=\"", StringComparison.Ordinal) + 9;
            var href = System.Net.WebUtility.HtmlDecode(row[a..row.IndexOf('"', a)]);
            var textStart = row.IndexOf('>', a) + 1;
            links.Add((row[textStart..row.IndexOf("</a>", textStart, StringComparison.Ordinal)], href));
        }
        return links;
    }

    [Fact]
    public void Toggles_SitBetweenTheRowLabelAndItsCategories()
    {
        var html = FilterHtml(PublicCalendarEndpoint.FilterState.Default);

        var label = html.IndexOf(">ON ICE</span>", StringComparison.Ordinal);
        var toggle = html.IndexOf(">None</a>", label, StringComparison.Ordinal);
        var firstCategory = html.IndexOf("name=\"categories\" value=\"GroupEvent\"", label, StringComparison.Ordinal);
        Assert.True(label < toggle && toggle < firstCategory);
    }

    [Fact]
    public void EverythingShowing_BothRowsOfferNone_AndEachOnlyClearsItsOwnRow()
    {
        var toggles = RowToggles(FilterHtml(PublicCalendarEndpoint.FilterState.Default));

        Assert.Equal(["None", "None"], toggles.Select(t => t.Text));
        var onIceNone = Parse(toggles[0].Href);
        Assert.Empty(onIceNone.Categories);
        Assert.Equal(PublicCalendarEndpoint.AllClubCategories, onIceNone.ClubCategories);
        var offIceNone = Parse(toggles[1].Href);
        Assert.Equal(PublicCalendarEndpoint.AllCategories, offIceNone.Categories);
        Assert.Empty(offIceNone.ClubCategories);
        Assert.StartsWith("/public/calendar?view=week&date=2026-10-07", toggles[0].Href);
    }

    [Fact]
    public void EmptyRow_OffersAll_WhichRestoresEveryCategoryInThatRow()
    {
        var filter = PublicCalendarEndpoint.FilterState.Default with { IsFiltered = true, Categories = [] };

        var onIce = RowToggles(FilterHtml(filter))[0];

        Assert.Equal("All", onIce.Text);
        Assert.Equal(PublicCalendarEndpoint.AllCategories, Parse(onIce.Href).Categories);
    }

    [Fact]
    public void NoneMarker_MeansNoOnIceCategories_ButAnEmptyListStillMeansAll()
    {
        // Existing links and subscriptions write "all on-ice" as no categories at all - they must
        // keep meaning all. Only the explicit marker means none.
        Assert.Equal(PublicCalendarEndpoint.AllCategories, PublicCalendarEndpoint.ParseFilter("1", null, "1").Categories);
        Assert.Empty(PublicCalendarEndpoint.ParseFilter("1", ["none"], "1").Categories);
        Assert.Equal([BookingCategory.League], PublicCalendarEndpoint.ParseFilter("1", ["none", "League"], "1").Categories);
    }

    [Fact]
    public void Form_SubmitsTheNoneMarker_SoUncheckingEveryOnIceBoxMeansNone()
    {
        var html = FilterHtml(PublicCalendarEndpoint.FilterState.Default);

        Assert.Contains("""<input type="hidden" name="categories" value="none">""", html);
    }

    [Fact]
    public void FeedUrl_ForNoOnIceCategories_CarriesTheMarker()
    {
        var filter = PublicCalendarEndpoint.FilterState.Default with { IsFiltered = true, Categories = [] };

        Assert.Contains("categories=none", PublicCalendarFeedEndpoint.FeedUrl("https://calendar.test.example", filter));
    }

    // Parses a generated href's query string the way the endpoint's model binding would.
    private static PublicCalendarEndpoint.FilterState Parse(string href)
    {
        var query = href[(href.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=')).ToList();
        string? One(string name) => query.FirstOrDefault(p => p[0] == name)?[1];
        string[] Many(string name) => [.. query.Where(p => p[0] == name).Select(p => p[1])];
        return PublicCalendarEndpoint.ParseFilter(One("filtered"), Many("categories"), One("showClubEvents"), One("clubFiltered"), Many("clubCategories"));
    }
}
