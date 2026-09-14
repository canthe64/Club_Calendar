using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

public class BreelyBookingProcessorTests
{
    [Fact]
    public async Task ClaimsOpenHold_WhenWindowFullyCovered()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        var end = start.AddHours(2);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), end.AddHours(1));

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(1, start, 120, clientName: "Jane Curler") });

        var confirmed = Assert.Single(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy);
        Assert.Equal("Group Event - Jane Curler", confirmed.Subject);
        Assert.Contains(confirmed.SingleValueExtendedProperties!, p => p.Value == "breely:1");
        // Confirms this came from claiming the seeded hold (not the force-book fallback, which would
        // produce an indistinguishable subject/extended-property shape) - the seeded hold is the only
        // hold that exists, so a Hold fragment must remain around the claimed window.
        Assert.Contains(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Tentative);
        Assert.Equal(start, facility.FromUtcResponseString(confirmed.Start!.DateTime!));
    }

    [Fact]
    public async Task Reschedule_ReleasesOldSlot_ClaimsNewSlot()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var originalStart = facility.Today.AddDays(2).AddHours(9);
        var newStart = facility.Today.AddDays(2).AddHours(15);
        // One wide hold covering both the original and rescheduled windows.
        BreelyHarness.SeedOpenHold(gateway, sheet, facility.Today.AddDays(2).AddHours(8), facility.Today.AddDays(2).AddHours(20));

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(2, originalStart, 60) });
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(2, newStart, 60) });

        var confirmed = gateway.Events(sheet).Where(e => e.ShowAs == FreeBusyStatus.Busy).ToList();
        var booking = Assert.Single(confirmed);
        Assert.Equal(newStart, facility.FromUtcResponseString(booking.Start!.DateTime!));
    }

    [Fact]
    public async Task Cancel_ReleasesExistingBooking()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(3).AddHours(18);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(3, start, 60) });
        Assert.Contains(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy);

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(3, start, 60, canceled: true) });

        Assert.DoesNotContain(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy);
    }

    [Fact]
    public async Task Cancel_NoMatchingBooking_NoOpDoesNotThrow()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(3).AddHours(18);

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(999, start, 60, canceled: true) });

        Assert.Empty(gateway.Events(sheet));
    }

    [Fact]
    public async Task DuplicateNotification_SameWindow_DoesNotCreateSecondBooking()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(4).AddHours(19);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        var evt = BreelyTestData.MakeEvent(4, start, 60);
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });

        Assert.Single(gateway.Events(sheet), e => e.ShowAs == FreeBusyStatus.Busy);
    }

    [Fact]
    public async Task NoCoveringHold_ForceBooksFallbackSheets_RoundRobinAcrossBatch()
    {
        // M4: with no open hold anywhere, a multi-sibling batch that all fail to match a hold must
        // spread across sheets by batch position, not all stack onto sheet 1.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var start = facility.Today.AddDays(5).AddHours(10);

        var siblings = new List<BreelyEvent>
        {
            BreelyTestData.MakeEvent(10, start, 60),
            BreelyTestData.MakeEvent(11, start, 60),
            BreelyTestData.MakeEvent(12, start, 60)
        };
        var payload = new BreelyWebhookPayload
        {
            Event = siblings[0],
            Submission = new BreelySubmission { Events = siblings }
        };

        await processor.ProcessAsync(payload);

        for (var i = 0; i < TestFacility.SheetMailboxes.Length && i < siblings.Count; i++)
        {
            Assert.Single(gateway.Events(TestFacility.SheetMailboxes[i]), e => e.ShowAs == FreeBusyStatus.Busy);
        }
    }

    [Fact]
    public async Task StaleSibling_AlreadyClaimed_IsNotMutatedByLaterBatch()
    {
        // M5: a sibling resolved only from submission.events[] (possibly stale) must never mutate an
        // already-claimed booking - only the primary event's own data can change an existing booking.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var anchor = facility.Today.AddDays(6);
        var originalStart = anchor.AddHours(9);
        var otherStart = anchor.AddHours(15);
        BreelyHarness.SeedOpenHold(gateway, sheet, anchor.AddHours(8), anchor.AddHours(20));

        // Event A claimed on its own first.
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(20, originalStart, 60) });

        // A later batch names A again (stale: different time) as a non-primary sibling, alongside a
        // genuinely new primary event B.
        var staleA = BreelyTestData.MakeEvent(20, otherStart, 60); // stale copy of A, wrong time
        var freshB = BreelyTestData.MakeEvent(21, otherStart.AddHours(2), 60);
        var payload = new BreelyWebhookPayload
        {
            Event = freshB,
            Submission = new BreelySubmission { Events = [staleA, freshB] }
        };
        await processor.ProcessAsync(payload);

        var confirmed = gateway.Events(sheet).Where(e => e.ShowAs == FreeBusyStatus.Busy).ToList();
        Assert.Equal(2, confirmed.Count);
        var confirmedStarts = confirmed.Select(e => facility.FromUtcResponseString(e.Start!.DateTime!)).ToList();
        Assert.Contains(originalStart, confirmedStarts); // A untouched by the stale sibling data
        Assert.Contains(otherStart.AddHours(2), confirmedStarts); // B claimed normally
    }

    [Fact]
    public async Task IgnoresNonSheetResource()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var start = facility.Today.AddDays(1).AddHours(9);

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(30, start, 60, bookedWith: "Warm Room Table")
        });

        Assert.All(TestFacility.SheetMailboxes, sheet => Assert.Empty(gateway.Events(sheet)));
    }

    [Fact]
    public async Task MalformedWindow_IsSkippedWithoutThrowing()
    {
        var (processor, gateway, _, _) = BreelyHarness.Build();

        var evt = new BreelyEvent { Id = 40, BookedWith = "Curling Sheet", StartDate = null, StartTime = null, DurationInMinutes = 0 };
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });

        Assert.All(TestFacility.SheetMailboxes, sheet => Assert.Empty(gateway.Events(sheet)));
    }

    // ---- Group Reservation sheet-count expansion (D119) ------------------------------------------

    [Fact]
    public async Task GroupReservation_KnownLabel_ClaimsThatManySheets_OnOneRealEventId()
    {
        // Breely only ever sends ONE event for these - no submission.events[] siblings at all - so
        // this exercises the actual reported shape: a top-level "event" alone, event_type carrying
        // the sheet count instead of a sibling array.
        var fiveSheets = new[] { "sheet1", "sheet2", "sheet3", "sheet4", "sheet5" };
        var (processor, gateway, facility, _) = BreelyHarness.Build(sheetLocalParts: fiveSheets);
        var start = facility.Today.AddDays(10).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(482792, start, 120, eventType: "25-32 Participants")
        });

        var claimedSheets = facility.SheetMailboxes
            .Where(sheet => gateway.Events(sheet).Any(e => e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Busy))
            .ToList();
        Assert.Equal(4, claimedSheets.Count);
    }

    [Fact]
    public async Task GroupReservation_ClaimedSheets_ShareOneBookingGroupId()
    {
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var start = facility.Today.AddDays(10).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(500, start, 60, eventType: "17-24 participants") // 3 sheets
        });

        var allBookings = await sheetBookings.GetBookingsForAllSheetsAsync(start.AddDays(-1), start.AddDays(1));
        // Synthetic sheets 2/3 get their own distinct ExternalBookingId (D119's SyntheticSheetIdOffset,
        // not a shared-prefix scheme), so BookingGroupId - not ExternalBookingId - is the one field
        // every sheet of this reservation actually shares. Find the primary's own group id, then
        // confirm every claimed booking for this window is in it.
        var primary = Assert.Single(allBookings, b => b.ExternalBookingId == "breely:500");
        var claimed = allBookings.Where(b => b.BookingGroupId == primary.BookingGroupId).ToList();
        Assert.Equal(3, claimed.Count);
        Assert.NotEqual(Guid.Empty, primary.BookingGroupId);
    }

    [Fact]
    public async Task GroupReservation_Reschedule_MovesEverySheet_NotJustTheFirst()
    {
        // The actual behavior D119 had to get right: Breely will only ever re-notify this app about
        // the one real event id it knows - that reschedule has to propagate to every sheet this app
        // itself inferred from it, not just the first (authoritativeIds, not the old primaryId check).
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var originalStart = facility.Today.AddDays(11).AddHours(9);
        var newStart = facility.Today.AddDays(11).AddHours(14);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, facility.Today.AddDays(11).AddHours(8), facility.Today.AddDays(11).AddHours(20));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(600, originalStart, 60, eventType: "9-16 participants") // 2 sheets
        });
        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(600, newStart, 60, eventType: "9-16 participants")
        });

        var allBookings = await sheetBookings.GetBookingsForAllSheetsAsync(facility.Today.AddDays(10), facility.Today.AddDays(13));
        var primary = Assert.Single(allBookings, b => b.ExternalBookingId == "breely:600");
        var claimed = allBookings.Where(b => b.BookingGroupId == primary.BookingGroupId).ToList();
        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, b => Assert.Equal(newStart, b.Start));
    }

    [Fact]
    public async Task GroupReservation_Cancel_ReleasesEverySheet()
    {
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, _) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var start = facility.Today.AddDays(12).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(700, start, 60, eventType: "17-24 participants") // 3 sheets
        });
        Assert.Equal(3, facility.SheetMailboxes.Count(sheet => gateway.Events(sheet).Any(e => e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Busy)));

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(700, start, 60, eventType: "17-24 participants", canceled: true)
        });

        Assert.All(facility.SheetMailboxes, sheet => Assert.DoesNotContain(gateway.Events(sheet), e => e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Busy));
    }

    [Fact]
    public async Task GroupReservation_DuplicateDelivery_DoesNotClaimExtraSheets()
    {
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var start = facility.Today.AddDays(13).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        var evt = BreelyTestData.MakeEvent(800, start, 60, eventType: "9-16 participants"); // 2 sheets
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt }); // resend, identical

        var allBookings = await sheetBookings.GetBookingsForAllSheetsAsync(start.AddDays(-1), start.AddDays(1));
        var primary = Assert.Single(allBookings, b => b.ExternalBookingId == "breely:800");
        var claimed = allBookings.Where(b => b.BookingGroupId == primary.BookingGroupId).ToList();
        Assert.Equal(2, claimed.Count);
    }

    [Fact]
    public async Task UnrecognizedEventType_StillClaimsExactlyOneSheet()
    {
        // Regression guard: an ordinary (or unrecognized) event_type must keep behaving exactly as it
        // did before D119 - no expansion, no change to the original single-sheet flow.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(900, start, 60, eventType: "Some future Breely event type we've never seen")
        });

        Assert.Equal(1, TestFacility.SheetMailboxes.Count(s => gateway.Events(s).Any(e => e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Busy)));
    }

    // ---- Unrecognized event_type flagging (D120) --------------------------------------------------

    private static IEnumerable<Microsoft.Graph.Models.Event> TriageMarkers(FakeGraphEventGateway gateway) =>
        gateway.Events(TestFacility.ClubEventsMailbox).Where(e => e.Subject == "⚠ Web booking needs review");

    [Fact]
    public async Task UnrecognizedEventType_ClaimedNormally_AlsoFlagsForReview()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(910, start, 60, eventType: "Some future Breely event type we've never seen")
        });

        var marker = Assert.Single(TriageMarkers(gateway));
        Assert.Contains("Some future Breely event type we've never seen", marker.Body?.Content ?? marker.BodyPreview);
        Assert.Contains("claimed", marker.Body?.Content ?? marker.BodyPreview);
    }

    [Fact]
    public async Task UnrecognizedEventType_NoCoveringHold_CombinesBothReasonsInOneMarker()
    {
        // Not two separate markers for one booking - one marker naming both reasons.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var start = facility.Today.AddDays(1).AddHours(19);
        // No hold seeded anywhere - forces the fallback path.

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(920, start, 60, eventType: "Some future Breely event type we've never seen")
        });

        var marker = Assert.Single(TriageMarkers(gateway));
        var text = marker.Body?.Content ?? marker.BodyPreview ?? "";
        Assert.Contains("didn't match any open hold", text);
        Assert.Contains("Some future Breely event type we've never seen", text);
    }

    [Fact]
    public async Task BlankEventType_DoesNotFlagForReview()
    {
        // Most Breely bookings never carry event_type at all - flagging every one of those would
        // drown out the signal this feature exists to provide.
        var (processor, gateway, facility, _) = BreelyHarness.Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(930, start, 60) // no eventType - defaults to null
        });

        Assert.Empty(TriageMarkers(gateway));
    }

    [Fact]
    public async Task RecognizedEventType_DoesNotFlagForReview()
    {
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, _) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var start = facility.Today.AddDays(1).AddHours(19);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(940, start, 60, eventType: "17-24 participants") // known label, 3 sheets
        });

        Assert.Empty(TriageMarkers(gateway));
    }

    [Fact]
    public async Task UnrecognizedEventType_OnANonPrimarySibling_DoesNotFlag()
    {
        // Scoped to the primary event only (see UnrecognizedEventTypeReason's own doc comment) - a
        // genuine submission.events[] sibling from the original multi-sheet flow isn't re-checked, so
        // an already-working ordinary multi-sheet booking doesn't get flagged once per sheet for a
        // label this feature was never meant to recognize in the first place. The primary's own
        // event_type is a RECOGNIZED label here specifically so its own claim can't also produce a
        // marker - isolating what's under test to the sibling's scoping alone.
        var threeSheets = new[] { "sheet1", "sheet2", "sheet3" };
        var (processor, gateway, facility, _) = BreelyHarness.Build(sheetLocalParts: threeSheets);
        var start = facility.Today.AddDays(5).AddHours(10);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        var primary = BreelyTestData.MakeEvent(950, start, 60, eventType: "Up to 8 participants"); // recognized, 1 sheet
        var sibling = BreelyTestData.MakeEvent(951, start, 60, eventType: "Some type this app has never seen");
        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = primary,
            Submission = new BreelySubmission { Events = [primary, sibling] }
        });

        Assert.Empty(TriageMarkers(gateway));
    }

    // ---- Processing failures are surfaced to staff, not just ILogger (code review C3) --------------

    [Fact]
    public async Task ProcessingException_IsLoggedAtStandardTier_AndFlaggedForReview()
    {
        // Uses its own AppLogService (not the harness's private throwaway one) so this test can read
        // back what actually got written - the whole point of C3 is that this line must land at
        // Standard tier (always written), not Debug (only visible with troubleshooting logging on).
        var appLog = TestAppLog.Create(out _);
        var (processor, gateway, facility, _) = BreelyHarness.Build(appLog: appLog);
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));

        // Simulates a Graph write throwing mid-claim (ClaimHoldAsync's own CreateEventAsync call,
        // the first Graph create this flow makes) - the review's own worst case: a booking that's
        // half-processed and would otherwise vanish with nothing but an ILogger line nobody but
        // someone with Azure portal access could ever find. Exactly-once (not FailCreateAfter, which
        // fails every call from its threshold on) so the triage marker's own, separate create right
        // after it can still succeed - this is testing that C3's catch block runs, not simulating a
        // total Graph outage.
        gateway.FailCreateExactlyOnCall = 1;

        await processor.ProcessAsync(new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(960, start, 60) });

        var lines = await appLog.TailAsync(50);
        Assert.Contains(lines, l => l.Contains("[INFO]") && l.Contains("BreelyProcessingFailed"));

        var marker = Assert.Single(TriageMarkers(gateway));
        var text = marker.Body?.Content ?? marker.BodyPreview ?? "";
        Assert.Contains("960", text);
        Assert.Contains("threw while processing", text);
    }

    [Fact]
    public async Task UnparseableWindow_IsLoggedAtStandardTier_NotJustDebug()
    {
        var appLog = TestAppLog.Create(out _);
        var (processor, gateway, facility, _) = BreelyHarness.Build(appLog: appLog);

        // DurationInMinutes <= 0 makes TryParseWindow fail - the event is skipped entirely, no
        // booking is ever attempted for it.
        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(961, facility.Today.AddDays(1).AddHours(19), durationMinutes: 0)
        });

        var lines = await appLog.TailAsync(50);
        Assert.Contains(lines, l => l.Contains("[INFO]") && l.Contains("WebhookUnparseableWindow"));
    }

    // ---- Shrink reconciliation (D121) ---------------------------------------------------------------

    private static async Task<List<SheetBooking>> ClaimedForPrimary(SheetBookingService sheetBookings, DateTime windowStart, DateTime windowEnd, long primaryId)
    {
        var all = await sheetBookings.GetBookingsForAllSheetsAsync(windowStart, windowEnd);
        var primary = Assert.Single(all, b => b.ExternalBookingId == $"breely:{primaryId}");
        return all.Where(b => b.BookingGroupId == primary.BookingGroupId).ToList();
    }

    [Fact]
    public async Task Shrinking_ReleasesTheNoLongerNeededSheets_KeepingOnlyWhatTheNewLabelCallsFor()
    {
        var fiveSheets = new[] { "sheet1", "sheet2", "sheet3", "sheet4", "sheet5" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: fiveSheets);
        var originalStart = facility.Today.AddDays(14).AddHours(9);
        var newStart = facility.Today.AddDays(14).AddHours(14);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, facility.Today.AddDays(14).AddHours(8), facility.Today.AddDays(14).AddHours(20));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1000, originalStart, 60, eventType: "25-32 Participants") // 4 sheets
        });
        var beforeShrink = await ClaimedForPrimary(sheetBookings, facility.Today.AddDays(13), facility.Today.AddDays(16), 1000);
        Assert.Equal(4, beforeShrink.Count);

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1000, newStart, 60, eventType: "9-16 participants") // shrunk to 2 sheets
        });

        var afterShrink = await ClaimedForPrimary(sheetBookings, facility.Today.AddDays(13), facility.Today.AddDays(16), 1000);
        Assert.Equal(2, afterShrink.Count);
        Assert.All(afterShrink, b => Assert.Equal(newStart, b.Start));

        // The released sheets must be genuinely reopened (a Hold fragment remains), not just silently
        // dropped - same release semantics an ordinary reschedule's old slot already gets. Scoped to
        // the sheets THIS reservation originally used, not every configured sheet - the 5th sheet's own
        // never-touched seeded hold would otherwise look like a false-positive match. No longer requires
        // the reopened fragment's exact Start/End - CancelGroupAsync's own adjacent-hold-absorbing logic
        // (architecture doc §4.8) can legitimately merge it into the pre-existing wider seeded hold
        // rather than leaving a separately-timed fragment, and that's still a correct release either way.
        var stillHeldSheets = afterShrink.Select(b => b.SheetMailbox).ToHashSet();
        var releasedSheets = beforeShrink.Select(b => b.SheetMailbox).Where(s => !stillHeldSheets.Contains(s)).ToList();
        Assert.Equal(2, releasedSheets.Count); // 4 originally claimed, 2 kept
        Assert.All(releasedSheets, sheet => Assert.Contains(gateway.Events(sheet), e =>
            e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Tentative &&
            e.Categories != null && e.Categories.Contains(BookingCategory.GroupEvent.ToString())));
        // And no longer confirmed/busy at the old time on those sheets - the actual claim is gone.
        Assert.All(releasedSheets, sheet => Assert.DoesNotContain(gateway.Events(sheet), e =>
            e.ShowAs == Microsoft.Graph.Models.FreeBusyStatus.Busy));
    }

    [Fact]
    public async Task RelabeledToUnrecognized_ReleasesEveryExtraSheet_KeepingOnlyThePrimarys()
    {
        var fiveSheets = new[] { "sheet1", "sheet2", "sheet3", "sheet4", "sheet5" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: fiveSheets);
        var start = facility.Today.AddDays(15).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1100, start, 60, eventType: "25-32 Participants") // 4 sheets
        });

        // Same time (no reschedule), but the label no longer means anything this app recognizes -
        // still a duplicate-ish notification for the primary's own sheet, but the extras must go.
        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1100, start, 60, eventType: "Some brand new Breely type")
        });

        var claimed = await ClaimedForPrimary(sheetBookings, facility.Today.AddDays(14), facility.Today.AddDays(17), 1100);
        Assert.Single(claimed);
    }

    [Fact]
    public async Task Growing_ClaimsMoreSheets_WithoutReleasingAnyExistingOnes()
    {
        var fiveSheets = new[] { "sheet1", "sheet2", "sheet3", "sheet4", "sheet5" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: fiveSheets);
        var start = facility.Today.AddDays(16).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1200, start, 60, eventType: "9-16 participants") // 2 sheets
        });
        await processor.ProcessAsync(new BreelyWebhookPayload
        {
            Event = BreelyTestData.MakeEvent(1200, start, 60, eventType: "25-32 Participants") // grew to 4 sheets
        });

        var claimed = await ClaimedForPrimary(sheetBookings, facility.Today.AddDays(15), facility.Today.AddDays(18), 1200);
        Assert.Equal(4, claimed.Count);
    }

    [Fact]
    public async Task UnchangedLabelResend_DoesNotReleaseAnySheets()
    {
        var fiveSheets = new[] { "sheet1", "sheet2", "sheet3", "sheet4", "sheet5" };
        var (processor, gateway, facility, sheetBookings) = BreelyHarness.Build(sheetLocalParts: fiveSheets);
        var start = facility.Today.AddDays(17).AddHours(9);
        foreach (var sheet in facility.SheetMailboxes)
        {
            BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-1), start.AddHours(3));
        }

        var evt = BreelyTestData.MakeEvent(1300, start, 60, eventType: "25-32 Participants"); // 4 sheets
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt });
        await processor.ProcessAsync(new BreelyWebhookPayload { Event = evt }); // identical resend

        var claimed = await ClaimedForPrimary(sheetBookings, facility.Today.AddDays(16), facility.Today.AddDays(19), 1300);
        Assert.Equal(4, claimed.Count);
    }
}
