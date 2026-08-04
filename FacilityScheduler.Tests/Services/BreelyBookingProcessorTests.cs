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
}
