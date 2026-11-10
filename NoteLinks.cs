using System.Text.RegularExpressions;

namespace FacilityScheduler;

/// <summary>
/// Splits free-text notes into plain-text and URL pieces, so the staff dialogs can render the URLs
/// as links (<c>Components/Calendar/LinkifiedText.razor</c>). Only http(s) URLs are recognized, so a
/// link's href can never be a <c>javascript:</c> or other scheme. The public calendar's overlay
/// script (PublicCalendarEndpoint) applies the same rules in the browser; keep the two in step.
/// </summary>
public static partial class NoteLinks
{
    public readonly record struct Segment(string Text, bool IsUrl);

    [GeneratedRegex("""https?://[^\s<>"]+""")]
    private static partial Regex UrlPattern();

    public static IReadOnlyList<Segment> Split(string? text)
    {
        var segments = new List<Segment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var pos = 0;
        foreach (Match m in UrlPattern().Matches(text))
        {
            // Anything trimmed off the end of a URL falls into the next plain-text segment.
            var url = TrimUrl(m.Value);
            if (m.Index > pos)
            {
                segments.Add(new Segment(text[pos..m.Index], false));
            }

            segments.Add(new Segment(url, true));
            pos = m.Index + url.Length;
        }

        if (pos < text.Length)
        {
            segments.Add(new Segment(text[pos..], false));
        }

        return segments;
    }

    // Sentence punctuation after a URL isn't part of it; a closing paren only is when the URL itself
    // opened one (Wikipedia-style links like .../Curling_(sport)).
    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?']".Contains(last))
            {
                url = url[..^1];
            }
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
            {
                url = url[..^1];
            }
            else
            {
                break;
            }
        }

        return url;
    }
}
