using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// Proves BreelyBookingProcessor's ExternalIdLocks (guarding against Breely's observed
/// re-sends of the same creation notification) actually serialize concurrent deliveries for the
/// same external booking id - the exact race M5/H2 closed. DelayDuringFindEvents forces a real
/// await-yield inside the lookup step both concurrent calls race on, so the test can't pass by
/// sheer luck of fully-synchronous fake I/O never interleaving.
/// </summary>
public class BreelyLockConcurrencyTests
{
    [Fact]
    public async Task ConcurrentDuplicateWebhookDelivery_SameExternalId_ClaimsExactlyOnce()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build(delayDuringFindEvents: () => Task.Delay(30));
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(4).AddHours(19);
        var end = start.AddHours(2);
        BreelyHarness.SeedOpenHold(gateway, sheet, start.AddHours(-2), end.AddHours(2));

        var evt = BreelyTestData.MakeEvent(777, start, (int)(end - start).TotalMinutes);
        var payload = new BreelyWebhookPayload { Event = evt };

        // Breely has been observed re-sending the same creation notification twice within minutes -
        // simulate two concurrent deliveries for the exact same external id landing at once.
        await Task.WhenAll(processor.ProcessAsync(payload), processor.ProcessAsync(payload));

        var confirmed = gateway.Events(sheet).Where(e => e.ShowAs == FreeBusyStatus.Busy).ToList();
        Assert.Single(confirmed);
    }

    [Fact]
    public async Task ConcurrentDeliveriesForDifferentExternalIds_BothClaimIndependently()
    {
        var (processor, gateway, facility, _) = BreelyHarness.Build(delayDuringFindEvents: () => Task.Delay(30));
        var sheet = TestFacility.SheetMailboxes[0];
        var startA = facility.Today.AddDays(5).AddHours(9);
        var startB = facility.Today.AddDays(5).AddHours(14);
        BreelyHarness.SeedOpenHold(gateway, sheet, startA.AddHours(-1), startA.AddHours(3));
        BreelyHarness.SeedOpenHold(gateway, sheet, startB.AddHours(-1), startB.AddHours(3));

        var payloadA = new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(101, startA, 60) };
        var payloadB = new BreelyWebhookPayload { Event = BreelyTestData.MakeEvent(102, startB, 60) };

        await Task.WhenAll(processor.ProcessAsync(payloadA), processor.ProcessAsync(payloadB));

        var confirmed = gateway.Events(sheet).Where(e => e.ShowAs == FreeBusyStatus.Busy).ToList();
        Assert.Equal(2, confirmed.Count);
    }
}
