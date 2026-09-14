using System.IO.Compression;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// Staff-authenticated log download - a plain Minimal API endpoint rather than a Blazor page,
/// since Blazor Server's SignalR circuit isn't a good fit for streaming a file download. Same
/// "raw HTTP semantics belong outside the Blazor component tree" reasoning as the anonymous public
/// endpoints (architecture doc D15), just with authorization required instead of anonymous - this
/// one isn't meant to be public. Zips every rotated log file (small, plain text, at this app's
/// volume) rather than only today's, so a support conversation isn't limited to "since midnight."
/// </summary>
public static class SettingsLogsEndpoint
{
    public static void MapSettingsLogsEndpoint(this WebApplication app)
    {
        app.MapGet("/settings/logs/download", async (HttpContext context, AppLogService logService, CancellationToken ct) =>
        {
            var files = logService.ListLogFiles();
            if (files.Count == 0)
            {
                return Results.NotFound("No log files exist yet.");
            }

            // Standard tier (code review S5) - a Debug-tier archive can carry booking times, sheet
            // assignments, and Breely admin URLs (architecture doc §4.9's own PII warning), and this
            // was the one staff action reachable from Settings that wrote nothing to the audit log.
            await LogDownloadAsync(context, logService, files.Count, ct);

            return Results.File(BuildZip(files), "application/zip", $"facility-scheduler-logs-{DateTime.UtcNow:yyyy-MM-dd}.zip");
        })
        .RequireAuthorization(StaffAuthorizationPolicies.StaffOnly)
        .RequireRateLimiting("staff-export"); // code review S3 - builds the entire log archive in memory
    }

    // internal, not private - InternalsVisibleTo (D60 precedent), so this is testable with a plain
    // DefaultHttpContext rather than a full ASP.NET Core test host. Same actor-resolution fallback
    // Settings.razor's own SetLevelAsync call uses.
    internal static Task LogDownloadAsync(HttpContext context, AppLogService logService, int fileCount, CancellationToken ct)
    {
        var actor = context.User.Identity?.Name ?? "Unknown";
        return logService.LogActionAsync("LogsDownloaded", actor, details: $"{fileCount} file(s).", ct: ct);
    }

    // internal, not private - InternalsVisibleTo (D60 precedent), so the file-sharing behavior below
    // is testable directly rather than only through a full ASP.NET Core test host.
    internal static byte[] BuildZip(IReadOnlyList<string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                // Not ZipArchive.CreateEntryFromFile - it opens with FileShare.Read, which denies
                // AppLogService's own concurrent writer (File.AppendAllTextAsync to today's file).
                // Depending on timing that either loses an audit line or throws an unhandled
                // IOException here (code review C10); FileShare.ReadWrite lets both proceed
                // regardless of which comes first.
                var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fileStream.CopyTo(entryStream);
            }
        }
        return stream.ToArray();
    }
}
