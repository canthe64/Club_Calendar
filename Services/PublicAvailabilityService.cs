using FacilityScheduler.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Services;

/// <summary>
/// Computes the public, anonymous-safe availability view (architecture doc §5.4) - a deliberately
/// separate, hand-built minimization mapping, never a reuse of the internal booking/service-layer
/// types with anonymous access bolted on. "Available" here means an existing Rental+Hold booking
/// (the same "AVAILABLE FOR RENTAL" slots staff already create today), not raw free/busy - simpler
/// than computing complementary free time, and more correct: unbooked League/Bonspiel/practice time
/// isn't necessarily something staff want the public renting.
/// </summary>
public class PublicAvailabilityService(SheetBookingService bookingService, ClubEventService clubEventService, IMemoryCache cache)
{
    private const int DefaultDays = 30;
    private const int MaxDays = 60;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<PublicAvailabilityResponse> GetAvailabilityAsync(int? requestedDays, CancellationToken ct = default)
    {
        var days = Math.Clamp(requestedDays ?? DefaultDays, 1, MaxDays);
        var start = DateTime.UtcNow.Date;
        var cacheKey = $"public-availability:{start:yyyyMMdd}:{days}";

        if (cache.TryGetValue(cacheKey, out PublicAvailabilityResponse? cached) && cached is not null)
        {
            return cached;
        }

        var response = await ComputeAvailabilityAsync(start, start.AddDays(days), ct);
        cache.Set(cacheKey, response, CacheTtl);
        return response;
    }

    private async Task<PublicAvailabilityResponse> ComputeAvailabilityAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(start, end, ct);
        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);

        // Never publicly promise a sheet that's actually closed for a club-wide event - this is a
        // public-view-only correctness check, distinct from D13's staff-side "no cross-check"
        // decision (which was specifically about the internal write path).
        var closures = clubEvents.Where(ce => ce.MarksSheetsUnavailable).ToList();

        var openSlots = bookings
            .Where(b => b.Category == BookingCategory.Rental && b.State == BookingState.Hold)
            .Where(b => !closures.Any(closure => Overlaps(b, closure)))
            .Select(b => new PublicSheetSlot(SheetLabel(b.SheetMailbox), b.Start, b.End))
            .OrderBy(s => s.Start)
            .ToList();

        var eventLabels = clubEvents
            .Select(ce => new PublicClubEventLabel(ce.Title, ce.Start, ce.End, ce.IsAllDay, ce.MarksSheetsUnavailable))
            .OrderBy(e => e.Start)
            .ToList();

        return new PublicAvailabilityResponse(DateTime.UtcNow, openSlots, eventLabels);
    }

    private static bool Overlaps(SheetBooking booking, ClubEvent closure)
    {
        // ClubEvent.End is already the inclusive last day for all-day events (converted back from
        // Graph's exclusive end-date convention in ClubEventService) - add a day to get the
        // exclusive boundary needed for this comparison. Timed closures already have an exact End.
        var closureEnd = closure.IsAllDay ? closure.End.Date.AddDays(1) : closure.End;
        return booking.Start < closureEnd && booking.End > closure.Start;
    }

    private static string SheetLabel(string sheetMailbox)
    {
        var localPart = sheetMailbox.Split('@')[0];
        var digits = new string(localPart.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? $"Sheet {digits}" : localPart;
    }
}
