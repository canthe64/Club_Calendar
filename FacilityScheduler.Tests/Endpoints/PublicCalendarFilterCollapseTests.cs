using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The public calendar's category filters are collapsible (2026-09-30, to free up space on phones):
/// a native &lt;details&gt; that works without script, rendered open, with a small script choosing the
/// starting state. The summary says when some categories are hidden.
/// </summary>
public class PublicCalendarFilterCollapseTests
{
    private static readonly DateTime Anchor = new(2026, 10, 1);

    [Fact]
    public void Filters_AreACollapsibleDetailsBlock_ThatWorksWithoutScript()
    {
        var html = PublicCalendarEndpoint.AppendCategoryFilterForm(PublicCalendarEndpoint.ViewMode.Month, Anchor, PublicCalendarEndpoint.FilterState.Default);

        // Rendered open, so with script disabled the filters are simply shown, as before.
        Assert.Contains("""<details class="pub-cal-filters" open""", html);
        Assert.Contains(">Filters</summary>", html);
        Assert.True(html.IndexOf("<form", StringComparison.Ordinal) > html.IndexOf("<summary", StringComparison.Ordinal));
        Assert.True(html.IndexOf("</details>", StringComparison.Ordinal) > html.IndexOf("</form>", StringComparison.Ordinal));
    }

    [Fact]
    public void SomeCategoriesHidden_TheSummarySaysSo()
    {
        var filter = PublicCalendarEndpoint.FilterState.Default with
        {
            IsFiltered = true,
            Categories = [BookingCategory.League, BookingCategory.GroupEvent]
        };
        var total = PublicCalendarEndpoint.AllCategories.Count + PublicCalendarEndpoint.AllClubCategories.Count;
        var shown = 2 + PublicCalendarEndpoint.AllClubCategories.Count;

        var html = PublicCalendarEndpoint.AppendCategoryFilterForm(PublicCalendarEndpoint.ViewMode.Month, Anchor, filter);

        Assert.Contains($"{shown} of {total} categories shown", html);
    }

    [Fact]
    public void EverythingShown_NoHiddenCategoriesNote()
    {
        var html = PublicCalendarEndpoint.AppendCategoryFilterForm(PublicCalendarEndpoint.ViewMode.Month, Anchor, PublicCalendarEndpoint.FilterState.Default);

        Assert.DoesNotContain("categories shown", html);
    }
}
