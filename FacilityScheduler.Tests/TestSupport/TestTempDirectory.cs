namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Every temp directory the suite creates lives under one per-run root. Tests used to create
/// a fresh %TEMP%\FacilitySchedulerTests\{guid} each and never remove it, which left tens of thousands
/// of directories behind. The test host is torn down rather than exiting normally, so cleanup can't
/// rely on a process-exit hook: instead each run deletes the earlier runs' roots when it starts. A
/// whole suite takes seconds, so a root older than <see cref="StaleAfter"/> belongs to a finished run.</summary>
public static class TestTempDirectory
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "FacilitySchedulerTests");
    private static readonly string Root = Path.Combine(Parent, $"run-{Guid.NewGuid():N}");

    static TestTempDirectory()
    {
        SweepFinishedRuns();
        Directory.CreateDirectory(Root);
    }

    /// <summary>A new, empty directory under this run's root.</summary>
    public static string Create()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SweepFinishedRuns()
    {
        if (!Directory.Exists(Parent))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var run in Directory.EnumerateDirectories(Parent, "run-*"))
        {
            if (Directory.GetCreationTimeUtc(run) < cutoff)
            {
                try
                {
                    Directory.Delete(run, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
