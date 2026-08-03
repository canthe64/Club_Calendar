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
        app.MapGet("/settings/logs/download", (AppLogService logService) =>
        {
            var files = logService.ListLogFiles();
            if (files.Count == 0)
            {
                return Results.NotFound("No log files exist yet.");
            }

            byte[] zipBytes;
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in files)
                    {
                        zip.CreateEntryFromFile(file, Path.GetFileName(file));
                    }
                }
                zipBytes = stream.ToArray();
            }

            return Results.File(zipBytes, "application/zip", $"facility-scheduler-logs-{DateTime.UtcNow:yyyy-MM-dd}.zip");
        })
        .RequireAuthorization();
    }
}
