using System.Globalization;
using System.Text.Json;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

/// <summary>Reading a Breely booking's start time. Breely switched start_time from "12:15PM" to a
/// 24-hour "12:15" in early November 2026 without notice, and every booking was skipped as
/// unparseable (live-found 2026-11-10). The window now comes from the ISO 8601 start, which doesn't
/// depend on Breely's display clock, with start_date + start_time - in either form - as a fallback.</summary>
public class BreelyStartTimeTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById(TestFacility.TimeZoneId);

    [Theory]
    // The ISO instant, converted to facility time: Nov 12 is PST (UTC-8), Oct 11 is PDT (UTC-7).
    [InlineData("2026-11-12T20:15:00+00:00", null, null, "2026-11-12T12:15")]
    [InlineData("2026-10-12T02:15:00+00:00", null, null, "2026-10-11T19:15")]
    // The ISO instant wins over a display time that says something else.
    [InlineData("2026-11-12T20:15:00+00:00", "Nov 12, 2026", "9:00", "2026-11-12T12:15")]
    // No ISO field: start_date + start_time, 24-hour (current) or 12-hour (until Nov 2026).
    [InlineData(null, "Nov 12, 2026", "19:30", "2026-11-12T19:30")]
    [InlineData(null, "Nov 12, 2026", "12:15", "2026-11-12T12:15")]
    [InlineData(null, "Nov 12, 2026", "7:30pm", "2026-11-12T19:30")]
    [InlineData(null, "Nov 12, 2026", "12:15PM", "2026-11-12T12:15")]
    public void TheWindowIsReadInFacilityTime(string? iso, string? date, string? time, string expectedStart)
    {
        var evt = new BreelyEvent { StartIso = iso, StartDate = date, StartTime = time, DurationInMinutes = 120 };

        Assert.True(BreelyBookingProcessor.TryParseWindow(evt, Pacific, out var start, out var end));
        Assert.Equal(DateTime.Parse(expectedStart, CultureInfo.InvariantCulture), start);
        Assert.Equal(start.AddMinutes(120), end);
    }

    [Theory]
    [InlineData(null, "Nov 12, 2026", "quarter past noon")]
    [InlineData("not a date", null, null)]
    [InlineData(null, null, null)]
    public void NothingReadable_IsUnparseable(string? iso, string? date, string? time) =>
        Assert.False(BreelyBookingProcessor.TryParseWindow(
            new BreelyEvent { StartIso = iso, StartDate = date, StartTime = time, DurationInMinutes = 120 }, Pacific, out _, out _));

    [Fact]
    public async Task ABookingInBreelysCurrentFormat_ClaimsItsSheets()
    {
        // The field names and value shapes from a real 2026-11-10 payload (customer details left
        // out): a 3-sheet "17-24 Participants" group reservation with a 24-hour start_time.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var start = facility.Today.AddDays(2).AddHours(12.25);
        var iso = new DateTimeOffset(start, Pacific.GetUtcOffset(start)).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        foreach (var sheet in TestFacility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-2), start.AddHours(4));
        }
        var json = $$$"""
            {"event":{"id":575340,"start_date_&_time":"{{{start:ddd, MMM d, yyyy HH:mm}}}","start_date":"{{{start:MMM d, yyyy}}}",
              "start_time":"{{{start:HH:mm}}}","duration_in_minutes":120,"booked_with":"Curling Sheet","canceled":false,
              "event_type":"17-24 Participants","start_date_&_time_(iso_8601)":"{{{iso}}}",
              "admin_url":"https://app.breely.example/calendar?eventId=575340"}}
            """;

        await processor.ProcessAsync(JsonSerializer.Deserialize<BreelyWebhookPayload>(json)!);

        foreach (var sheet in TestFacility.SheetMailboxes)
        {
            var claimed = Assert.Single(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy);
            Assert.Equal(start, facility.FromUtcResponseString(claimed.Start!.DateTime!));
        }
    }

    [Fact]
    public async Task EverySheetOfAGroupReservation_UsesTheIsoStart()
    {
        // The extra sheets are copies the app makes of Breely's one event; each must carry the ISO
        // start too, or it would silently depend on the display-format fallback.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var start = facility.Today.AddDays(2).AddHours(19.5);
        foreach (var sheet in TestFacility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-2), start.AddHours(4));
        }
        var evt = new BreelyEvent
        {
            Id = 575341, StartIso = new DateTimeOffset(start, Pacific.GetUtcOffset(start)).ToString("o", CultureInfo.InvariantCulture),
            DurationInMinutes = 120, BookedWith = "Curling Sheet", EventType = "17-24 Participants"
        };

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });

        Assert.All(TestFacility.SheetMailboxes, sheet => Assert.Single(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy));
    }
}
