using FacilityScheduler.Domain;
using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

public class SearchCategoryVocabularyTests
{
    [Theory]
    [MemberData(nameof(BookingCategories))]
    public void Resolve_ByNormalizedEnumName_FindsBookingCategory(BookingCategory category)
    {
        var resolution = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize(category.ToString()));

        Assert.NotNull(resolution);
        Assert.Contains(category, resolution!.BookingCategories);
    }

    [Theory]
    [MemberData(nameof(BookingCategories))]
    public void Resolve_ByNormalizedDisplayLabel_FindsBookingCategory(BookingCategory category)
    {
        // Guards a future label rename (CalendarStyles.CategoryLabel) from silently breaking search.
        var resolution = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize(CalendarStyles.CategoryLabel(category)));

        Assert.NotNull(resolution);
        Assert.Contains(category, resolution!.BookingCategories);
    }

    [Theory]
    [MemberData(nameof(ClubEventCategories))]
    public void Resolve_ByNormalizedEnumName_FindsClubEventCategory(ClubEventCategory category)
    {
        var resolution = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize(category.ToString()));

        Assert.NotNull(resolution);
        Assert.Contains(category, resolution!.ClubEventCategories);
    }

    [Theory]
    [MemberData(nameof(ClubEventCategories))]
    public void Resolve_ByNormalizedDisplayLabel_FindsClubEventCategory(ClubEventCategory category)
    {
        var resolution = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize(CalendarStyles.ClubEventCategoryLabel(category)));

        Assert.NotNull(resolution);
        Assert.Contains(category, resolution!.ClubEventCategories);
    }

    [Fact]
    public void Resolve_KnownCollisionTokens_ResolveToBothFamilies()
    {
        // Documents the exact known collisions rather than asserting "none collide" - fails loudly
        // if a future rename silently creates a third one that nobody decided about.
        var bonspiel = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize("bonspiel"));
        var other = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize("other"));

        Assert.NotNull(bonspiel);
        Assert.True(bonspiel!.MultipleFamilies);
        Assert.NotNull(other);
        Assert.True(other!.MultipleFamilies);
    }

    [Fact]
    public void Resolve_ClubOnlyToken_DoesNotAlsoResolveToBookingFamily()
    {
        var resolution = SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize("outoftownbonspiels"));

        Assert.NotNull(resolution);
        Assert.False(resolution!.MultipleFamilies);
        Assert.Empty(resolution.BookingCategories);
        Assert.Single(resolution.ClubEventCategories);
    }

    [Fact]
    public void Resolve_UnknownToken_ReturnsNull()
    {
        Assert.Null(SearchCategoryVocabulary.Resolve(SearchCategoryVocabulary.Normalize("zamboni")));
    }

    [Fact]
    public void Resolve_EmptyToken_ReturnsNull()
    {
        Assert.Null(SearchCategoryVocabulary.Resolve(""));
    }

    [Theory]
    [InlineData("PracticeIce", "practiceice")]
    [InlineData("practice ice", "practiceice")]
    [InlineData("Practice-Ice", "practiceice")]
    [InlineData("PRACTICE_ICE", "practiceice")]
    public void Normalize_StripsPunctuationAndCase(string input, string expected)
    {
        Assert.Equal(expected, SearchCategoryVocabulary.Normalize(input));
    }

    public static TheoryData<BookingCategory> BookingCategories() =>
        [.. Enum.GetValues<BookingCategory>()];

    public static TheoryData<ClubEventCategory> ClubEventCategories() =>
        [.. Enum.GetValues<ClubEventCategory>()];
}
