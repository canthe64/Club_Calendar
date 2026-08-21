namespace FacilityScheduler.Domain.Search;

/// <summary>
/// Normalized token -&gt; which BookingCategory/ClubEventCategory members it resolves to. Built from
/// <c>Enum.GetValues</c> plus <see cref="CalendarStyles.CategoryLabel"/>/<see
/// cref="CalendarStyles.ClubEventCategoryLabel"/>, so a category is findable by either its raw enum
/// name or its human display label - the same normalization (lowercase, strip non-alphanumerics)
/// SearchQueryParser applies to a typed <c>category:</c> value means "practiceice",
/// "practice ice", and "Practice-Ice" all land on the same key as the enum member itself.
///
/// One deliberate alias on top of that: "bonspiel" also resolves to
/// <see cref="ClubEventCategory.OutOfTownBonspiels"/>, alongside its natural
/// <see cref="BookingCategory.Bonspiel"/> hit. D81 renamed that club category specifically because
/// staff conflate the two ("bonspiel" is nobody's first guess for "Out of Town Bonspiels"), so a
/// search reflecting the old, still-natural instinct should find both rather than silently missing
/// the club-event half. Everything downstream of this table treats it exactly like "Other" - a
/// token that happens to resolve to more than one family - so the collision handling in
/// SearchQueryParser needs no special case for it at all; the alias is the only thing that's special.
/// </summary>
internal static class SearchCategoryVocabulary
{
    public sealed record Resolution(IReadOnlyList<BookingCategory> BookingCategories, IReadOnlyList<ClubEventCategory> ClubEventCategories)
    {
        public bool MultipleFamilies => BookingCategories.Count > 0 && ClubEventCategories.Count > 0;
    }

    private static readonly IReadOnlyDictionary<string, Resolution> Vocabulary = Build();

    public static Resolution? Resolve(string normalizedToken) =>
        normalizedToken.Length > 0 && Vocabulary.TryGetValue(normalizedToken, out var resolution) ? resolution : null;

    /// <summary>Lowercase, alphanumerics only - shared by every prefixed value SearchQueryParser
    /// reads (<c>category:</c>/<c>day:</c>/<c>type:</c>), so "PracticeIce", "practice ice", and
    /// "Practice-Ice" are indistinguishable by the time they reach this table.</summary>
    internal static string Normalize(string value) =>
        new string([.. value.Where(char.IsLetterOrDigit)]).ToLowerInvariant();

    private static IReadOnlyDictionary<string, Resolution> Build()
    {
        var bookings = new Dictionary<string, List<BookingCategory>>();
        var clubEvents = new Dictionary<string, List<ClubEventCategory>>();

        void AddBooking(string key, BookingCategory category)
        {
            if (!bookings.TryGetValue(key, out var list))
            {
                bookings[key] = list = [];
            }

            if (!list.Contains(category))
            {
                list.Add(category);
            }
        }

        void AddClubEvent(string key, ClubEventCategory category)
        {
            if (!clubEvents.TryGetValue(key, out var list))
            {
                clubEvents[key] = list = [];
            }

            if (!list.Contains(category))
            {
                list.Add(category);
            }
        }

        // BookingCategory.Event is deliberately included here even though CalendarStyles.SheetCategories
        // excludes it from the booking picker (it's reserved for Club Events, see that field's own
        // comment) - legacy data tagged Event predates that split and is still findable this way, per
        // the plan's "flagged for the operator" note. The search help card just doesn't advertise it.
        foreach (var category in Enum.GetValues<BookingCategory>())
        {
            AddBooking(Normalize(category.ToString()), category);
            AddBooking(Normalize(CalendarStyles.CategoryLabel(category)), category);
        }

        foreach (var category in Enum.GetValues<ClubEventCategory>())
        {
            AddClubEvent(Normalize(category.ToString()), category);
            AddClubEvent(Normalize(CalendarStyles.ClubEventCategoryLabel(category)), category);
        }

        // The one deliberate alias - see the class doc comment.
        AddClubEvent(Normalize(BookingCategory.Bonspiel.ToString()), ClubEventCategory.OutOfTownBonspiels);

        var result = new Dictionary<string, Resolution>();
        foreach (var key in bookings.Keys.Concat(clubEvents.Keys).Distinct())
        {
            var b = bookings.TryGetValue(key, out var bl) ? (IReadOnlyList<BookingCategory>)bl : [];
            var c = clubEvents.TryGetValue(key, out var cl) ? (IReadOnlyList<ClubEventCategory>)cl : [];
            result[key] = new Resolution(b, c);
        }

        return result;
    }
}
