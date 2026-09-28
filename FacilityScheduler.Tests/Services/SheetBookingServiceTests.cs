using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Tests.Services;

public class SheetBookingServiceTests
{
    private static (SheetBookingService Service, FakeGraphEventGateway Gateway, FacilityConfiguration Facility, SchedulingWindowService Window) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var appLog = TestAppLog.Create();
        var viewCache = new ViewCacheRegistry(cache);
        var window = new SchedulingWindowService(appLog, viewCache);
        var service = new SheetBookingService(gateway, cache, facility, appLog, viewCache, window);
        return (service, gateway, facility, window);
    }

    [Fact]
    public async Task CreateAsync_OverlappingTimeOnSameSheet_ReturnsConflictAndWritesNothing()
    {
        var (service, gateway, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(18);

        var first = await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");
        Assert.True(first.IsSuccess);

        var second = await service.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start.AddMinutes(30), End = start.AddMinutes(90), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");

        Assert.False(second.IsSuccess);
        Assert.Single(second.Conflicts);
        Assert.Single(gateway.Events(sheet)); // nothing written for the rejected booking
    }

    [Fact]
    public async Task CreateAsync_NonOverlappingTime_Succeeds()
    {
        var (service, _, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(9);

        var first = await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");
        var second = await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start.AddHours(2), End = start.AddHours(3), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task CreateAsync_ImmediatelyAdjacentBooking_SucceedsDespiteGraphsInclusiveCalendarViewBoundary()
    {
        // Live-found 2026-08-26: a 1pm-6pm booking request was rejected as conflicting with a
        // pre-existing 9am-1pm event, because Graph's real calendarView returns events touching the
        // query boundary, not just ones that actually overlap it. The default fake gateway already
        // performs a correct strict-overlap query, so it can't reproduce that - flip on the quirk
        // simulation to prove SheetBookingService's own re-check (not the gateway) is what protects
        // against it.
        var (service, gateway, facility, _) = Build();
        gateway.SimulateInclusiveBoundaryQuirk = true;
        var sheet = TestFacility.SheetMailboxes[0];
        var day = facility.Today.AddDays(1);

        var morning = await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = day.AddHours(9), End = day.AddHours(13), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");
        Assert.True(morning.IsSuccess);

        var afternoon = await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = day.AddHours(13), End = day.AddHours(18), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        }, "tester");

        Assert.True(afternoon.IsSuccess);
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_ConflictOnOneSheet_CreatesNothingOnAnySheet()
    {
        var (service, gateway, facility, _) = Build();
        var sheets = TestFacility.SheetMailboxes;
        var start = facility.Today.AddDays(1).AddHours(18);

        // Pre-existing booking on sheet[1] only.
        await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheets[1], Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");

        var result = await service.CreateAcrossSheetsAsync([sheets[0], sheets[1]], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.Bonspiel, State = BookingState.Confirmed
        }, "tester");

        Assert.False(result.IsSuccess);
        Assert.Empty(gateway.Events(sheets[0])); // all-or-nothing: sheet[0] was free but nothing was written there either
        Assert.Single(gateway.Events(sheets[1])); // only the pre-existing booking, no new one
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_MultiDaySpan_SucceedsAcrossAllSheetsAndDays()
    {
        // Nothing in the service layer assumes same-day - proving that here rather than only
        // assuming it, per the multi-day booking plan. A 3-day bonspiel across 2 sheets.
        var (service, gateway, facility, _) = Build();
        var sheets = TestFacility.SheetMailboxes;
        var start = facility.Today.AddDays(1).AddHours(18);
        var end = facility.Today.AddDays(3).AddHours(20);

        var result = await service.CreateAcrossSheetsAsync([sheets[0], sheets[1]], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = end, Category = BookingCategory.Bonspiel, State = BookingState.Confirmed, RenterName = "Fall Mixed Bonspiel"
        }, "tester");

        Assert.True(result.IsSuccess);
        Assert.Single(gateway.Events(sheets[0]));
        Assert.Single(gateway.Events(sheets[1]));
        var writtenEnd = facility.FromUtcResponseString(gateway.Events(sheets[0])[0].End!.DateTime!);
        Assert.Equal(end, writtenEnd); // the full 3-day span, not truncated to one day
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_MultiDaySpan_ConflictOnLaterDayOfOneSheet_CreatesNothingOnAnySheet()
    {
        var (service, gateway, facility, _) = Build();
        var sheets = TestFacility.SheetMailboxes;

        // Pre-existing booking on sheet[1], on the SECOND day of the span the multi-day booking
        // below will request - only reachable if the conflict check looks past day one.
        var existingStart = facility.Today.AddDays(2).AddHours(19);
        await service.CreateConfirmedAsync(new SheetBooking
        {
            SheetMailbox = sheets[1], Start = existingStart, End = existingStart.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed
        }, "tester");

        var spanStart = facility.Today.AddDays(1).AddHours(18);
        var spanEnd = facility.Today.AddDays(3).AddHours(20);
        var result = await service.CreateAcrossSheetsAsync([sheets[0], sheets[1]], new SheetBooking
        {
            SheetMailbox = "", Start = spanStart, End = spanEnd, Category = BookingCategory.Bonspiel, State = BookingState.Confirmed
        }, "tester");

        Assert.False(result.IsSuccess);
        Assert.Empty(gateway.Events(sheets[0])); // all-or-nothing: sheet[0] was free but nothing was written there either
        Assert.Single(gateway.Events(sheets[1])); // only the pre-existing booking, no new one
    }

    [Fact]
    public async Task UpdateGroupAsync_DoesNotConflictWithItsOwnUnmovedEvent()
    {
        var (service, _, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(18);

        var created = await service.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = start.AddHours(1), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");
        Assert.True(created.IsSuccess);
        var booking = created.Booking!;

        var result = await service.UpdateGroupAsync([booking], new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = start.AddHours(1), Category = BookingCategory.GroupEvent, State = BookingState.Hold, Notes = "updated"
        }, "tester");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ClaimHoldAsync_DropsRemainderShorterThanMinimumInterval_KeepsRemainderAtOrAboveIt()
    {
        var (service, gateway, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var holdStart = facility.Today.AddDays(1).AddHours(9);
        var holdEnd = holdStart.AddHours(3); // 9:00-12:00
        gateway.Seed(sheet, new Event
        {
            Subject = "Available for Group Events", ShowAs = FreeBusyStatus.Tentative,
            Categories = [BookingCategory.GroupEvent.ToString()], Start = TestFacility.Dtz(holdStart), End = TestFacility.Dtz(holdEnd)
        });

        // Claim 9:30-11:00: leaves a 30-min remainder before (below the 60-min default minimum,
        // dropped) and a 60-min remainder after (at the minimum, kept).
        var claimStart = holdStart.AddMinutes(30);
        var claimEnd = holdStart.AddHours(2);
        var claimed = await service.ClaimHoldAsync(claimStart, claimEnd, new SheetBooking
        {
            SheetMailbox = "", Start = claimStart, End = claimEnd, Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
        }, Guid.NewGuid());

        Assert.NotNull(claimed);
        var events = gateway.Events(sheet);
        Assert.Equal(2, events.Count); // the confirmed claim + the surviving 60-min remainder; the 30-min sliver is gone entirely
        Assert.Single(events, e => e.ShowAs == FreeBusyStatus.Busy);
        var remainder = Assert.Single(events, e => e.ShowAs == FreeBusyStatus.Tentative);
        Assert.Equal(claimEnd, facility.FromUtcResponseString(remainder.Start!.DateTime!));
        Assert.Equal(holdEnd, facility.FromUtcResponseString(remainder.End!.DateTime!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the hold is a recurring occurrence - deleted and recreated rather than patched
    public async Task ClaimHoldAsync_MultiSheetClaimInTheMiddle_LeftoverFragmentsShareOneGroupPerSide(bool holdIsSeriesOccurrence)
    {
        // Live-found 2026-09-28 (screenshot): a 5-sheet Breely booking claimed 5-7PM out of a
        // 12-10PM hold on every sheet, and the 12-5 and 7-10 leftovers showed as a separate chip per
        // sheet - each sheet's leftover got its own fresh group id.
        var (service, gateway, facility, _) = Build();
        var sheets = TestFacility.SheetMailboxes.Take(3).ToList();
        var day = facility.Today.AddDays(2);
        foreach (var sheet in sheets)
        {
            gateway.Seed(sheet, new Event
            {
                Subject = "Group Events Availability", ShowAs = FreeBusyStatus.Tentative,
                Categories = [BookingCategory.GroupEvent.ToString()],
                Start = TestFacility.Dtz(day.AddHours(12)), End = TestFacility.Dtz(day.AddHours(22)),
                SeriesMasterId = holdIsSeriesOccurrence ? $"master-{sheet}" : null
            });
        }

        var claimGroupId = Guid.NewGuid();
        foreach (var _ in sheets)
        {
            Assert.NotNull(await service.ClaimHoldAsync(day.AddHours(17), day.AddHours(19), new SheetBooking
            {
                SheetMailbox = "", Start = day.AddHours(17), End = day.AddHours(19), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed, RenterName = "Robert"
            }, claimGroupId));
        }

        var holds = (await service.GetBookingsForAllSheetsAsync(day, day.AddDays(1)))
            .Where(b => b.State == BookingState.Hold).ToList();
        var before = holds.Where(b => b.End == day.AddHours(17)).ToList();
        var after = holds.Where(b => b.Start == day.AddHours(19)).ToList();
        Assert.Equal(3, before.Count);
        Assert.Equal(3, after.Count);
        Assert.Single(before.Select(b => b.BookingGroupId).Distinct());
        Assert.Single(after.Select(b => b.BookingGroupId).Distinct());
        Assert.NotEqual(Guid.Empty, before[0].BookingGroupId);
        Assert.NotEqual(claimGroupId, before[0].BookingGroupId); // never mistaken for a sibling of the claimed booking
        // ...which is what the calendars actually group on: one chip each side, not one per sheet.
        Assert.Equal(3, CalendarStyles.SiblingGroup(holds, before[0]).Count);
        Assert.Equal(3, CalendarStyles.SiblingGroup(holds, after[0]).Count);
    }

    [Fact]
    public async Task CancelGroupAsync_ReopenMergingNeighbours_AcrossSheets_ReopenedHoldsShareOneGroup()
    {
        // Same bug as above on the way back: releasing a multi-sheet booking into the leftover holds
        // on either side merged each sheet into one wider hold, each with its own fresh group id.
        var (service, gateway, facility, _) = Build();
        var sheets = TestFacility.SheetMailboxes.Take(2).ToList();
        var day = facility.Today.AddDays(2);
        foreach (var sheet in sheets)
        {
            gateway.Seed(sheet, new Event
            {
                Subject = "Available for Group Events", ShowAs = FreeBusyStatus.Tentative,
                Categories = [BookingCategory.GroupEvent.ToString()],
                Start = TestFacility.Dtz(day.AddHours(12)), End = TestFacility.Dtz(day.AddHours(22))
            });
        }

        var claimGroupId = Guid.NewGuid();
        var claimed = new List<SheetBooking>();
        foreach (var _ in sheets)
        {
            claimed.Add((await service.ClaimHoldAsync(day.AddHours(17), day.AddHours(19), new SheetBooking
            {
                SheetMailbox = "", Start = day.AddHours(17), End = day.AddHours(19), Category = BookingCategory.GroupEvent, State = BookingState.Confirmed
            }, claimGroupId))!);
        }

        await service.CancelGroupAsync(claimed, reopenAsGroupEventHold: true, "tester");

        var holds = (await service.GetBookingsForAllSheetsAsync(day, day.AddDays(1))).ToList();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, h => Assert.Equal((day.AddHours(12), day.AddHours(22)), (h.Start, h.End)));
        Assert.Single(holds.Select(h => h.BookingGroupId).Distinct());
    }

    [Fact]
    public async Task CancelGroupAsync_Reopen_ClearsBookedByAndExternalBookingId()
    {
        // Regression test for H1: a reopened hold must not keep the departed booking's BookedBy/
        // ExternalBookingId - Graph's PATCH only upserts extended properties it includes, so the fix
        // must explicitly send an empty value rather than omitting them.
        var (service, gateway, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(19);
        var end = start.AddHours(2);
        gateway.Seed(sheet, new Event
        {
            Subject = "Available for Group Events", ShowAs = FreeBusyStatus.Tentative,
            Categories = [BookingCategory.GroupEvent.ToString()], Start = TestFacility.Dtz(start), End = TestFacility.Dtz(end)
        });

        var claimed = await service.ClaimHoldAsync(start, end, new SheetBooking
        {
            SheetMailbox = "", Start = start, End = end, Category = BookingCategory.GroupEvent, State = BookingState.Confirmed,
            BookedBy = "Breely webhook", ExternalBookingId = "breely:123", RenterName = "Jane Curler"
        }, Guid.NewGuid());
        Assert.NotNull(claimed);

        await service.CancelGroupAsync([claimed!], reopenAsGroupEventHold: true, "tester");

        var reopened = Assert.Single(await service.GetBookingsAsync(sheet, start.AddHours(-1), end.AddHours(1)), b => b.State == BookingState.Hold);
        Assert.True(string.IsNullOrEmpty(reopened.BookedBy));
        Assert.True(string.IsNullOrEmpty(reopened.ExternalBookingId));
    }

    [Fact]
    public async Task CancelAsync_AlreadyGone_IsTreatedAsNoOpNotAnError()
    {
        var (service, gateway, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(9);
        var created = await service.CreateHoldAsync(new SheetBooking
        {
            SheetMailbox = sheet, Start = start, End = start.AddHours(1), Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");
        var eventId = created.Booking!.EventId!;

        // Simulate the booking having already been removed by something else (e.g. a concurrent
        // Breely claim) between the staff page loading and clicking Cancel.
        await gateway.DeleteEventAsync(sheet, eventId);

        await service.CancelAsync(sheet, eventId, "tester"); // must not throw
        Assert.Empty(gateway.Events(sheet));
    }

    // --- Season window: CreateAcrossSheetsAsync only - the single low-level write both the staff
    // form and PracticeIceRequestService.SubmitAsync go through. Deliberately not exercised against
    // CreateHoldAsync/CreateConfirmedAsync here: those route through a separate CreateAsync and
    // aren't called anywhere in production code (verified by grep) - only CreateAcrossSheetsAsync
    // is a real write path this check needs to cover. ---

    [Fact]
    public async Task CreateAcrossSheetsAsync_BeforeTheSeasonStarts_IsRejectedAndWritesNothing()
    {
        var (service, gateway, facility, window) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(18);
        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        var result = await service.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Too Early"
        }, "tester");

        Assert.False(result.IsSuccess);
        Assert.Empty(gateway.Events(sheet));
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_AfterTheSeasonEnds_IsRejectedAndWritesNothing()
    {
        var (service, gateway, facility, window) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(300).AddHours(18);
        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        var result = await service.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Too Late"
        }, "tester");

        Assert.False(result.IsSuccess);
        Assert.Empty(gateway.Events(sheet));
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_InsideTheSeason_Succeeds()
    {
        var (service, gateway, facility, window) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(100).AddHours(18);
        await window.SetSeasonWindowAsync(facility.Today.AddDays(30), facility.Today.AddDays(200), "tester");

        var result = await service.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "In Season"
        }, "tester");

        Assert.True(result.IsSuccess);
        Assert.Single(gateway.Events(sheet));
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_ExactlyOnTheSeasonBoundaryDates_Succeeds()
    {
        var (service, _, facility, window) = Build();
        var sheets = TestFacility.SheetMailboxes;
        var seasonStart = facility.Today.AddDays(30);
        var seasonEnd = facility.Today.AddDays(200);
        await window.SetSeasonWindowAsync(seasonStart, seasonEnd, "tester");

        var onStart = await service.CreateAcrossSheetsAsync([sheets[0]], new SheetBooking
        {
            SheetMailbox = "", Start = seasonStart.AddHours(18), End = seasonStart.AddHours(19), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Opening Day"
        }, "tester");
        var onEnd = await service.CreateAcrossSheetsAsync([sheets[1]], new SheetBooking
        {
            SheetMailbox = "", Start = seasonEnd.AddHours(18), End = seasonEnd.AddHours(19), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Closing Day"
        }, "tester");

        Assert.True(onStart.IsSuccess);
        Assert.True(onEnd.IsSuccess);
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_NoSeasonConfigured_NoRestriction()
    {
        var (service, gateway, facility, _) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(400).AddHours(18); // far future - would fail if some default season applied

        var result = await service.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Whenever"
        }, "tester");

        Assert.True(result.IsSuccess);
        Assert.Single(gateway.Events(sheet));
    }

    [Fact]
    public async Task CreateAcrossSheetsAsync_RejectedOffSeason_TheConflictMessageNamesTheSeasonDates()
    {
        var (service, _, facility, window) = Build();
        var sheet = TestFacility.SheetMailboxes[0];
        var start = facility.Today.AddDays(1).AddHours(18);
        var seasonStart = facility.Today.AddDays(30);
        var seasonEnd = facility.Today.AddDays(200);
        await window.SetSeasonWindowAsync(seasonStart, seasonEnd, "tester");

        var result = await service.CreateAcrossSheetsAsync([sheet], new SheetBooking
        {
            SheetMailbox = "", Start = start, End = start.AddHours(1), Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Too Early"
        }, "tester");

        var conflict = Assert.Single(result.Conflicts);
        Assert.Contains(seasonStart.ToString("MMM d, yyyy"), conflict.RenterName);
        Assert.Contains(seasonEnd.ToString("MMM d, yyyy"), conflict.RenterName);
    }

    // ---- Minimum group event booking interval bounds (code review C8) -------------------------------

    [Fact]
    public async Task SetMinimumGroupEventBookingIntervalAsync_NegativeValue_Throws()
    {
        var (service, _, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SetMinimumGroupEventBookingIntervalAsync(-1, "tester"));
    }

    [Fact]
    public async Task SetMinimumGroupEventBookingIntervalAsync_AbsurdlyLargeValue_Throws()
    {
        // Settings.razor's dropdown never offers more than 120 (D55) - this is the service-layer
        // backstop for any other caller, same reasoning as the pre-existing negative-value guard.
        var (service, _, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SetMinimumGroupEventBookingIntervalAsync(1441, "tester"));
    }

    [Fact]
    public async Task SetMinimumGroupEventBookingIntervalAsync_AtTheUpperBound_DoesNotThrow()
    {
        var (service, _, _, _) = Build();

        await service.SetMinimumGroupEventBookingIntervalAsync(1440, "tester");

        Assert.Equal(1440, service.MinimumGroupEventBookingIntervalMinutes);
    }
}
