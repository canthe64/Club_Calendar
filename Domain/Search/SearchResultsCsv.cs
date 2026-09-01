using System.Text;

namespace FacilityScheduler.Domain.Search;

/// <summary>
/// Pure CSV shaping for a search result set - takes what <see cref="SearchResultsBuilder"/> already
/// matched/grouped/sorted and turns it into bytes, with no I/O of its own so it's directly testable.
/// Deliberately excludes RenterPhone/RenterEmail (operator decision, 2026-08-27): a spreadsheet is
/// exactly the artifact a piece of customer contact data would most easily leave the building in,
/// and the search page itself already tells staff it doesn't search those fields.
/// </summary>
internal static class SearchResultsCsv
{
    internal const string ContentType = "text/csv";

    private static readonly string[] Header =
        ["Date", "Start", "End", "Title", "Type", "Category", "Sheets", "Status", "All day"];

    /// <summary>
    /// A cell whose first character is one of these is a live formula the instant Excel/Sheets opens
    /// the file - CSV formula injection (OWASP). The one field here that can hold arbitrary text is
    /// Title: a staff-typed RenterName, or (via the Breely webhook) a customer-supplied name never
    /// reviewed by anyone at the club before it lands in this export. Neutralized by prefixing a
    /// single quote, the standard convention that forces text interpretation without changing what a
    /// person reads when they open the file.
    /// </summary>
    private static readonly char[] FormulaTriggerChars = ['=', '+', '-', '@', '\t', '\r'];

    internal static byte[] Build(SearchResultsBuilder.Result result, IReadOnlyList<SheetBooking> allBookings)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);

        foreach (var row in result.Upcoming)
        {
            AppendRow(sb, FieldsFor(row, allBookings));
        }

        foreach (var row in result.Past)
        {
            AppendRow(sb, FieldsFor(row, allBookings));
        }

        // A leading UTF-8 BOM, not just UTF-8 bytes: without it, Excel on Windows - the realistic
        // opener for a file called "event-search-*.csv" - guesses Windows-1252 and renders any
        // non-ASCII renter name as mojibake. Encoding.UTF8.GetPreamble() gives the 3 BOM bytes
        // directly, rather than trying to smuggle the U+FEFF character through a string literal -
        // that character has already round-tripped incorrectly once while this file was being
        // written, which is exactly the failure mode this sidesteps entirely.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    internal static string FileNameFor(DateTime generatedAt) => $"event-search-{generatedAt:yyyy-MM-dd}.csv";

    private static string[] FieldsFor(SearchResultsBuilder.ResultRow row, IReadOnlyList<SheetBooking> allBookings)
    {
        if (row.Booking is { } b)
        {
            // Every sheet under this booking's group, not just the one row survived dedup on - a
            // 3-sheet booking must list all 3, matching what the screen's own chip already shows via
            // the same CalendarStyles.SiblingGroup call.
            var sheets = CalendarStyles.SiblingGroup(allBookings, b)
                .Select(s => CalendarStyles.SheetLabel(s.SheetMailbox))
                .Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

            return
            [
                b.Start.ToString("yyyy-MM-dd"),
                b.Start.ToString("yyyy-MM-dd HH:mm"),
                b.End.ToString("yyyy-MM-dd HH:mm"),
                NeutralizeFormula(CalendarStyles.BookingDisplayTitle(b)),
                "On ice",
                CalendarStyles.CategoryLabel(b.Category),
                string.Join(", ", sheets),
                b.State == BookingState.Hold ? "Hold" : "Confirmed",
                "No"
            ];
        }

        var ce = row.ClubEvent!;
        // All-day has no time to show - but a multi-day closure still needs its END DATE visible, so
        // this is the event's own date only (not blank), never a bare time. IsAllDay's End is already
        // the inclusive last day (see ClubEvent.End's own doc comment), which is exactly what belongs
        // here - ExclusiveEnd would print one day past what the event actually covers.
        var start = ce.IsAllDay ? ce.Start.ToString("yyyy-MM-dd") : ce.Start.ToString("yyyy-MM-dd HH:mm");
        var end = ce.IsAllDay ? ce.End.ToString("yyyy-MM-dd") : ce.End.ToString("yyyy-MM-dd HH:mm");

        return
        [
            ce.Start.ToString("yyyy-MM-dd"),
            start,
            end,
            NeutralizeFormula(ce.Title),
            "Off ice",
            CalendarStyles.ClubEventCategoryLabel(ce.Category),
            string.Empty,
            string.Empty,
            ce.IsAllDay ? "Yes" : "No"
        ];
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(QuoteField(fields[i]));
        }

        sb.Append("\r\n");
    }

    // RFC 4180: a field containing a comma, a double quote, or a line break must be wrapped in double
    // quotes, with any double quote inside it doubled. Sheet lists are joined with ", " on purpose
    // (readable in a single cell) rather than a delimiter that would dodge quoting, so this has real
    // work to do on every Sheets cell, not just an edge case.
    private static string QuoteField(string value)
    {
        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuoting)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string NeutralizeFormula(string value) =>
        value.Length > 0 && FormulaTriggerChars.Contains(value[0]) ? "'" + value : value;
}
