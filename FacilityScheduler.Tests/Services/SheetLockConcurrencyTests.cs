using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// Proves SheetBookingService's per-sheet SheetLocks (the only thing standing between two
/// overlapping bookings on the same sheet, per the class's own doc comment) actually serialize
/// concurrent callers. Every test here injects a real await-yield into the fake gateway's read
/// step (DelayDuringCalendarView) - without that, the fake's fully synchronous I/O would let these
/// tests pass "by luck" even with the lock removed, since nothing would ever interleave.
/// </summary>
public class SheetLockConcurrencyTests
{
    private static (SheetBookingService Service, FakeGraphEventGateway Gateway, FacilityConfiguration Facility) Build()
    {
        var h = ServiceHarness.Create();
        h.Gateway.DelayDuringCalendarView = () => Task.Delay(30);
        return (h.Bookings, h.Gateway, h.Facility);
    }

    [Fact]
    public async Task ConcurrentCreates_SameSheetOverlappingTime_OnlyOneSucceeds()
    {
        var (service, gateway, facility) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(18);
        var end = start.AddHours(2);

        Task<GroupBookingResult> Book(string renter) => service.BookAsync(new SheetBooking
        {
            SheetMailbox = sheet,
            Start = start,
            End = end,
            Category = BookingCategory.GroupEvent,
            State = BookingState.Hold,
            RenterName = renter
        });

        var results = await Task.WhenAll(Book("A"), Book("B"), Book("C"), Book("D"));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(3, results.Count(r => !r.IsSuccess));
        Assert.Single(gateway.Events(sheet));
    }

    [Fact]
    public async Task ConcurrentCreateAcrossSheetsAsync_SharedSheetsInReverseOrder_DoesNotDeadlockAndOnlyOneSucceeds()
    {
        var (service, _, facility) = Build();
        var sheets = TestFacility.SheetMailboxes;
        var start = facility.Today.AddDays(2).AddHours(10);
        var end = start.AddHours(1);

        var template = new SheetBooking
        {
            SheetMailbox = "",
            Start = start,
            End = end,
            Category = BookingCategory.League,
            State = BookingState.Confirmed
        };

        // Two callers requesting overlapping sheet sets in opposite orders ([0,1] vs [1,0]) - the
        // exact shape CreateAcrossSheetsAsync's sorted-lock-order comment says avoids deadlock.
        var t1 = service.CreateAcrossSheetsAsync([sheets[0], sheets[1]], template, "tester");
        var t2 = service.CreateAcrossSheetsAsync([sheets[1], sheets[0]], template, "tester");

        var results = await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, results.Count(r => !r.IsSuccess));
    }
}
