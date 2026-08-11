using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

public class PracticeIceRulesTests
{
    [Fact]
    public void DurationOptionsMinutes_ShorterThanMinSession_ReturnsEmpty()
    {
        Assert.Empty(PracticeIceRules.DurationOptionsMinutes(30));
    }

    [Fact]
    public void DurationOptionsMinutes_ExactlyMinSession_ReturnsJustThatOne()
    {
        Assert.Equal([60], PracticeIceRules.DurationOptionsMinutes(60));
    }

    [Fact]
    public void DurationOptionsMinutes_StepsBySlotIntervalUpToMax()
    {
        Assert.Equal([60, 90, 120], PracticeIceRules.DurationOptionsMinutes(120));
    }

    [Fact]
    public void DurationOptionsMinutes_MaxNotOnGrid_StopsAtLastFittingOption()
    {
        // 100 minutes of room fits a 60 or 90 minute session, but not a full 120.
        Assert.Equal([60, 90], PracticeIceRules.DurationOptionsMinutes(100));
    }
}
