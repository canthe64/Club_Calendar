using AngleSharp.Dom;
using Bunit;
using FacilityScheduler.Components.Calendar;

namespace FacilityScheduler.Tests.Components;

/// <summary>The hour + minutes pair that replaced the single 97-option time dropdown. The behaviour
/// worth pinning is the arithmetic between the two controls and the one value (end-of-day midnight)
/// that only half of them applies to.</summary>
public class TimeOfDayPickerTests : BunitContext
{
    private IRenderedComponent<TimeOfDayPicker> RenderPicker(int minutes, Action<int> onChange) =>
        Render<TimeOfDayPicker>(p => p
            .Add(c => c.Minutes, minutes)
            .Add(c => c.MinutesChanged, onChange));

    private static IElement HourSelect(IRenderedComponent<TimeOfDayPicker> cut) => cut.FindAll("select")[0];
    private static IElement MinuteSelect(IRenderedComponent<TimeOfDayPicker> cut) => cut.FindAll("select")[1];

    [Fact]
    public void RendersTwoSelects_TwentyFiveHoursAndFourQuarters()
    {
        var cut = RenderPicker(18 * 60 + 15, _ => { });

        Assert.Equal(2, cut.FindAll("select").Count);
        Assert.Equal(25, HourSelect(cut).QuerySelectorAll("option").Length);
        Assert.Equal(4, MinuteSelect(cut).QuerySelectorAll("option").Length);
    }

    [Fact]
    public void ChangingTheHour_KeepsTheMinutesAlreadyChosen()
    {
        // 6:45 PM -> 7 PM must land on 7:45 PM, not reset to the top of the hour.
        var result = -1;
        var cut = RenderPicker(18 * 60 + 45, m => result = m);

        HourSelect(cut).Change("1140");

        Assert.Equal(19 * 60 + 45, result);
    }

    [Fact]
    public void ChangingTheMinutes_KeepsTheHourAlreadyChosen()
    {
        var result = -1;
        var cut = RenderPicker(18 * 60, m => result = m);

        MinuteSelect(cut).Change("30");

        Assert.Equal(18 * 60 + 30, result);
    }

    [Fact]
    public void SelectingMidnight_DropsTheMinutesAndDisablesThatControl()
    {
        // There is no 12:15 AM end-of-day under the 1440 convention, so the minutes control has
        // nothing meaningful to offer once Midnight is picked.
        var result = -1;
        var cut = RenderPicker(18 * 60 + 45, m => result = m);

        HourSelect(cut).Change("1440");

        Assert.Equal(24 * 60, result);

        var atMidnight = RenderPicker(24 * 60, _ => { });
        Assert.True(MinuteSelect(atMidnight).HasAttribute("disabled"));
    }

    [Fact]
    public void AtMidnight_TheHourControlStillMovesBackToARealTime()
    {
        var result = -1;
        var cut = RenderPicker(24 * 60, m => result = m);

        HourSelect(cut).Change("1380");

        // Midnight carries no minutes of its own, so 11 PM is the whole answer.
        Assert.Equal(23 * 60, result);
    }
}
