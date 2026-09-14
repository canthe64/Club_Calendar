using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Tests.TestSupport;
using Microsoft.AspNetCore.Http;

namespace FacilityScheduler.Tests.Endpoints;

public class SettingsLogsEndpointTests
{
    private static string MakeTempFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FacilitySchedulerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "app-2026-09-14.log");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void BuildZip_RoundTripsFileNameAndContent()
    {
        var file = MakeTempFile("2026-09-14T10:00:00.000-07:00 [INFO] action=Test actor=test\n");

        var zipBytes = SettingsLogsEndpoint.BuildZip([file]);

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(Path.GetFileName(file), entry.Name);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("2026-09-14T10:00:00.000-07:00 [INFO] action=Test actor=test\n", reader.ReadToEnd());
    }

    [Fact]
    public void BuildZip_FileConcurrentlyOpenTheSameWayAppLogServiceOpensIt_DoesNotThrow()
    {
        // Code review C10: ZipArchive.CreateEntryFromFile opens with FileShare.Read, which denies a
        // concurrent writer - and AppLogService.WriteAsync (via File.AppendAllTextAsync's underlying
        // StreamWriter) opens each log line with exactly FileAccess.Write, FileShare.Read, the default
        // for that API. Holding that same kind of handle open here reproduces the sharing conflict a
        // real concurrent append would hit; BuildZip's FileShare.ReadWrite read must tolerate it.
        var file = MakeTempFile("existing content\n");
        using var writerHandle = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);

        var zipBytes = SettingsLogsEndpoint.BuildZip([file]);

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(Path.GetFileName(file), entry.Name);
    }

    [Fact]
    public async Task LogDownloadAsync_WritesAStandardTierEntry_NamingTheSignedInStaffMemberAndFileCount()
    {
        // Code review S5: downloading the log archive - which can carry booking times, sheet
        // assignments, and Breely admin URLs even with customer PII redacted (§4.9) - previously wrote
        // nothing to the audit log at all.
        var appLog = TestAppLog.Create(out _);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "jane@test.onmicrosoft.com")], "TestAuth"))
        };

        await SettingsLogsEndpoint.LogDownloadAsync(context, appLog, fileCount: 3, CancellationToken.None);

        var lines = await appLog.TailAsync(10);
        Assert.Contains(lines, l => l.Contains("[INFO]") && l.Contains("LogsDownloaded") && l.Contains("jane@test.onmicrosoft.com") && l.Contains("3 file(s)"));
    }

    [Fact]
    public async Task LogDownloadAsync_UnauthenticatedContext_FallsBackToUnknown()
    {
        var appLog = TestAppLog.Create(out _);
        var context = new DefaultHttpContext(); // no User set - Identity.Name is null

        await SettingsLogsEndpoint.LogDownloadAsync(context, appLog, fileCount: 1, CancellationToken.None);

        var lines = await appLog.TailAsync(10);
        Assert.Contains(lines, l => l.Contains("actor=Unknown"));
    }

    [Fact]
    public void BuildZip_MultipleFiles_AllIncluded()
    {
        var file1 = MakeTempFile("first\n");
        var file2 = Path.Combine(Path.GetDirectoryName(file1)!, "app-2026-09-13.log");
        File.WriteAllText(file2, "second\n");

        var zipBytes = SettingsLogsEndpoint.BuildZip([file1, file2]);

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        Assert.Equal(2, zip.Entries.Count);
        Assert.Contains(zip.Entries, e => e.Name == Path.GetFileName(file1));
        Assert.Contains(zip.Entries, e => e.Name == Path.GetFileName(file2));
    }
}
