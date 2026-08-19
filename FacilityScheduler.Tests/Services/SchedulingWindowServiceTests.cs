using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// SchedulingWindowService - the publish cutoff and booking season settings. Persistence follows
/// the same file-in-AppLog:LogDirectory pattern as SheetBookingService's booking-policy.txt and
/// AppLogService's level.txt, but as one JSON file rather than three, since SetSeasonWindowAsync
/// sets two of the three values together.
/// </summary>
public class SchedulingWindowServiceTests
{
    private static (SchedulingWindowService Window, AppLogService AppLog, ViewCacheRegistry ViewCache, IMemoryCache Cache) Build()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var viewCache = new ViewCacheRegistry(cache);
        var appLog = TestAppLog.Create();
        return (new SchedulingWindowService(appLog, viewCache), appLog, viewCache, cache);
    }

    [Fact]
    public void FreshInstance_NothingConfigured_AllThreeValuesAreNull()
    {
        var (window, _, _, _) = Build();

        Assert.Null(window.PublicCutoffDate);
        Assert.Null(window.SeasonStartDate);
        Assert.Null(window.SeasonEndDate);
    }

    [Fact]
    public async Task SetPublicCutoffAsync_PersistsAcrossANewInstanceReadingTheSameDirectory()
    {
        var (window, appLog, viewCache, _) = Build();
        var date = new DateTime(2026, 8, 30);
        await window.SetPublicCutoffAsync(date, "tester");

        // A second instance, same AppLogService (same LogDirectory on disk) - proves the value was
        // actually written to the file, not just held in the first instance's memory.
        var reloaded = new SchedulingWindowService(appLog, viewCache);

        Assert.Equal(date, reloaded.PublicCutoffDate);
    }

    [Fact]
    public async Task SetSeasonWindowAsync_PersistsAlongsideAnAlreadySetCutoff_NeitherOverwritesTheOther()
    {
        // All three values share one file - proves a season-window write doesn't clobber a
        // previously-set cutoff (or vice versa), which per-value files couldn't get wrong but a
        // naive "always write the whole snapshot from a stale reference" bug could.
        var (window, appLog, viewCache, _) = Build();
        await window.SetPublicCutoffAsync(new DateTime(2026, 8, 30), "tester");

        await window.SetSeasonWindowAsync(new DateTime(2026, 10, 15), new DateTime(2027, 3, 15), "tester");

        var reloaded = new SchedulingWindowService(appLog, viewCache);
        Assert.Equal(new DateTime(2026, 8, 30), reloaded.PublicCutoffDate);
        Assert.Equal(new DateTime(2026, 10, 15), reloaded.SeasonStartDate);
        Assert.Equal(new DateTime(2027, 3, 15), reloaded.SeasonEndDate);
    }

    [Fact]
    public async Task SetSeasonWindowAsync_SetsBothValuesTogether()
    {
        var (window, _, _, _) = Build();
        var start = new DateTime(2026, 10, 15);
        var end = new DateTime(2027, 3, 15);

        await window.SetSeasonWindowAsync(start, end, "tester");

        Assert.Equal(start, window.SeasonStartDate);
        Assert.Equal(end, window.SeasonEndDate);
    }

    [Fact]
    public async Task SetSeasonWindowAsync_NullBoth_ClearsTheRestriction()
    {
        var (window, _, _, _) = Build();
        await window.SetSeasonWindowAsync(new DateTime(2026, 10, 15), new DateTime(2027, 3, 15), "tester");

        await window.SetSeasonWindowAsync(null, null, "tester");

        Assert.Null(window.SeasonStartDate);
        Assert.Null(window.SeasonEndDate);
    }

    [Fact]
    public async Task SetPublicCutoffAsync_Null_ClearsIt()
    {
        var (window, _, _, _) = Build();
        await window.SetPublicCutoffAsync(new DateTime(2026, 8, 30), "tester");

        await window.SetPublicCutoffAsync(null, "tester");

        Assert.Null(window.PublicCutoffDate);
    }

    [Fact]
    public async Task ChangingTheCutoff_InvalidatesThePublicViewCache()
    {
        // Unlike a booking write, nothing else invalidates when only a Settings value changes -
        // without this, a just-set cutoff would still read stale for up to the public cache's TTL.
        var (window, _, viewCache, cache) = Build();
        viewCache.Track("some-public-cache-key");
        cache.Set("some-public-cache-key", "stale-value");

        await window.SetPublicCutoffAsync(new DateTime(2026, 8, 30), "tester");

        Assert.False(cache.TryGetValue("some-public-cache-key", out _));
    }

    [Theory]
    [InlineData(-1, false)] // day before the cutoff
    [InlineData(0, false)]  // on the cutoff itself - still visible (straddling-event rule)
    [InlineData(1, true)]   // day after
    public async Task IsPastPublicCutoff_BoundaryIsInclusiveOfTheCutoffDateItself(int offsetDays, bool expectedPast)
    {
        var (window, _, _, _) = Build();
        var cutoff = new DateTime(2026, 8, 30);
        await window.SetPublicCutoffAsync(cutoff, "tester");

        Assert.Equal(expectedPast, window.IsPastPublicCutoff(cutoff.AddDays(offsetDays)));
    }

    [Fact]
    public void IsPastPublicCutoff_NothingConfigured_AlwaysFalse()
    {
        var (window, _, _, _) = Build();

        Assert.False(window.IsPastPublicCutoff(new DateTime(2099, 1, 1)));
    }

    [Theory]
    [InlineData(-1, true)]  // before season start
    [InlineData(0, false)]  // exactly on season start
    [InlineData(100, false)] // comfortably inside
    public async Task IsOutsideSeason_StartBoundary(int offsetDaysFromStart, bool expectedOutside)
    {
        var (window, _, _, _) = Build();
        var start = new DateTime(2026, 10, 15);
        var end = new DateTime(2027, 3, 15);
        await window.SetSeasonWindowAsync(start, end, "tester");

        Assert.Equal(expectedOutside, window.IsOutsideSeason(start.AddDays(offsetDaysFromStart)));
    }

    [Theory]
    [InlineData(0, false)]  // exactly on season end - still inside
    [InlineData(1, true)]   // day after
    public async Task IsOutsideSeason_EndBoundary(int offsetDaysFromEnd, bool expectedOutside)
    {
        var (window, _, _, _) = Build();
        var start = new DateTime(2026, 10, 15);
        var end = new DateTime(2027, 3, 15);
        await window.SetSeasonWindowAsync(start, end, "tester");

        Assert.Equal(expectedOutside, window.IsOutsideSeason(end.AddDays(offsetDaysFromEnd)));
    }

    [Fact]
    public void IsOutsideSeason_NothingConfigured_AlwaysFalse()
    {
        var (window, _, _, _) = Build();

        Assert.False(window.IsOutsideSeason(new DateTime(2020, 1, 1)));
        Assert.False(window.IsOutsideSeason(new DateTime(2099, 1, 1)));
    }

    [Fact]
    public async Task IsOutsideSeason_OnlyStartConfigured_RestrictsThatSideAlone()
    {
        var (window, _, _, _) = Build();
        await window.SetSeasonWindowAsync(new DateTime(2026, 10, 15), null, "tester");

        Assert.True(window.IsOutsideSeason(new DateTime(2026, 10, 1)));
        Assert.False(window.IsOutsideSeason(new DateTime(2099, 1, 1))); // no end bound, far future still fine
    }

    [Fact]
    public async Task IsOutsideSeason_OnlyEndConfigured_RestrictsThatSideAlone()
    {
        var (window, _, _, _) = Build();
        await window.SetSeasonWindowAsync(null, new DateTime(2027, 3, 15), "tester");

        Assert.True(window.IsOutsideSeason(new DateTime(2027, 4, 1)));
        Assert.False(window.IsOutsideSeason(new DateTime(2000, 1, 1))); // no start bound, far past still fine
    }
}
