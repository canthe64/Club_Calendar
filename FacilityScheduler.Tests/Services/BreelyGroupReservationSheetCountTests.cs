using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// D119: the operator-supplied event_type-label-to-sheet-count table (staff-maintained in Breely's own
/// admin panel, §8/D119). Pins every label the operator gave verbatim, case-insensitivity (labels are
/// hand-typed in Breely and can't be guaranteed to always match casing exactly), the "Extended session"
/// prefix match (those carry unspecified extra text after the hour count), and that anything else -
/// including a blank/missing label - falls through to null, which ExpandForGroupReservation then treats
/// as an ordinary single-sheet event exactly as it always has.
/// </summary>
public class BreelyGroupReservationSheetCountTests
{
    [Theory]
    [InlineData("Up to 8 participants", 1)]
    [InlineData("9-16 participants", 2)]
    [InlineData("17-24 participants", 3)]
    [InlineData("25-32 participants", 4)]
    [InlineData("33-40 participants", 5)]
    public void SheetCountForEventType_KnownParticipantLabel_ReturnsItsSheetCount(string eventType, int expected)
    {
        Assert.Equal(expected, BreelyBookingProcessor.SheetCountForEventType(eventType));
    }

    [Theory]
    [InlineData("up to 8 PARTICIPANTS")]
    [InlineData("25-32 PARTICIPANTS")]
    [InlineData("  25-32 participants  ")] // staff-editable in Breely - stray whitespace shouldn't break the match
    public void SheetCountForEventType_IsCaseInsensitiveAndTrims(string eventType)
    {
        Assert.NotNull(BreelyBookingProcessor.SheetCountForEventType(eventType));
    }

    // The real payload that started this investigation (§8/D117) used "25-32 Participants" - pinned
    // directly since it's the concrete case this feature exists to fix.
    [Fact]
    public void SheetCountForEventType_TheOriginallyReportedLabel_ReturnsFour()
    {
        Assert.Equal(4, BreelyBookingProcessor.SheetCountForEventType("25-32 Participants"));
    }

    [Theory]
    [InlineData("Extended session - 3-Hours (weekday evening)")]
    [InlineData("Extended session - 3-Hours")]
    [InlineData("extended session - 3-hours - anything after")]
    public void SheetCountForEventType_Extended3HourLabel_ReturnsFive(string eventType)
    {
        Assert.Equal(5, BreelyBookingProcessor.SheetCountForEventType(eventType));
    }

    [Theory]
    [InlineData("Extended session - 4-Hours (weekend)")]
    [InlineData("Extended session - 4-Hours")]
    public void SheetCountForEventType_Extended4HourLabel_ReturnsFive(string eventType)
    {
        Assert.Equal(5, BreelyBookingProcessor.SheetCountForEventType(eventType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Curling Sheet")]
    [InlineData("Try Curling Weekday Group Reservation")] // the event_type_category, not event_type - never the field this reads
    [InlineData("41-48 participants")] // outside the known ranges - not a silent guess
    public void SheetCountForEventType_UnrecognizedOrBlank_ReturnsNull(string? eventType)
    {
        Assert.Null(BreelyBookingProcessor.SheetCountForEventType(eventType));
    }
}
