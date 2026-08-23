using System.Text;

namespace FacilityScheduler.Domain.Search;

/// <summary>
/// Parses the staff search box's grammar - see the plan's grammar table - into a <see
/// cref="SearchQuery"/>. Pure: no clock, no DI, no Graph. AND across fields, OR within a field:
/// every recognized <c>category:</c>/<c>day:</c> term in the query unions into that field's set, and
/// a result must satisfy every field that ended up constrained.
/// </summary>
internal static class SearchQueryParser
{
    private static readonly Dictionary<string, DayOfWeek> DayVocabulary = BuildDayVocabulary();

    public static SearchQuery Parse(string? raw)
    {
        var titleTerms = new List<string>();
        HashSet<DayOfWeek>? days = null;
        HashSet<BookingCategory>? bookingCategories = null;
        HashSet<ClubEventCategory>? clubCategories = null;
        var kind = SearchKindFilter.Any;
        var notices = new List<SearchNotice>();

        foreach (var token in Tokenize(raw ?? string.Empty))
        {
            var colonIndex = token.IndexOf(':');
            if (colonIndex <= 0)
            {
                // No colon (a plain word), or a colon with nothing before it - not a recognizable
                // prefix either way. The whole token is literal title text.
                titleTerms.Add(token);
                continue;
            }

            var prefix = token[..colonIndex].ToLowerInvariant();
            var value = token[(colonIndex + 1)..];
            var normalizedValue = SearchCategoryVocabulary.Normalize(value);

            switch (prefix)
            {
                case "category":
                    // Constraining the family the instant a category: term appears, even before
                    // knowing whether it resolves - an unknown value still expressed clear category
                    // intent (matches nothing), which is different from never having asked at all
                    // (unconstrained). See SearchQuery's own doc comment on why this has to be a
                    // nullable set, not a plain list.
                    bookingCategories ??= [];
                    clubCategories ??= [];

                    var resolution = SearchCategoryVocabulary.Resolve(normalizedValue);
                    if (resolution is null)
                    {
                        notices.Add(new SearchNotice(SearchNoticeKind.UnknownValue,
                            $"\"category:{value}\" isn't a known category, so it won't match anything."));
                    }
                    else
                    {
                        foreach (var b in resolution.BookingCategories)
                        {
                            bookingCategories.Add(b);
                        }

                        foreach (var c in resolution.ClubEventCategories)
                        {
                            clubCategories.Add(c);
                        }

                        if (resolution.MultipleFamilies)
                        {
                            // "were included in the search" rather than "are shown below" - this
                            // fires whenever the term resolves to both families, regardless of
                            // whether either one actually has a match in the searched range. Saying
                            // "shown below" reads as a promise of results that may not be there.
                            notices.Add(new SearchNotice(SearchNoticeKind.CategoryCollision,
                                $"\"category:{value}\" matches both an on-ice and an off-ice category - both were included in the search."));
                        }
                    }

                    break;

                case "day":
                    days ??= [];
                    if (DayVocabulary.TryGetValue(normalizedValue, out var dayOfWeek))
                    {
                        days.Add(dayOfWeek);
                    }
                    else
                    {
                        notices.Add(new SearchNotice(SearchNoticeKind.UnknownValue,
                            $"\"day:{value}\" isn't a day of the week, so it won't match anything."));
                    }

                    break;

                case "type":
                    // Normalize strips non-alphanumerics, so "on-ice" arrives as "onice" and
                    // "off-ice" as "office". The old booking/clubevent tokens stay as silent aliases:
                    // they're in the wild in staff bookmarks and muscle memory, and rejecting them
                    // would break a search that used to work for no benefit.
                    if (normalizedValue is "onice" or "booking" or "bookings")
                    {
                        kind = SearchKindFilter.OnIceOnly;
                    }
                    else if (normalizedValue is "office" or "clubevent" or "clubevents")
                    {
                        kind = SearchKindFilter.OffIceOnly;
                    }
                    else
                    {
                        notices.Add(new SearchNotice(SearchNoticeKind.UnknownValue,
                            $"\"type:{value}\" isn't \"on-ice\" or \"off-ice\", so it was ignored."));
                    }

                    break;

                default:
                    // Real titles can contain colons, so an unrecognized prefix falls back to literal
                    // text rather than being silently dropped - paired with a notice so staff can see
                    // it wasn't parsed as a filter, in case that wasn't what they meant.
                    titleTerms.Add(token);
                    notices.Add(new SearchNotice(SearchNoticeKind.UnknownPrefix,
                        $"\"{prefix}:\" isn't a recognized search term, so it was matched as text instead."));
                    break;
            }
        }

        return new SearchQuery(titleTerms, days, bookingCategories, clubCategories, kind, notices);
    }

    /// <summary>Splits on whitespace, honoring double-quoted phrases as single tokens (quotes
    /// stripped). An unterminated quote consumes to the end of input instead of throwing - a
    /// forgotten closing quote shouldn't turn a search box into an error page.</summary>
    private static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        void Flush()
        {
            if (hasContent)
            {
                tokens.Add(current.ToString());
            }

            current.Clear();
            hasContent = false;
        }

        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasContent = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }

            current.Append(ch);
            hasContent = true;
        }

        Flush();
        return tokens;
    }

    // Full weekday name and its 3-letter abbreviation, both normalized the same way a typed
    // day: value is - so "Saturday", "SATURDAY", and "sat" all resolve identically.
    private static Dictionary<string, DayOfWeek> BuildDayVocabulary()
    {
        var map = new Dictionary<string, DayOfWeek>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = day.ToString();
            map[SearchCategoryVocabulary.Normalize(name)] = day;
            map[SearchCategoryVocabulary.Normalize(name[..3])] = day;
        }

        return map;
    }
}
