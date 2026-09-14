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
}
