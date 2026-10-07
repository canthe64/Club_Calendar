using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// "Resolve this alert" on the Breely webhook's "⚠ Web booking needs review" marker (staff request
/// 2026-09-28): staff had no clear way to clear one. Resolving removes the marker and records who
/// resolved it.
/// </summary>
public class TriageAlertResolveTests : BunitContext
{
    private static ClubEvent Marker(string? eventId = "marker-1") => new()
    {
        EventId = eventId,
        Title = BreelyBookingProcessor.TriageMarkerTitle,
        BookedBy = BreelyBookingProcessor.BookedByLabel,
        Category = ClubEventCategory.Other,
        Start = new DateTime(2026, 10, 3),
        End = new DateTime(2026, 10, 3),
        IsAllDay = true,
        Notes = "Breely event 123 (Jane Customer) matched no open hold."
    };

    private static ClubEvent OrdinaryEvent() => new()
    {
        EventId = "evt-1",
        Title = "Board meeting",
        BookedBy = "Staff Person",
        Category = ClubEventCategory.Meetings,
        Start = new DateTime(2026, 10, 3, 18, 0, 0),
        End = new DateTime(2026, 10, 3, 19, 0, 0)
    };

    private static (ClubEventService Service, FakeGraphEventGateway Gateway, AppLogService Log) BuildService()
    {
        var h = ServiceHarness.Create();
        return (h.ClubEvents, h.Gateway, h.AppLog);
    }

    [Fact]
    public async Task Resolve_RemovesTheMarker_AndLogsWhoResolvedIt_WithoutTheCustomerNotes()
    {
        var (service, gateway, log) = BuildService();
        var marker = await service.CreateAsync(Marker(eventId: null), BreelyBookingProcessor.BookedByLabel);

        await service.ResolveTriageMarkerAsync(marker, "Staff Person");

        Assert.Empty(gateway.Events(TestFacility.ClubEventsMailbox));
        var line = Assert.Single(await log.TailAsync(50), l => l.Contains("TriageAlertResolved"));
        Assert.Contains("Staff Person", line);
        Assert.DoesNotContain("Jane Customer", line);
    }

    [Fact]
    public async Task Resolve_AlreadyGone_IsTreatedAsResolved()
    {
        var (service, _, log) = BuildService();

        await service.ResolveTriageMarkerAsync(Marker(eventId: "no-such-event"), "Staff Person");

        Assert.Contains(await log.TailAsync(50), l => l.Contains("TriageAlertResolved"));
    }

    [Fact]
    public async Task Resolve_AnOrdinaryOffIceEvent_IsRefused()
    {
        var (service, _, _) = BuildService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveTriageMarkerAsync(OrdinaryEvent(), "Staff Person"));
    }

    [Fact]
    public void DetailModal_TriageMarker_OffersResolveInsteadOfEditAndDelete()
    {
        ClubEvent? resolved = null;
        var cut = Render<ClubEventDetailModal>(p => p
            .Add(m => m.ClubEvent, Marker())
            .Add(m => m.OnResolve, (ClubEvent ce) => resolved = ce));

        Assert.DoesNotContain("Edit event", cut.Markup);
        Assert.DoesNotContain("Delete Event", cut.Markup);
        cut.FindAll("span").Single(s => s.TextContent == "Resolve this alert").Click();
        Assert.NotNull(resolved);
    }

    [Fact]
    public void DetailModal_OrdinaryEvent_KeepsEditAndDelete_WithNoResolve()
    {
        var cut = Render<ClubEventDetailModal>(p => p.Add(m => m.ClubEvent, OrdinaryEvent()));

        Assert.Contains("Edit event", cut.Markup);
        Assert.Contains("Delete Event", cut.Markup);
        Assert.DoesNotContain("Resolve this alert", cut.Markup);
    }
}
