using FacilityScheduler.Domain;
using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

public class SearchQueryParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankInput_IsEmptyWithNoNotices(string? raw)
    {
        var query = SearchQueryParser.Parse(raw);

        Assert.True(query.IsEmpty);
        Assert.Empty(query.Notices);
    }

    [Fact]
    public void Parse_BareWords_BecomeTitleTermsInOrder()
    {
        var query = SearchQueryParser.Parse("smith wedding");

        Assert.Equal(["smith", "wedding"], query.TitleTerms);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void Parse_QuotedPhrase_IsOneTermContainingTheSpace()
    {
        var query = SearchQueryParser.Parse("\"junior league\"");

        Assert.Equal(["junior league"], query.TitleTerms);
    }

    [Fact]
    public void Parse_UnterminatedQuote_ConsumesToEndOfInputWithoutThrowing()
    {
        var query = SearchQueryParser.Parse("\"junior league");

        Assert.Equal(["junior league"], query.TitleTerms);
    }

    [Theory]
    [InlineData("day:saturday")]
    [InlineData("day:sat")]
    [InlineData("day:SATURDAY")]
    [InlineData("day:Sat")]
    public void Parse_DayValue_NormalizesToTheSameWeekday(string raw)
    {
        var query = SearchQueryParser.Parse(raw);

        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday }, query.Days);
    }

    [Fact]
    public void Parse_MultipleDayTerms_UnionWithinTheField()
    {
        // day:saturday day:sunday means Saturday OR Sunday - proves OR-within-field.
        var query = SearchQueryParser.Parse("day:saturday day:sunday");

        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, query.Days);
    }

    [Fact]
    public void Parse_BonspielCategory_ResolvesBothFamiliesAndEmitsCollisionNotice()
    {
        var query = SearchQueryParser.Parse("category:bonspiel");

        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.Bonspiel }, query.BookingCategories);
        Assert.Equal(new HashSet<ClubEventCategory> { ClubEventCategory.OutOfTownBonspiels }, query.ClubCategories);
        Assert.Contains(query.Notices, n => n.Kind == SearchNoticeKind.CategoryCollision);
    }

    [Fact]
    public void Parse_ClubOnlyCategory_LeavesBookingSetEmptyButNonNull()
    {
        // This is the assertion that stops the "returns every booking too" bug: an empty non-null
        // set means "constrained to nothing," not "unconstrained."
        var query = SearchQueryParser.Parse("category:outoftownbonspiels");

        Assert.NotNull(query.BookingCategories);
        Assert.Empty(query.BookingCategories!);
        Assert.Equal(new HashSet<ClubEventCategory> { ClubEventCategory.OutOfTownBonspiels }, query.ClubCategories);
    }

    [Theory]
    [InlineData("category:practiceice")]
    [InlineData("category:\"practice ice\"")]
    [InlineData("category:PRACTICE-ICE")]
    public void Parse_PracticeIceCategory_NormalizesRegardlessOfPunctuationOrCase(string raw)
    {
        var query = SearchQueryParser.Parse(raw);

        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.PracticeIce }, query.BookingCategories);
    }

    [Theory]
    [InlineData("category:learntocurl")]
    [InlineData("category:\"learn to curl\"")]
    [InlineData("category:LEARN-TO-CURL")]
    public void Parse_LearnToCurlCategory_NormalizesRegardlessOfPunctuationOrCase(string raw)
    {
        // SearchCategoryVocabulary builds itself from Enum.GetValues<BookingCategory>() rather than a
        // hardcoded list, so a new category is searchable with no change to that file - this pins
        // that it actually worked out that way for D106, not just that it should have.
        var query = SearchQueryParser.Parse(raw);

        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.LearnToCurl }, query.BookingCategories);
    }

    [Fact]
    public void Parse_UnknownCategoryValue_MatchesNothingAndEmitsNotice()
    {
        var query = SearchQueryParser.Parse("category:zamboni");

        Assert.NotNull(query.BookingCategories);
        Assert.Empty(query.BookingCategories!);
        Assert.NotNull(query.ClubCategories);
        Assert.Empty(query.ClubCategories!);
        Assert.Contains(query.Notices, n => n.Kind == SearchNoticeKind.UnknownValue);
    }

    [Fact]
    public void Parse_UnrecognizedPrefix_BecomesLiteralTitleTermWithNotice()
    {
        // Real titles can contain colons (e.g. "League: Tuesday Night") - an unrecognized prefix
        // must fall back to text, not be silently dropped.
        var query = SearchQueryParser.Parse("foo:bar");

        Assert.Equal(["foo:bar"], query.TitleTerms);
        Assert.Contains(query.Notices, n => n.Kind == SearchNoticeKind.UnknownPrefix);
    }

    [Fact]
    public void Parse_EmptyCategoryValue_DoesNotCrash()
    {
        var query = SearchQueryParser.Parse("category:");

        Assert.NotNull(query.BookingCategories);
        Assert.Empty(query.BookingCategories!);
        Assert.Contains(query.Notices, n => n.Kind == SearchNoticeKind.UnknownValue);
    }

    // SearchKindFilter is internal - InternalsVisibleTo lets this assembly reference it, but xUnit's
    // [Theory]/[InlineData] can't put an internal type in a public method signature (CS0051), so the
    // expected value travels as a string and is compared via ToString(), same workaround
    // PublicCalendarEndpointTests uses for ViewMode.
    [Theory]
    [InlineData("type:on-ice", "OnIceOnly")]
    [InlineData("type:onice", "OnIceOnly")]
    [InlineData("type:off-ice", "OffIceOnly")]
    [InlineData("type:office", "OffIceOnly")]
    // The pre-rename tokens stay as silent aliases - they're in staff bookmarks and muscle memory,
    // and rejecting them would break searches that used to work for no benefit.
    [InlineData("type:booking", "OnIceOnly")]
    [InlineData("type:bookings", "OnIceOnly")]
    [InlineData("type:clubevent", "OffIceOnly")]
    [InlineData("type:club-events", "OffIceOnly")]
    public void Parse_TypeValue_SetsTheExpectedKindFilter(string raw, string expected)
    {
        var query = SearchQueryParser.Parse(raw);

        Assert.Equal(expected, query.Kind.ToString());
    }

    [Fact]
    public void Parse_UnknownTypeValue_IsIgnoredWithNotice()
    {
        var query = SearchQueryParser.Parse("type:sandwich");

        Assert.Equal(SearchKindFilter.Any, query.Kind);
        Assert.Contains(query.Notices, n => n.Kind == SearchNoticeKind.UnknownValue);
    }

    [Fact]
    public void Parse_CombinedQuery_PopulatesAllThreeConstraintsAtOnce()
    {
        var query = SearchQueryParser.Parse("category:league day:tuesday junior");

        Assert.Equal(new HashSet<BookingCategory> { BookingCategory.League }, query.BookingCategories);
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Tuesday }, query.Days);
        Assert.Equal(["junior"], query.TitleTerms);
    }
}
