using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The rendering half of public Notes exposure (D108) - PublicAvailabilityServiceTests covers which
/// Notes reach these DTOs at all (the Breely/staff gate); these tests cover what happens once a DTO
/// already carries a Notes value, which is the one thing AppendDayCell/AppendDayColumn control.
/// Exercised directly (AppendDayCell/AppendDayColumn are internal specifically for this, D60's
/// precedent) rather than through a full ASP.NET Core test host.
/// </summary>
public class PublicCalendarNotesRenderingTests
{
    private static readonly DateTime Day = new(2026, 9, 8);

    private static PublicMonthBooking Booking(string? notes) =>
        new("Tuesday League", "League", Day.AddHours(18), Day.AddHours(20), true, notes);

    [Fact]
    public void MonthCell_BookingWithNotes_CarriesThemInDataNotes()
    {
        var view = new PublicMonthView([Booking("Bring your own broom tonight.")], []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        Assert.Contains("data-notes=\"Bring your own broom tonight.\"", sb.ToString());
    }

    [Fact]
    public void MonthCell_BookingWithNoNotes_HasAnEmptyDataNotesAttribute()
    {
        var view = new PublicMonthView([Booking(null)], []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        Assert.Contains("data-notes=\"\"", sb.ToString());
    }

    [Fact]
    public void MonthCell_ClubEventWithNotes_CarriesThemSeparatelyFromTheClosureNote()
    {
        // A closure event has BOTH data-note ("All sheets closed") and data-notes (the staff Note) -
        // they're deliberately separate attributes so the overlay never renders a genuinely helpful
        // Note in the closure warning's red/bold styling.
        var ce = new PublicClubEventLabel("Ice Plant Maintenance", ClubEventCategory.Closure, Day, Day, true, true, "Back by 6pm.");
        var view = new PublicMonthView([], [ce]);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        var markup = sb.ToString();
        Assert.Contains("data-note=\"All sheets closed\"", markup);
        Assert.Contains("data-notes=\"Back by 6pm.\"", markup);
    }

    [Fact]
    public void MonthCell_NotesContainingMarkup_IsHtmlEncodedNotRawInjected()
    {
        // The concrete stored-XSS risk: Notes is free text (staff-typed, or Breely's own fields) with
        // no upstream sanitization. If this ever reached the page unescaped, it would be a live
        // vulnerability against every anonymous visitor. WebUtility.HtmlEncode must run on it exactly
        // like every other public field already gets.
        var view = new PublicMonthView([Booking("<script>alert(1)</script>")], []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayCell(sb, Day, Day, view);

        var markup = sb.ToString();
        Assert.DoesNotContain("<script>", markup);
        Assert.Contains("&lt;script&gt;", markup);
    }

    [Fact]
    public void DayColumn_AllDayClubEventWithNotes_CarriesThemInDataNotes()
    {
        var ce = new PublicClubEventLabel("Fall Bonspiel", ClubEventCategory.Competitions, Day, Day, true, false, "Spectators welcome.");
        var view = new PublicMonthView([], [ce]);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayColumn(sb, Day, view, allDayRowHeightPx: 24, showHeader: true, isMultiDay: false);

        Assert.Contains("data-notes=\"Spectators welcome.\"", sb.ToString());
    }

    [Fact]
    public void DayColumn_TimedBookingWithMarkupInNotes_IsHtmlEncoded()
    {
        var b = new PublicMonthBooking("Tuesday League", "League", Day.AddHours(18), Day.AddHours(20), true, "<img src=x onerror=alert(1)>");
        var view = new PublicMonthView([b], []);
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendDayColumn(sb, Day, view, allDayRowHeightPx: 0, showHeader: true, isMultiDay: false);

        var markup = sb.ToString();
        Assert.DoesNotContain("<img", markup);
        Assert.Contains("&lt;img", markup);
    }
}
