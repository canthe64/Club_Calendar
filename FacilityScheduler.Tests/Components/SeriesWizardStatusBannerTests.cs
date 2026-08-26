using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;

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

/// <summary>
/// SeriesWizardModal.ConflictSummary - the per-date conflict line in Step 2's preview list. Staff
/// feedback: it only said "conflicts with N booking(s)", same complaint already fixed for
/// EventFormModal/SeriesEditModal's conflict lists (this one just hadn't been touched yet).
/// </summary>
public class SeriesWizardConflictSummaryTests
{
    private static SheetBooking Booking(BookingCategory category, string? renterName = null) => new()
    {
        SheetMailbox = "sheet1@test.onmicrosoft.com",
        Start = DateTime.Today.AddHours(18),
        End = DateTime.Today.AddHours(20),
        Category = category,
        State = BookingState.Confirmed,
        RenterName = renterName,
    };

    [Fact]
    public void SingleConflict_ShowsItsTitle()
    {
        var text = SeriesWizardModal.ConflictSummary([Booking(BookingCategory.GroupEvent, "Smith Wedding")]);

        Assert.Equal("Smith Wedding", text);
    }

    [Fact]
    public void MultipleConflictsOnDifferentSheets_ListsEachDistinctTitle()
    {
        var text = SeriesWizardModal.ConflictSummary([
            Booking(BookingCategory.GroupEvent, "Smith Wedding"),
            Booking(BookingCategory.League),
        ]);

        Assert.Equal($"Smith Wedding, {BookingCategory.League}", text);
    }

    [Fact]
    public void TwoSheetsCollidingWithTheSameBooking_ListsItsTitleOnce()
    {
        var text = SeriesWizardModal.ConflictSummary([
            Booking(BookingCategory.League),
            Booking(BookingCategory.League),
        ]);

        Assert.Equal(BookingCategory.League.ToString(), text);
    }

    [Fact]
    public void LongCombinedTitles_AreTruncatedWithAnEllipsis()
    {
        var text = SeriesWizardModal.ConflictSummary([
            Booking(BookingCategory.GroupEvent, "A Very Long Renter Name For A Big Bonspiel Weekend"),
        ]);

        Assert.EndsWith("…", text);
        Assert.DoesNotContain("Bonspiel Weekend", text);
    }
}
