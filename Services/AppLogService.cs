using System.Globalization;
using FacilityScheduler.Domain;
using Microsoft.Extensions.Options;

namespace FacilityScheduler.Services;

/// <summary>
/// A small, self-contained audit/debug log - deliberately separate from the framework's own
/// ILogger (which by default only reaches the console/Azure Log Stream, not anything staff can see
/// without portal access, and isn't retained anywhere once the stream scrolls past it). Writes
/// plain, grep-able single-line entries to a daily-rotating text file, with files older than the
/// configured retention window auto-deleted. No companion database (architecture doc D7) - this is
/// a flat file, same spirit; just outside the deployed app folder so it survives a redeploy.
///
/// Two tiers. Standard entries - definitive actions (booking/series/club-event created, edited,
/// canceled) with the acting staff email or "Breely webhook" - are always written. Debug entries
/// (webhook payload detail, staff sign-in events) are only written while the configured level is
/// Debug, meant to be switched on temporarily via the Settings page while actively troubleshooting
/// and back off afterward, keeping routine log volume small.
/// </summary>
public class AppLogService
{
    private const string LevelFileName = "level.txt";
    private const string FilePrefix = "app-";
    private const string FileExtension = ".log";

    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private readonly ILogger<AppLogService> _fallbackLogger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile AppLogLevel _level;
    private DateOnly _currentFileDate;
    private string _currentFilePath;

    public AppLogService(IOptions<AppLogOptions> options, IHostEnvironment env, ILogger<AppLogService> fallbackLogger)
    {
        _fallbackLogger = fallbackLogger;
        var o = options.Value;
        _retentionDays = o.RetentionDays > 0 ? o.RetentionDays : 30;

        if (string.IsNullOrWhiteSpace(o.LogDirectory))
        {
            _logDirectory = Path.Combine(env.ContentRootPath, "App_Data", "logs");
            _fallbackLogger.LogWarning(
                "AppLog:LogDirectory is not configured - falling back to {Fallback}. On Azure App " +
                "Service this folder is replaced on every redeploy, losing log history; set " +
                "AppLog:LogDirectory to a persistent path (see the deployment guide) before relying " +
                "on this in production.", _logDirectory);
        }
        else
        {
            _logDirectory = o.LogDirectory;
        }

        Directory.CreateDirectory(_logDirectory);

        _level = LoadPersistedLevel();
        _currentFileDate = DateOnly.FromDateTime(DateTime.UtcNow);
        _currentFilePath = BuildFilePath(_currentFileDate);
    }

    public AppLogLevel CurrentLevel => _level;
    public string LogDirectory => _logDirectory;

