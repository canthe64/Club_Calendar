using System.Text.Json;
using FacilityScheduler;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

public class ClubEventCategoryTests
{
    [Fact]
    public void ClubEventCategories_ListsEveryEnumMemberExactlyOnce()
    {
        // The picker renders this list, not Enum.GetValues - so a category added to the enum and
        // forgotten here would exist, parse, and render on the calendar while being impossible to
        // actually select. This is the drift that separating the two lists introduces; this test is
        // what pays for it.
        Assert.Equal(
            Enum.GetValues<ClubEventCategory>().OrderBy(c => c).ToArray(),
            CalendarStyles.ClubEventCategories.OrderBy(c => c).ToArray());

        Assert.Equal(CalendarStyles.ClubEventCategories.Length,
            CalendarStyles.ClubEventCategories.Distinct().Count());
    }

    [Theory]
    [InlineData(ClubEventCategory.OutOfTownBonspiels, "OutOfTownBonspiels")]
    [InlineData(ClubEventCategory.Activities, "Activities")]
    [InlineData(ClubEventCategory.Closure, "Closure")]
    [InlineData(ClubEventCategory.Other, "Other")]
    [InlineData(ClubEventCategory.Meetings, "Meetings")]
    [InlineData(ClubEventCategory.Competitions, "Competitions")]
    public void MemberNames_AreStable_BecauseTheyAreThePublishedWireValue(ClubEventCategory category, string expected)
    {
        // Since D79 these names are what /api/public/availability puts on the wire, and they're also
        // the literal Graph category string ClubEventService parses back. Renaming a member breaks
        // both, and needs the Exchange master category renamed too. If this test fails, the fix is
        // almost certainly to revert the rename rather than update the expected value.
        Assert.Equal(expected, category.ToString());
    }

    [Fact]
    public void PublicApi_SerializesCategoryAsItsName_NotAnOrdinal()
    {
        // Guards the actual configured wire format, via the same entry point Program.cs calls -
        // rebuilding the options here rather than reaching into a running host, but exercising the
        // real configuration method rather than a copy of it.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PublicJsonOptions.Configure(options);

        var json = JsonSerializer.Serialize(
            new PublicClubEventLabel("Board Meeting", ClubEventCategory.Meetings,
                new DateTime(2026, 9, 1, 19, 0, 0), new DateTime(2026, 9, 1, 20, 30, 0), false, false),
            options);

        Assert.Contains("\"category\":\"Meetings\"", json);
        Assert.DoesNotContain("\"category\":4", json);
    }

    [Fact]
    public void PublicApi_StillAcceptsAnOrdinalOnRead()
    {
        // JsonStringEnumConverter reads numbers as well as names, so anything that round-trips an
        // older numeric payload back through this shape keeps working.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PublicJsonOptions.Configure(options);

        var parsed = JsonSerializer.Deserialize<PublicClubEventLabel>(
            """{"title":"x","category":2,"start":"2026-09-01T00:00:00","end":"2026-09-01T00:00:00","isAllDay":true,"marksSheetsUnavailable":false}""",
            options);

        Assert.Equal(ClubEventCategory.Closure, parsed!.Category);
    }

    [Fact]
    public void EveryCategory_HasItsOwnColorAndLightBackground()
    {
        // The switch expressions have a `_ =>` neutral fallback, so a missing category renders as
        // grey rather than throwing - silently indistinguishable from Other on the calendar.
        var colors = CalendarStyles.ClubEventCategories.Select(CalendarStyles.ClubEventCategoryColor).ToList();
        var backgrounds = CalendarStyles.ClubEventCategories.Select(CalendarStyles.ClubEventCategoryLightBg).ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal(backgrounds.Count, backgrounds.Distinct().Count());
    }

    [Fact]
    public void OutOfTownBonspiels_DisplaysAsWordsButStoresAsOneToken()
    {
        // The rename exists to stop staff confusing this with BookingCategory.Bonspiel (a bonspiel on
        // this club's own ice). The label is what they read; the member name is what Graph stores and
        // what provision-categories.ps1 must create as the Exchange master category.
        Assert.Equal("Out of Town Bonspiels",
            CalendarStyles.ClubEventCategoryLabel(ClubEventCategory.OutOfTownBonspiels));
        Assert.Equal("OutOfTownBonspiels", ClubEventCategory.OutOfTownBonspiels.ToString());
    }

    [Fact]
    public void EveryCategory_HasANonEmptyLabel()
    {
        foreach (var category in CalendarStyles.ClubEventCategories)
        {
            Assert.False(string.IsNullOrWhiteSpace(CalendarStyles.ClubEventCategoryLabel(category)));
        }
    }

    [Fact]
    public void Meetings_IsCranberry()
    {
        Assert.Equal("#a63a5d", CalendarStyles.ClubEventCategoryColor(ClubEventCategory.Meetings));
    }

    [Fact]
    public void Meetings_RoundTripsThroughTheGraphCategoryString()
    {
        // ClubEventService reads the category back by name from the event's Categories collection,
        // and falls back to Other on a parse failure - so a name mismatch degrades silently rather
        // than erroring. The provisioning script's master category must use this same literal.
        Assert.True(Enum.TryParse<ClubEventCategory>("Meetings", out var parsed));
        Assert.Equal(ClubEventCategory.Meetings, parsed);
    }
}
