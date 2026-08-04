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
    public static AppLogService Create(out string logDirectory)
    {
        logDirectory = Path.Combine(Path.GetTempPath(), "FacilitySchedulerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        var options = Options.Create(new AppLogOptions { LogDirectory = logDirectory, RetentionDays = 30 });
        var env = new Mock<IHostEnvironment>().Object; // ContentRootPath never read - LogDirectory is always set above
        return new AppLogService(options, env, NullLogger<AppLogService>.Instance);
    }

    public static AppLogService Create() => Create(out _);
}
