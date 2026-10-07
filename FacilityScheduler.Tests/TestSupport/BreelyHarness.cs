using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Wires up a real BreelyBookingProcessor + SheetBookingService/ClubEventService against a
/// FakeGraphEventGateway, so processor tests exercise the actual service-layer logic (hold-claiming,
/// trimming, conflict rules) rather than a hand-rolled substitute of it.</summary>
public static class BreelyHarness
{
    public static (BreelyBookingProcessor Processor, FakeGraphEventGateway Gateway, FacilityConfiguration Facility, SheetBookingService SheetBookings) Build(
        Func<Task>? delayDuringFindEvents = null, string[]? sheetLocalParts = null, AppLogService? appLog = null)
    {
        // Callers that need to assert on what got written to the app log (code review C3) pass their
        // own pre-built instance in; everyone else gets a private throwaway one.
        var h = ServiceHarness.Create(TestFacility.Create(sheetLocalParts), appLog: appLog);
        h.Gateway.DelayDuringFindEvents = delayDuringFindEvents;
        var processor = new BreelyBookingProcessor(h.Bookings, h.ClubEvents, h.Facility, h.AppLog, NullLogger<BreelyBookingProcessor>.Instance);
        return (processor, h.Gateway, h.Facility, h.Bookings);
    }

    /// <summary>Seeds an open Group Event hold directly on the fake, the same shape
    /// SheetBookingService would have written - avoids needing the private extended-property ids to
    /// set up "there's already an open hold here" starting state.</summary>
    public static string SeedOpenHold(FakeGraphEventGateway gateway, string sheet, DateTime start, DateTime end) =>
        gateway.Seed(sheet, new Event
        {
            Subject = "Available for Group Events",
            ShowAs = FreeBusyStatus.Tentative,
            Categories = [BookingCategory.GroupEvent.ToString()],
            Start = TestFacility.Dtz(start),
            End = TestFacility.Dtz(end)
        });
}
