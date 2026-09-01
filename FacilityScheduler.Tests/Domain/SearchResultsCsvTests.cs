using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Domain.Search;

namespace FacilityScheduler.Tests.Domain;

/// <summary>
/// Pure CSV shaping for the search export. Phone/email exclusion, the formula-injection guard, and
/// the UTF-8 BOM are all correctness properties here, not polish - each one is a real way a staff
/// member's export could leak data, execute an attacker's formula, or garble on the machine that
/// actually opens it (Excel on Windows).
/// </summary>
public class SearchResultsCsvTests
{
    private static readonly IReadOnlyList<SheetBooking> NoBookings = [];

    private static SheetBooking Booking(string sheet, Guid groupId, DateTime start, DateTime end, string? renterName, BookingState state = BookingState.Confirmed, BookingCategory category = BookingCategory.GroupEvent, string? phone = null, string? email = null) => new()
    {
        SheetMailbox = sheet,
        EventId = Guid.NewGuid().ToString(),
        BookingGroupId = groupId,
        Category = category,
        State = state,
        RenterName = renterName,
        RenterPhone = phone,
        RenterEmail = email,
        Start = start,
        End = end
    };

    private static ClubEvent Event(string title, DateTime start, DateTime end, bool isAllDay, ClubEventCategory category = ClubEventCategory.Meetings) => new()
    {
        Title = title,
        Category = category,
        IsAllDay = isAllDay,
        Start = start,
        End = end
    };

    private static SearchResultsBuilder.Result Result(List<SearchResultsBuilder.ResultRow> upcoming) =>
        new(upcoming, [], upcoming.Count(r => r.Booking is not null), upcoming.Count(r => r.ClubEvent is not null));

    // Skips the 3-byte BOM explicitly - Encoding.UTF8.GetString does NOT strip a leading BOM the way
    // a StreamReader's auto-detection would, so decoding the raw bytes would otherwise leave a
    // U+FEFF character glued onto the front of "Date", breaking every line/field comparison below.
    private static string Decode(byte[] csv) => Encoding.UTF8.GetString(csv, 3, csv.Length - 3);

    private static string[] Lines(byte[] csv) =>
        Decode(csv).TrimEnd('\r', '\n').Split("\r\n");

    /// <summary>
    /// A real RFC 4180 field split - NOT a naive comma-split, which breaks the moment a field's own
    /// content contains a comma (exactly what the formula-injection test data does: "@SUM(1,1)" is
    /// legitimately one field once quoted). Handles a doubled "" inside a quoted field.
    /// </summary>
    private static string[] Fields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

    [Fact]
    public void StartsWithAUtf8Bom()
    {
        var csv = SearchResultsCsv.Build(Result([]), NoBookings);

        Assert.Equal(0xEF, csv[0]);
        Assert.Equal(0xBB, csv[1]);
        Assert.Equal(0xBF, csv[2]);
    }

    [Fact]
    public void HeaderRow_MatchesTheAgreedColumns()
    {
        var csv = SearchResultsCsv.Build(Result([]), NoBookings);

        Assert.Equal("Date,Start,End,Title,Type,Category,Sheets,Status,All day", Lines(csv)[0]);
    }

    [Fact]
    public void OnIceBooking_ListsEverySheetInItsGroup()
    {
        var groupId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var end = start.AddHours(2);
        var bookings = new List<SheetBooking>
        {
            Booking("sheet3@example.com", groupId, start, end, "Anthe Bonspiel"),
            Booking("sheet1@example.com", groupId, start, end, "Anthe Bonspiel"),
            Booking("sheet2@example.com", groupId, start, end, "Anthe Bonspiel")
        };
        var row = new SearchResultsBuilder.ResultRow(start, end, bookings[0], null);

        var csv = SearchResultsCsv.Build(Result([row]), bookings);

        var line = Lines(csv)[1];
        Assert.Contains("\"Sheet 1, Sheet 2, Sheet 3\"", line);
        Assert.Contains("On ice", line);
        Assert.Contains("Confirmed", line);
    }

