using FacilityScheduler.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Services;

/// <summary>
/// Computes the public, anonymous-safe availability view (architecture doc §5.4) - a deliberately
/// separate, hand-built minimization mapping, never a reuse of the internal booking/service-layer
/// types with anonymous access bolted on. "Available" here means an existing GroupEvent+Hold booking
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
            .Where(b => b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold)
            .Where(b => !closures.Any(closure => Overlaps(b, closure)))
            .Select(b => new PublicSheetSlot(SheetLabel(b.SheetMailbox), b.Start, b.End))
            .OrderBy(s => s.Start)
            .ToList();

        var eventLabels = clubEvents
            .Select(ce => new PublicClubEventLabel(ce.Title, ce.Category, ce.Start, ce.End, ce.IsAllDay, ce.MarksSheetsUnavailable))
            .OrderBy(e => e.Start)
            .ToList();

        return new PublicAvailabilityResponse(DateTime.UtcNow, openSlots, eventLabels);
    }

    /// <summary>
    /// The public month calendar's data - unlike GetAvailabilityAsync (GroupEvent+Hold "available for
    /// group event" slots only, a subordinate feature), this covers every category and state, reduced to
    /// just category+time+confirmed-state. The public calendar's primary purpose is letting members
    /// see what's going on club-wide while unauthenticated, not just where they can rent ice.
    /// Deliberately does NOT dedupe by BookingGroupId here - a multi-week recurring series shares one
    /// group id across every date, and deduping across the whole month range (rather than per-day,
    /// the way the internal MonthGrid does it) would collapse different dates' occurrences into one.
    /// That dedup happens per-day in the page itself, same as the internal view.
    /// </summary>
    public async Task<PublicMonthView> GetMonthViewAsync(DateTime monthAnchor, CancellationToken ct = default)
    {
        var gridStart = MonthGridStart(monthAnchor);
        var gridEnd = MonthGridEnd(monthAnchor).AddDays(1);
        var cacheKey = $"public-month:{monthAnchor:yyyyMM}";

        if (cache.TryGetValue(cacheKey, out PublicMonthView? cached) && cached is not null)
        {
            return cached;
        }

        var bookings = await bookingService.GetBookingsForAllSheetsAsync(gridStart, gridEnd, ct);
        var clubEvents = await clubEventService.GetEventsAsync(gridStart, gridEnd, ct);

        var bookingLabels = bookings
            .Select(b => new PublicMonthBooking(
                string.IsNullOrWhiteSpace(b.RenterName) ? b.Category.ToString() : b.RenterName,
                b.Category.ToString(),
                b.Start,
                b.End,
                b.State == BookingState.Confirmed))
            .ToList();

        var eventLabels = clubEvents
            .Select(ce => new PublicClubEventLabel(ce.Title, ce.Category, ce.Start, ce.End, ce.IsAllDay, ce.MarksSheetsUnavailable))
            .ToList();

        var view = new PublicMonthView(bookingLabels, eventLabels);
        cache.Set(cacheKey, view, CacheTtl);
        return view;
    }

    private static DateTime MonthGridStart(DateTime anchor)
    {
        var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
        return firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
    }

    private static DateTime MonthGridEnd(DateTime anchor)
    {
        var lastOfMonth = new DateTime(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1);
        return lastOfMonth.AddDays(6 - (int)lastOfMonth.DayOfWeek);
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
