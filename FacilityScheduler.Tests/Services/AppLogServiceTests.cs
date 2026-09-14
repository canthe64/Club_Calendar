using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FacilityScheduler.Tests.Services;

public class AppLogServiceTests
{
    [Fact]
    public void Constructor_DeletesExpiredLogFilesImmediately_WithoutWaitingForARollover()
    {
        // Code review C9: CleanUpOldFiles was previously only ever called from RotateIfNeeded, which
        // only fires once this same process observes the facility-local date changing - an App Service
        // instance that recycles at least once a day (routine, not exceptional) would otherwise never
        // see a rollover and AppLog:RetentionDays would never actually apply. Must happen at startup.
        var facility = TestFacility.Create();
        var logDirectory = Path.Combine(Path.GetTempPath(), "FacilitySchedulerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        const int retentionDays = 5;
        var expiredFile = Path.Combine(logDirectory, $"app-{facility.Today.AddDays(-(retentionDays + 10)):yyyy-MM-dd}.log");
        var freshFile = Path.Combine(logDirectory, $"app-{facility.Today.AddDays(-1):yyyy-MM-dd}.log");
        File.WriteAllText(expiredFile, "old\n");
        File.WriteAllText(freshFile, "recent\n");

        var options = Options.Create(new AppLogOptions { LogDirectory = logDirectory, RetentionDays = retentionDays });
        var env = new Mock<IHostEnvironment>().Object; // ContentRootPath never read - LogDirectory is always set above

        _ = new AppLogService(options, env, NullLogger<AppLogService>.Instance, facility);

        Assert.False(File.Exists(expiredFile));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task LogActionAsync_DetailsContainingDoubleQuotes_SurvivesVerbatim()
    {
        // Code review O7: details used to have every " collapsed to ' (and be wrapped in its own
        // quotes), which mangled the one thing WebhookRawPayloadReceived exists to preserve - raw
        // Breely JSON - into something no JSON tool could parse back out. details is always the
        // line's last field, so it never needed the quote-collapsing that protects actor/eventId/sheet.
        var appLog = TestAppLog.Create(out _);
        const string rawJson = "{\"event_type\":\"25-32 Participants\",\"admin_url\":\"https://example.com\"}";

        await appLog.LogActionAsync("WebhookRawPayloadReceived", "Breely webhook", details: rawJson);

        var lines = await appLog.TailAsync(10);
        Assert.Contains(lines, l => l.EndsWith($"details={rawJson}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogActionAsync_ActorContainingDoubleQuotes_StillHasThemCollapsed()
    {
        // actor isn't the line's last field (details, when present, always comes after it) - it
        // still needs its quotes collapsed so an embedded " can't unbalance the line's own
        // details="..." delimiter for a caller that supplies both. Regression guard for O7's fix
        // staying scoped to details only.
        var appLog = TestAppLog.Create(out _);

        await appLog.LogActionAsync("Test", "actor with \"quotes\"", details: "some details");

        var lines = await appLog.TailAsync(10);
        Assert.Contains(lines, l => l.Contains("actor=actor with 'quotes'"));
    }
}
