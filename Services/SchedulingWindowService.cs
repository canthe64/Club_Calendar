using System.Globalization;
using System.Text.Json;

namespace FacilityScheduler.Services;

/// <summary>
/// Two independent staff-configurable date settings (Settings page), both nullable - unset means
/// no restriction, matching this app's "not a requirement" convention:
///
///  - <see cref="PublicCutoffDate"/> - hides anything starting after it from the public calendar
///    and the JSON/embed widget only. Lets staff build out a season on the staff calendar before
///    it's visible to members, without a per-booking draft flag.
///  - <see cref="SeasonStartDate"/>/<see cref="SeasonEndDate"/> - rejects new sheet bookings
///    outside the window (staff form, series, practice ice). Club Events are exempt by design.
///
/// Persisted as one JSON file rather than the one-file-per-value convention SheetBookingService's
/// booking-policy.txt and AppLogService's level.txt each use for their own single setting: this
/// service owns three related values, and SetSeasonWindowAsync sets two of them together - splitting
/// those into separate files would mean "together" is true in the API but not in storage (a write
/// failure between two file writes could leave one bound updated and the other stale).
/// </summary>
public class SchedulingWindowService(AppLogService log, ViewCacheRegistry viewCache)
{
    private const string FileName = "scheduling-window.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(log.LogDirectory, FileName);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile Snapshot _current = Load(Path.Combine(log.LogDirectory, FileName));

    public DateTime? PublicCutoffDate => _current.PublicCutoffDate;
    public DateTime? SeasonStartDate => _current.SeasonStartDate;
    public DateTime? SeasonEndDate => _current.SeasonEndDate;

    /// <summary>True once a date falls after the configured public cutoff. Null-safe - always
    /// false when no cutoff is set. A date landing exactly ON the cutoff is not past it (the
    /// operator's own straddling-event rule: visible if it starts on or before the cutoff).</summary>
    public bool IsPastPublicCutoff(DateTime date) => PublicCutoffDate is { } cutoff && date.Date > cutoff.Date;

    /// <summary>True once a date falls outside the configured booking season, on either bound.
    /// Null-safe per bound - configuring only one end still restricts that side alone.</summary>
    public bool IsOutsideSeason(DateTime date) =>
        (SeasonStartDate is { } start && date.Date < start.Date) ||
        (SeasonEndDate is { } end && date.Date > end.Date);

    public async Task SetPublicCutoffAsync(DateTime? date, string actor, CancellationToken ct = default)
    {
        var previous = _current;
        await PersistAsync(previous with { PublicCutoffDate = date?.Date }, ct);

        if (previous.PublicCutoffDate != _current.PublicCutoffDate)
        {
            await log.LogActionAsync("PublicCutoffChanged", actor,
                details: $"{Describe(previous.PublicCutoffDate)} -> {Describe(_current.PublicCutoffDate)}", ct: ct);
        }
    }

    public async Task SetSeasonWindowAsync(DateTime? start, DateTime? end, string actor, CancellationToken ct = default)
    {
        var previous = _current;
        await PersistAsync(previous with { SeasonStartDate = start?.Date, SeasonEndDate = end?.Date }, ct);

        if (previous.SeasonStartDate != _current.SeasonStartDate || previous.SeasonEndDate != _current.SeasonEndDate)
        {
            await log.LogActionAsync("SeasonWindowChanged", actor,
                details: $"{Describe(previous.SeasonStartDate)}-{Describe(previous.SeasonEndDate)} -> {Describe(_current.SeasonStartDate)}-{Describe(_current.SeasonEndDate)}", ct: ct);
        }
    }

    private async Task PersistAsync(Snapshot next, CancellationToken ct)
    {
        // Takes effect immediately regardless of persistence outcome, same as every other setting
        // in this app - the in-memory value is the live truth, a failed write just risks it not
        // surviving a restart.
        _current = next;

        // Unlike a booking write, this doesn't sit behind SheetBookingService's own invalidation -
        // nothing else calls InvalidateAll() when only a Settings value changed, so without this a
        // just-set cutoff/season could still read stale for up to the public cache's 60s TTL.
        viewCache.InvalidateAll();

        var acquired = false;
        try
        {
            await _writeLock.WaitAsync(ct);
            acquired = true;
            var dto = new FileDto(FormatDate(next.PublicCutoffDate), FormatDate(next.SeasonStartDate), FormatDate(next.SeasonEndDate));
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(dto, JsonOptions), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence, same tolerance as booking-policy.txt/level.txt.
        }
        finally
        {
            if (acquired)
            {
                _writeLock.Release();
            }
        }
    }

    private static Snapshot Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path));
                if (dto is not null)
                {
                    return new Snapshot(ParseDate(dto.PublicCutoffDate), ParseDate(dto.SeasonStartDate), ParseDate(dto.SeasonEndDate));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to "nothing configured" - not worth failing startup over, same
            // tolerance as booking-policy.txt/level.txt.
        }
        return new Snapshot(null, null, null);
    }

    private static string? FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string Describe(DateTime? date) => date is { } d ? d.ToString("yyyy-MM-dd") : "(none)";

    private sealed record Snapshot(DateTime? PublicCutoffDate, DateTime? SeasonStartDate, DateTime? SeasonEndDate);

    private sealed record FileDto(string? PublicCutoffDate, string? SeasonStartDate, string? SeasonEndDate);
}