    [Fact]
    public void HoldBooking_ReportsStatusAsHold()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(2), "Open Hold", state: BookingState.Hold);
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(2), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        Assert.Contains(",Hold,", Lines(csv)[1]);
    }

    [Fact]
    public void TimedClubEvent_HasNoSheetsOrStatus_AndAllDayIsNo()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var ce = Event("Board Meeting", start, start.AddHours(1), isAllDay: false);
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(1), null, ce);

        var csv = SearchResultsCsv.Build(Result([row]), NoBookings);

        var fields = Fields(Lines(csv)[1]);
        Assert.Equal("Off ice", fields[4]);
        Assert.Equal(string.Empty, fields[6]); // Sheets
        Assert.Equal(string.Empty, fields[7]); // Status
        Assert.Equal("No", fields[8]);
    }

    [Fact]
    public void AllDayMultiDayClubEvent_ShowsBothDatesWithNoTime()
    {
        // The case that would otherwise lose information: an all-day event has no time component,
        // but a 3-day closure's END DATE still has to survive into the export.
        var start = new DateTime(2026, 9, 5);
        var end = new DateTime(2026, 9, 7); // inclusive last day, per ClubEvent.End's own convention
        var ce = Event("Fall Ice Maintenance", start, end, isAllDay: true, category: ClubEventCategory.Closure);
        var row = new SearchResultsBuilder.ResultRow(start, end, null, ce);

        var csv = SearchResultsCsv.Build(Result([row]), NoBookings);

        var fields = Fields(Lines(csv)[1]);
        Assert.Equal("2026-09-05", fields[0]); // Date
        Assert.Equal("2026-09-05", fields[1]); // Start - date only, no time
        Assert.Equal("2026-09-07", fields[2]); // End - the real last day, not one day past it
        Assert.Equal("Yes", fields[8]);
    }

    [Fact]
    public void PhoneAndEmail_NeverAppearAnywhereInTheFile()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(2), "Anthe / Curry",
            phone: "206-555-0148", email: "events@antheco.example");
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(2), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        var text = Decode(csv);
        Assert.DoesNotContain("206-555-0148", text);
        Assert.DoesNotContain("events@antheco.example", text);
    }

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1 fake", "'+1 fake")]
    [InlineData("-2 fake", "'-2 fake")]
    [InlineData("@SUM(1,1)", "'@SUM(1,1)")]
    public void TitleStartingWithAFormulaTrigger_IsNeutralized(string title, string expectedField)
    {
        // A real exposure, not a theoretical one: RenterName on a Breely-sourced booking comes from
        // the external platform's ClientFullName, reviewed by nobody at the club before export.
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(1), title);
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(1), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        var fields = Fields(Lines(csv)[1]);
        Assert.Equal(expectedField, fields[3]);
    }

    [Fact]
    public void OrdinaryTitle_IsNotAltered()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(1), "Smith Wedding");
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(1), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        Assert.Equal("Smith Wedding", Fields(Lines(csv)[1])[3]);
    }

    [Fact]
    public void TitleContainingAComma_IsQuoted()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(1), "Anthe, Curry & Co.");
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(1), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        Assert.Contains("\"Anthe, Curry & Co.\"", Lines(csv)[1]);
    }

    [Fact]
    public void TitleContainingADoubleQuote_IsEscapedByDoubling()
    {
        var start = new DateTime(2026, 9, 1, 18, 0, 0);
        var booking = Booking("sheet1@example.com", Guid.NewGuid(), start, start.AddHours(1), "The \"Big\" Bonspiel");
        var row = new SearchResultsBuilder.ResultRow(start, start.AddHours(1), booking, null);

        var csv = SearchResultsCsv.Build(Result([row]), [booking]);

        Assert.Contains("\"The \"\"Big\"\" Bonspiel\"", Lines(csv)[1]);
    }

    [Fact]
    public void EveryMatchIsIncluded_NoRowCap()
    {
        var rows = Enumerable.Range(0, 400).Select(i =>
        {
            var start = new DateTime(2026, 9, 1, 18, 0, 0).AddMinutes(i);
            var b = Booking($"sheet{i}@example.com", Guid.NewGuid(), start, start.AddMinutes(30), $"Booking {i}");
            return (Row: new SearchResultsBuilder.ResultRow(start, start.AddMinutes(30), b, null), Booking: b);
        }).ToList();

        var csv = SearchResultsCsv.Build(Result(rows.Select(r => r.Row).ToList()), rows.Select(r => r.Booking).ToList());

        // Header + 400 data rows.
        Assert.Equal(401, Lines(csv).Length);
    }
}
