using FacilityScheduler.Components.Pages;

namespace FacilityScheduler.Tests.Components;

public class CalendarDeepLinkTests
{
    private static readonly DateTime Today = new(2026, 8, 21);

    [Fact]
    public void ResolveDeepLink_NoQueryParameters_DefaultsToTodayAndMonth()
    {
        var (anchor, view) = Calendar.ResolveDeepLink(null, null, Today);

        Assert.Equal(Today, anchor);
        Assert.Equal(Calendar.ViewMode.Month, view);
    }

    [Fact]
    public void ResolveDeepLink_ValidDateAndDayView_IsUsedAsGiven()
    {
        var (anchor, view) = Calendar.ResolveDeepLink("2026-09-05", "day", Today);

        Assert.Equal(new DateTime(2026, 9, 5), anchor);
        Assert.Equal(Calendar.ViewMode.Day, view);
    }

    [Fact]
    public void ResolveDeepLink_WeekView_CaseInsensitive()
    {
        var (_, view) = Calendar.ResolveDeepLink(null, "WEEK", Today);

        Assert.Equal(Calendar.ViewMode.Week, view);
    }

    [Fact]
    public void ResolveDeepLink_UnparsableDate_FallsBackToToday()
    {
        var (anchor, _) = Calendar.ResolveDeepLink("not-a-date", null, Today);

        Assert.Equal(Today, anchor);
    }

    [Fact]
    public void ResolveDeepLink_UnrecognizedView_FallsBackToMonth()
    {
        var (_, view) = Calendar.ResolveDeepLink(null, "agenda", Today);

        Assert.Equal(Calendar.ViewMode.Month, view);
    }
}