    public async Task SetLevelAsync(AppLogLevel level, CancellationToken ct = default)
    {
        var previous = _level;
        _level = level;

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_logDirectory, LevelFileName), level.ToString(), ct);
        }
        catch (Exception ex)
        {
            _fallbackLogger.LogError(ex, "Failed to persist the logging level - it will revert to Standard on the next restart.");
        }
        finally
        {
            _writeLock.Release();
        }

        if (previous != level)
        {
            await LogActionAsync("LoggingLevelChanged", actor: "staff", details: $"{previous} -> {level}", ct: ct);
        }
    }

    /// <summary>Standard tier - always written. Use for definitive actions: a booking/series/club
    /// event created, edited, or canceled.</summary>
    public Task LogActionAsync(string action, string actor, string? eventId = null, string? sheet = null, string? details = null, CancellationToken ct = default) =>
        WriteAsync("INFO", action, actor, eventId, sheet, details, ct);

    /// <summary>Standard tier - always written. Use for security-relevant events worth keeping
    /// regardless of level, e.g. a failed webhook secret check.</summary>
    public Task LogSecurityAsync(string action, string actor, string? details = null, CancellationToken ct = default) =>
        WriteAsync("WARN", action, actor, null, null, details, ct);

    /// <summary>Debug tier - a no-op unless the configured level is currently Debug.</summary>
    public Task LogDebugAsync(string action, string actor, string? eventId = null, string? sheet = null, string? details = null, CancellationToken ct = default)
    {
        if (_level != AppLogLevel.Debug)
        {
            return Task.CompletedTask;
        }
        return WriteAsync("DEBUG", action, actor, eventId, sheet, details, ct);
    }

    private async Task WriteAsync(string tier, string action, string actor, string? eventId, string? sheet, string? details, CancellationToken ct)
    {
        var line = FormatLine(tier, action, actor, eventId, sheet, details);

        await _writeLock.WaitAsync(ct);
        try
        {
            RotateIfNeeded();
            await File.AppendAllTextAsync(_currentFilePath, line + Environment.NewLine, ct);
        }
        catch (Exception ex)
        {
            // A logging failure must never take down the caller's own action - the booking/webhook
            // processing already happened (or is about to); losing one audit line is far better than
            // faulting the request over it. Surface it via the framework logger instead.
            _fallbackLogger.LogError(ex, "AppLogService failed to write a log entry: {Action}", action);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Caller must hold _writeLock.
    private void RotateIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _currentFileDate)
        {
            return;
        }

        _currentFileDate = today;
        _currentFilePath = BuildFilePath(today);
        CleanUpOldFiles();
    }

    private void CleanUpOldFiles()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_logDirectory, $"{FilePrefix}*{FileExtension}"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var datePart = name.Length > FilePrefix.Length ? name[FilePrefix.Length..] : null;
            if (datePart is not null && DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var fileDate) && fileDate < cutoff)
            {
                try { File.Delete(file); }
                catch (Exception ex) { _fallbackLogger.LogWarning(ex, "Failed to delete expired log file {File}", file); }
            }
        }
    }

    private string BuildFilePath(DateOnly date) => Path.Combine(_logDirectory, $"{FilePrefix}{date:yyyy-MM-dd}{FileExtension}");

    private AppLogLevel LoadPersistedLevel()
    {
        try
        {
            var path = Path.Combine(_logDirectory, LevelFileName);
            if (File.Exists(path) && Enum.TryParse<AppLogLevel>(File.ReadAllText(path).Trim(), out var level))
            {
                return level;
            }
        }
        catch (Exception ex)
        {
            _fallbackLogger.LogWarning(ex, "Failed to read the persisted logging level - defaulting to Standard.");
        }
        return AppLogLevel.Standard;
    }

    private static string FormatLine(string tier, string action, string actor, string? eventId, string? sheet, string? details)
    {
        var parts = new List<string>
        {
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            $"[{tier}]",
            $"action={action}",
            $"actor={Escape(actor)}"
        };

        if (!string.IsNullOrEmpty(eventId)) parts.Add($"eventId={Escape(eventId)}");
        if (!string.IsNullOrEmpty(sheet)) parts.Add($"sheet={Escape(sheet)}");
        if (!string.IsNullOrEmpty(details)) parts.Add($"details=\"{Escape(details)}\"");

        return string.Join(' ', parts);
    }

    // One entry per line, by convention - strip characters that would let a value (a staff-entered
    // note, a Breely field) inject a fake extra line or unbalanced quote into the file.
    private static string Escape(string value) => value.Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');

    /// <summary>Returns up to <paramref name="count"/> of the most recent lines, oldest-to-newest
    /// (reads top-to-bottom like a normal log tail), walking backward from today's file into older
    /// ones if the current file doesn't have enough on its own (e.g. right after midnight rotation).</summary>
    public async Task<List<string>> TailAsync(int count, CancellationToken ct = default)
    {
        var collected = new List<string>();
        foreach (var file in ListLogFiles())
        {
            if (collected.Count >= count)
            {
                break;
            }

            List<string> lines;
            try
            {
                lines = [.. await File.ReadAllLinesAsync(file, ct)];
            }
            catch (IOException)
            {
                continue; // being written to concurrently or otherwise unreadable right now - skip, not fatal
            }

            var needed = count - collected.Count;
            var take = lines.Count > needed ? lines.GetRange(lines.Count - needed, needed) : lines;
            collected.InsertRange(0, take);
        }

        return collected;
    }

    /// <summary>Every rotated log file, newest first.</summary>
    public List<string> ListLogFiles() =>
        Directory.Exists(_logDirectory)
            ? Directory.GetFiles(_logDirectory, $"{FilePrefix}*{FileExtension}").OrderByDescending(f => f).ToList()
            : [];
}
