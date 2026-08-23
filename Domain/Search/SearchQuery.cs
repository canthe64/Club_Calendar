namespace FacilityScheduler.Domain.Search;

/// <summary>Which record kind(s) a parsed search is restricted to via a <c>type:</c> term.
/// Named for where an event happens rather than which mailbox it lands on - the vocabulary the rest
/// of the UI now uses. Internal and never serialized, so the rename costs nothing on the wire.</summary>
internal enum SearchKindFilter
{
    Any,
    OnIceOnly,
    OffIceOnly
}

internal enum SearchNoticeKind
{
    /// <summary>A <c>prefix:value</c> token whose prefix isn't <c>category</c>/<c>day</c>/<c>type</c> -
    /// treated as literal title text (real titles can contain colons) rather than dropped.</summary>
    UnknownPrefix,

    /// <summary>A recognized prefix (<c>category:</c>/<c>day:</c>/<c>type:</c>) whose value doesn't
    /// resolve to anything - a deliberate dead end rather than a silent reinterpretation, since the
    /// staff member's intent here (unlike UnknownPrefix) was unambiguous.</summary>
    UnknownValue,

    /// <summary>A <c>category:</c> token that resolved to both a BookingCategory and a
    /// ClubEventCategory at once - most notably "bonspiel" (see SearchCategoryVocabulary).</summary>
    CategoryCollision
}

internal record SearchNotice(SearchNoticeKind Kind, string Message);

/// <summary>
/// A fully parsed staff search - see SearchQueryParser.Parse for the grammar it's built from. Fields
/// combine with AND (a result must satisfy every field that's actually constrained); values within
/// one field combine with OR (<c>day:saturday day:sunday</c> means Saturday OR Sunday, not both at
/// once on the same item).
///
/// <see cref="BookingCategories"/>/<see cref="ClubCategories"/> are nullable sets, not plain lists:
/// <c>null</c> means no <c>category:</c> term ever touched that family (unconstrained - matches
/// based on the query's other fields alone), while a non-null set - even an empty one - means at
/// least one <c>category:</c> term in this query named a category, and every item in this family
/// must have a category from that set to match. An empty non-null set is therefore "constrained to
/// match nothing," which is exactly right when every <c>category:</c> term in the query resolved to
/// the *other* family only (e.g. <c>category:outoftownbonspiels</c> - a real club category with no
/// booking-category counterpart). Modelling this as an empty list instead of null - or reusing one
/// list for "no constraint" and "constrained to nothing" - is precisely the bug the plan calls out:
/// it would turn <c>category:closure</c> into "every booking, plus closures."
/// </summary>
internal record SearchQuery(
    IReadOnlyList<string> TitleTerms,
    HashSet<DayOfWeek>? Days,
    HashSet<BookingCategory>? BookingCategories,
    HashSet<ClubEventCategory>? ClubCategories,
    SearchKindFilter Kind,
    IReadOnlyList<SearchNotice> Notices)
{
    /// <summary>True only for a blank/whitespace-only raw query - nothing parsed at all, not even a
    /// notice. The page reads this as "render the syntax help card, fetch nothing."</summary>
    public bool IsEmpty =>
        TitleTerms.Count == 0 && Days is null && BookingCategories is null && ClubCategories is null
        && Kind == SearchKindFilter.Any && Notices.Count == 0;
}
