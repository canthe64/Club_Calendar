using FacilityScheduler.Domain;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// UpdateSeriesAsync - editing a recurring booking as a whole. The operator's rule is that a
/// series' *time* never moves (too likely to collide with everything else on the calendar), but
/// title, notes, category and which sheets it occupies are all fair game. Removing a sheet can
/// never conflict; adding one is checked against every occurrence, all-or-nothing.
/// </summary>
public class SeriesEditTests
{
    private static (SheetBookingService Service, FakeGraphEventGateway Gateway, FacilityConfiguration Facility) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphEventGateway(facility.ZoneInfo);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (new SheetBookingService(gateway, cache, facility, TestAppLog.Create(), new ViewCacheRegistry(cache)), gateway, facility);
    }

    /// <summary>A 4-week Tuesday-evening league on the first two sheets.</summary>
    private static async Task<List<SheetBooking>> SeedLeagueAsync(
        SheetBookingService service, FacilityConfiguration facility, DateTime firstNight, string[]? sheets = null) =>
        await service.CreateSeriesAsync(
            sheets ?? [TestFacility.SheetMailboxes[0], TestFacility.SheetMailboxes[1]],
            new SheetBooking
            {
                SheetMailbox = "",
                Start = firstNight.AddHours(19),
                End = firstNight.AddHours(21),
                Category = BookingCategory.League,
                State = BookingState.Confirmed,
                RenterName = "Tuesday Nite Leage",
                Notes = "original notes"
            },
            firstNight.AddDays(21), [], "tester");

    /// <summary>The occurrences of the seeded series as the UI would hand them back - one per sheet
    /// for a single date, each carrying its own SeriesMasterId.</summary>
    private static async Task<List<SheetBooking>> OccurrenceGroupAsync(
        SheetBookingService service, DateTime date) =>
        (await service.GetBookingsForAllSheetsAsync(date.Date, date.Date.AddDays(1)))
            .Where(b => b.SeriesMasterId is not null)
            .ToList();

    private static SheetBooking Fields(string renterName, string? notes = null,
        BookingCategory category = BookingCategory.League) =>
        new()
        {
            SheetMailbox = "",
            Start = default,
            End = default,
            Category = category,
            State = BookingState.Confirmed,
            RenterName = renterName,
            Notes = notes
        };

    [Fact]
    public async Task RenamingTheSeries_AppliesToEveryOccurrence_IncludingPastOnes()
    {
        var (service, _, facility) = Build();
        // Start in the past so the "past occurrences change too" half is genuinely exercised - the
        // operator chose whole-series-including-history over a "from here on" split.
        var firstNight = facility.Today.AddDays(-7);
        var seeded = await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight.AddDays(7));
        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Night League"),
            seeded.Select(s => s.SheetMailbox).Distinct(), "tester");

        Assert.True(result.IsSuccess);

        var past = await OccurrenceGroupAsync(service, firstNight);
        var future = await OccurrenceGroupAsync(service, firstNight.AddDays(14));
        Assert.All(past, b => Assert.Equal("Tuesday Night League", b.RenterName));
        Assert.All(future, b => Assert.Equal("Tuesday Night League", b.RenterName));
    }

    [Fact]
    public async Task EditingTheSeries_LeavesTheTimeAlone()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        var originalStart = group[0].Start;
        var originalEnd = group[0].End;

        // The caller passes default(DateTime) for Start/End - the UI has no time inputs on this
        // form at all. If those ever reached Graph the series would jump to year 1.
        await service.UpdateSeriesAsync(group, Fields("Renamed"), group.Select(g => g.SheetMailbox), "tester");

        var after = await OccurrenceGroupAsync(service, firstNight);
        Assert.All(after, b => Assert.Equal(originalStart, b.Start));
        Assert.All(after, b => Assert.Equal(originalEnd, b.End));
    }

    [Fact]
    public async Task ChangingCategoryAndNotes_AppliesAcrossTheSeries()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage", "moved to sheet 3", BookingCategory.GroupEvent),
            group.Select(g => g.SheetMailbox), "tester");

        var after = await OccurrenceGroupAsync(service, firstNight.AddDays(14));
        Assert.All(after, b => Assert.Equal(BookingCategory.GroupEvent, b.Category));
        Assert.All(after, b => Assert.Equal("moved to sheet 3", b.Notes));
    }

    [Fact]
    public async Task RemovingASheet_DropsEveryOccurrenceOnThatSheetOnly()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        var keep = TestFacility.SheetMailboxes[0];

        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"), [keep], "tester");
        Assert.True(result.IsSuccess);

        // Every date, not just the one the edit was launched from.
        foreach (var week in new[] { 0, 7, 14, 21 })
        {
            var remaining = await OccurrenceGroupAsync(service, firstNight.AddDays(week));
            Assert.Equal([keep], remaining.Select(r => r.SheetMailbox).Distinct());
        }
    }

    [Fact]
    public async Task AddingAFreeSheet_ExtendsEveryOccurrence()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"),
            TestFacility.SheetMailboxes, "tester");

        Assert.True(result.IsSuccess);

        foreach (var week in new[] { 0, 7, 14, 21 })
        {
            var occurrences = await OccurrenceGroupAsync(service, firstNight.AddDays(week));
            Assert.Equal(3, occurrences.Select(o => o.SheetMailbox).Distinct().Count());
        }
    }

    [Fact]
    public async Task AddedSheetJoinsTheSameBookingGroup_SoTheGridStillTreatsItAsOneBooking()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        var groupId = group[0].BookingGroupId;

        await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"), TestFacility.SheetMailboxes, "tester");

        var after = await OccurrenceGroupAsync(service, firstNight.AddDays(14));
        Assert.All(after, b => Assert.Equal(groupId, b.BookingGroupId));
    }

    [Fact]
    public async Task AddingASheetThatCollidesOnOneFutureDate_RefusesTheWholeEdit()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        // A one-off booking on the third sheet, colliding with only the *third* occurrence. The
        // launch date is clear, so nothing but a full-series check would catch this.
        var thirdWeek = firstNight.AddDays(14);
        await service.CreateAcrossSheetsAsync([TestFacility.SheetMailboxes[2]], new SheetBooking
        {
            SheetMailbox = "",
            Start = thirdWeek.AddHours(20),
            End = thirdWeek.AddHours(22),
            Category = BookingCategory.Bonspiel,
            State = BookingState.Confirmed,
            RenterName = "Club Bonspiel"
        }, "tester");

        var group = await OccurrenceGroupAsync(service, firstNight);
        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"),
            TestFacility.SheetMailboxes, "tester");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Conflicts, c => c.RenterName == "Club Bonspiel");
    }

    [Fact]
    public async Task ARefusedAddLeavesTheSeriesExactlyAsItWas()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var thirdWeek = firstNight.AddDays(14);
        await service.CreateAcrossSheetsAsync([TestFacility.SheetMailboxes[2]], new SheetBooking
        {
            SheetMailbox = "",
            Start = thirdWeek.AddHours(20),
            End = thirdWeek.AddHours(22),
            Category = BookingCategory.Bonspiel,
            State = BookingState.Confirmed,
            RenterName = "Club Bonspiel"
        }, "tester");

        var group = await OccurrenceGroupAsync(service, firstNight);
        await service.UpdateSeriesAsync(group, Fields("Should Not Stick", "nor these notes"),
            TestFacility.SheetMailboxes, "tester");

        // The conflict check runs before any write, so a refused edit must not have applied the
        // rename to the sheets that *were* free, nor half-added the third sheet.
        var after = await OccurrenceGroupAsync(service, firstNight);
        Assert.Equal(2, after.Select(a => a.SheetMailbox).Distinct().Count());
        Assert.All(after, b => Assert.Equal("Tuesday Nite Leage", b.RenterName));
        Assert.All(after, b => Assert.Equal("original notes", b.Notes));
    }

    [Fact]
    public async Task SwappingOneSheetForAnother_InOneEdit()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        // Drop sheet2, add sheet3. The reference master used to read the recurrence has to be one
        // that survives, or the template would be read from a series about to be deleted.
        var group = await OccurrenceGroupAsync(service, firstNight);
        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"),
            [TestFacility.SheetMailboxes[0], TestFacility.SheetMailboxes[2]], "tester");

        Assert.True(result.IsSuccess);

        var after = await OccurrenceGroupAsync(service, firstNight.AddDays(21));
        Assert.Equal(
            new[] { TestFacility.SheetMailboxes[0], TestFacility.SheetMailboxes[2] }.Order(),
            after.Select(a => a.SheetMailbox).Distinct().Order());
    }

    [Fact]
    public async Task AddingASheetMirrorsDatesTheSeriesSkipped()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        var skipped = firstNight.AddDays(7);

        // Staff excluded week 2 at creation time (the series wizard's per-date review).
        await service.CreateSeriesAsync([TestFacility.SheetMailboxes[0]], new SheetBooking
        {
            SheetMailbox = "",
            Start = firstNight.AddHours(19),
            End = firstNight.AddHours(21),
            Category = BookingCategory.League,
            State = BookingState.Confirmed,
            RenterName = "Tuesday Nite Leage"
        }, firstNight.AddDays(21), [skipped], "tester");

        var group = await OccurrenceGroupAsync(service, firstNight);
        await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"),
            [TestFacility.SheetMailboxes[0], TestFacility.SheetMailboxes[1]], "tester");

        // The new sheet must have the same gap - replaying the recurrence verbatim would have
        // given it a week the original sheet doesn't have.
        var onSkippedWeek = await OccurrenceGroupAsync(service, skipped);
        Assert.Empty(onSkippedWeek);

        var onWeekThree = await OccurrenceGroupAsync(service, firstNight.AddDays(14));
        Assert.Equal(2, onWeekThree.Select(o => o.SheetMailbox).Distinct().Count());
    }

    [Fact]
    public async Task UntickingEverySheet_IsANoOpRatherThanASilentSeriesDelete()
    {
        var (service, _, facility) = Build();
        var firstNight = facility.Today.AddDays(7);
        await SeedLeagueAsync(service, facility, firstNight);

        var group = await OccurrenceGroupAsync(service, firstNight);
        var result = await service.UpdateSeriesAsync(group, Fields("Tuesday Nite Leage"), [], "tester");

        Assert.True(result.IsSuccess);

        // Cancelling a series is CancelSeriesAsync, reached through a deliberate confirmation - not
        // something a form left with nothing ticked should be able to do by accident.
        var stillThere = await OccurrenceGroupAsync(service, firstNight);
        Assert.Equal(2, stillThere.Select(s => s.SheetMailbox).Distinct().Count());
    }

    [Fact]
    public async Task ANonRecurringBooking_IsLeftAloneEntirely()
    {
        var (service, _, facility) = Build();
        var day = facility.Today.AddDays(3);
        var created = await service.CreateAcrossSheetsAsync([TestFacility.SheetMailboxes[0]], new SheetBooking
        {
            SheetMailbox = "",
            Start = day.AddHours(10),
            End = day.AddHours(12),
            Category = BookingCategory.Bonspiel,
            State = BookingState.Confirmed,
            RenterName = "One-off"
        }, "tester");

        var result = await service.UpdateSeriesAsync(created.Bookings, Fields("Renamed"),
            TestFacility.SheetMailboxes, "tester");

        Assert.True(result.IsSuccess);
        var after = await service.GetBookingsForAllSheetsAsync(day, day.AddDays(1));
        Assert.Single(after);
        Assert.Equal("One-off", after[0].RenterName);
    }
}
