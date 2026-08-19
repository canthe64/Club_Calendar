using FacilityScheduler.Components.Calendar;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// SeriesWizardModal.BuildStatusBannerText - the Step 2 status banner directly above the per-date
/// list. Live-found 2026-08-18: with conflicts clear but a season exclusion in play, the banner said
/// "No conflicts — clear to create" in green while the season-warning banner right above it said a
/// date would be dropped - two banners visibly disagreeing about whether everything was clear.
/// "Clear to create" is now conditioned on there being nothing excluded at all, not just no
/// scheduling conflicts.
/// </summary>
public class SeriesWizardStatusBannerTests
{
    [Fact]
    public void NoConflicts_NoSeasonExclusions_SaysClearToCreate()
    {
        var text = SeriesWizardModal.BuildStatusBannerText(activeConflictCount: 0, seasonExcludedCount: 0);

        Assert.Equal("✓ No conflicts — clear to create", text);
    }

    [Fact]
    public void NoConflicts_SomeSeasonExclusions_DoesNotClaimClearToCreate()
    {
        var text = SeriesWizardModal.BuildStatusBannerText(activeConflictCount: 0, seasonExcludedCount: 2);

        Assert.DoesNotContain("clear to create", text);
        Assert.Contains("No scheduling conflicts", text);
        Assert.Contains("2 date(s)", text);
    }

    [Fact]
    public void ConflictsPresent_SeasonExclusionsIrrelevant_ShowsTheConflictWarning()
    {
        // Unresolved scheduling conflicts take priority over season-exclusion messaging - a
        // conflict is staff's call to skip or accept, so it needs to stay the headline either way.
        var text = SeriesWizardModal.BuildStatusBannerText(activeConflictCount: 3, seasonExcludedCount: 2);

        Assert.Contains("3 date(s) still have conflicts", text);
        Assert.DoesNotContain("season", text);
    }

    [Fact]
    public void ConflictsPresent_NoSeasonExclusions_UnchangedFromBefore()
    {
        var text = SeriesWizardModal.BuildStatusBannerText(activeConflictCount: 1, seasonExcludedCount: 0);

        Assert.Equal("⚠ 1 date(s) still have conflicts", text);
    }
}
