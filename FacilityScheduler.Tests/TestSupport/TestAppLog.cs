using FacilityScheduler.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Builds a real AppLogService writing to a throwaway temp directory - it's a small,
/// self-contained flat-file writer with no interface to mock against, and constructing the real
/// thing is cheap and exercises the same rotation/tail code paths services actually depend on
/// (e.g. LogActionAsync being awaited before a Graph call's caller returns).</summary>
public static class TestAppLog
{
    public static AppLogService Create(out string logDirectory, FacilityConfiguration? facility = null)
    {
        logDirectory = Path.Combine(Path.GetTempPath(), "FacilitySchedulerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        var options = Options.Create(new AppLogOptions { LogDirectory = logDirectory, RetentionDays = 30 });
        var env = new Mock<IHostEnvironment>().Object; // ContentRootPath never read - LogDirectory is always set above
        // AppLogService needs the facility only for its timestamps and daily rollover, which are
        // facility-local rather than UTC - any equivalently-configured instance behaves identically.
        return new AppLogService(options, env, NullLogger<AppLogService>.Instance, facility ?? TestFacility.Create());
    }

    public static AppLogService Create(FacilityConfiguration? facility = null) => Create(out _, facility);
}
