using FacilityScheduler.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Services;

/// <summary>
/// Computes the public, anonymous-safe availability view (architecture doc §5.4) - a deliberately
/// separate, hand-built minimization mapping, never a reuse of the internal booking/service-layer
/// types with anonymous access bolted on. "Available" here means an existing GroupEvent+Hold booking
/// (the same open group-event slots staff already create today), not raw free/busy - simpler
/// than computing complementary free time, and more correct: unbooked League/Bonspiel/practice time
/// isn't necessarily something staff want the public renting.
/// </summary>
public class PublicAvailabilityService(SheetBookingService bookingService, ClubEventService clubEventService, IMemoryCache cache, FacilityConfiguration facility)
{
    private const int DefaultDays = 30;
    private const int MaxDays = 60;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<PublicAvailabilityResponse> GetAvailabilityAsync(int? requestedDays, CancellationToken ct = default)
    {
        var days = Math.Clamp(requestedDays ?? DefaultDays, 1, MaxDays);
        // Facility-local "today", not DateTime.UtcNow.Date - live-found 2026-08-04: from ~5pm PDT
        // onward this window was starting "tomorrow," silently dropping the rest of tonight's open
        // ice from the public feed during the facility's own busiest hours (architecture doc §8).
        var start = facility.Today;
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
        var openSlots = await GetOpenSlotsAsync(start, end, ct);

        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);
        var eventLabels = clubEvents
            .Select(ce => new PublicClubEventLabel(ce.Title, ce.Category, ce.Start, ce.End, ce.IsAllDay, ce.MarksSheetsUnavailable))
            .OrderBy(e => e.Start)
            .ToList();

        return new PublicAvailabilityResponse(DateTime.UtcNow, openSlots, eventLabels);
    }

    /// <summary>Every genuinely open (GroupEvent+Hold) slot in a window, across every sheet - the
    /// same minimization/closure-exclusion rule GetAvailabilityAsync already applies, factored out
    /// so GetConcurrentAvailabilityAsync can reuse it for an arbitrary staff-facing search range
    /// instead of only "the next N days from today".</summary>
    private async Task<List<PublicSheetSlot>> GetOpenSlotsAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(start, end, ct);
        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);

        // Never publicly promise a sheet that's actually closed for a club-wide event - this is a
        // public-view-only correctness check, distinct from D13's staff-side "no cross-check"
        // decision (which was specifically about the internal write path).
        var closures = clubEvents.Where(ce => ce.MarksSheetsUnavailable).ToList();

        var holds = bookings.Where(b => b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold).ToList();
        var result = new List<PublicSheetSlot>();

        foreach (var hold in holds)
        {
            // A hold's own advertised Start/End can't be trusted blindly as open - the app's own
            // write-path conflict check should prevent another booking from ever overlapping a hold
            // on the same sheet, but that invariant doesn't protect data written outside the app
            // (direct Graph/Outlook writes, migrations, seeded test data) - live-found 2026-07-28 via
            // a hold that fully covered a separately-booked, confirmed League game on the same sheet.
            // The public feed must never promise ice that's actually occupied regardless of how the
            // conflicting data got there, so every OTHER booking on the same sheet is subtracted out
            // of the hold's window before it's reported as available.
            var blockers = bookings
                .Where(b => b.SheetMailbox == hold.SheetMailbox && b.EventId != hold.EventId)
                .Where(b => b.Start < hold.End && b.End > hold.Start)
                .Select(b => (b.Start, b.End))
                .ToList();

            var openSegments = blockers.Count == 0
                ? [(hold.Start, hold.End)]
                : CalendarStyles.SubtractIntervals(hold.Start, hold.End, blockers);

            foreach (var (segStart, segEnd) in openSegments)
            {
                if (segEnd <= segStart)
                {
                    continue;
                }

                if (closures.Any(closure => OverlapsRange(segStart, segEnd, closure)))
                {
                    continue;
                }

                result.Add(new PublicSheetSlot(SheetLabel(hold.SheetMailbox), segStart, segEnd));
            }
        }

        return result.OrderBy(s => s.Start).ToList();
    }

    /// <summary>
    /// Finds date/time windows where at least <paramref name="minSheets"/> distinct sheets have an
    /// open (GroupEvent+Hold) slot simultaneously - the R6 "≥N sheets available" view the
    /// architecture doc originally marked backlogged, delivered as a dedicated search page
    /// (/public/search) rather than a permanent calendar overlay, per the raised feedback.
    /// Cached by (start, end, minSheets) - same 60s TTL and the same clamped-window rationale as
    /// every other public read, since this is anonymous-reachable and its own quota-exhaustion vector.
    /// </summary>
    public async Task<List<PublicAvailabilityWindow>> GetConcurrentAvailabilityAsync(DateTime start, DateTime end, int minSheets, CancellationToken ct = default)
    {
        var cacheKey = $"public-search:{start:yyyyMMdd}:{end:yyyyMMdd}:{minSheets}";
        if (cache.TryGetValue(cacheKey, out List<PublicAvailabilityWindow>? cached) && cached is not null)
        {
            return cached;
        }

        var slots = await GetOpenSlotsAsync(start, end, ct);
        var windows = FindConcurrentAvailability(slots, minSheets);
        cache.Set(cacheKey, windows, CacheTtl);
        return windows;
    }

    // Classic two-pass interval algorithm: first merge each sheet's own slots into that sheet's
    // maximal contiguous available blocks (so a sheet held 6-8pm then 8-10pm reads as one 6-10pm
    // block, not two adjacent ones), then sweep across every sheet's merged blocks and, for each
    // micro-interval between consecutive boundary points, count how many distinct sheets' blocks
    // fully cover it. Because each sheet contributes at most one block at any given instant (its own
    // blocks were already merged), that count is exactly the number of distinct sheets simultaneously
    // available - consecutive micro-intervals meeting the threshold are then merged into the reported
    // windows.
    private static List<PublicAvailabilityWindow> FindConcurrentAvailability(List<PublicSheetSlot> slots, int minSheets)
    {
        var perSheetBlocks = slots
            .GroupBy(s => s.SheetLabel)
            .SelectMany(g => MergeIntervals(g.Select(s => (s.Start, s.End)).OrderBy(i => i.Start).ToList()))
            .ToList();

        if (perSheetBlocks.Count == 0)
        {
            return [];
        }

        var boundaries = perSheetBlocks
            .SelectMany(b => new[] { b.Start, b.End })
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var windows = new List<PublicAvailabilityWindow>();
        DateTime? windowStart = null;

        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var t1 = boundaries[i];
            var t2 = boundaries[i + 1];
            var concurrentSheets = perSheetBlocks.Count(b => b.Start <= t1 && b.End >= t2);

            if (concurrentSheets >= minSheets)
            {
                windowStart ??= t1;
            }
            else if (windowStart.HasValue)
            {
                windows.Add(new PublicAvailabilityWindow(windowStart.Value, t1));
                windowStart = null;
            }
        }

        if (windowStart.HasValue)
        {
            windows.Add(new PublicAvailabilityWindow(windowStart.Value, boundaries[^1]));
        }

        return windows;
    }

    private static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> sorted)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in sorted)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, interval.End > merged[^1].End ? interval.End : merged[^1].End);
            }
            else
            {
                merged.Add(interval);
            }
        }
        return merged;
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
    public Task<PublicMonthView> GetMonthViewAsync(DateTime monthAnchor, CancellationToken ct = default) =>
        GetRangeViewAsync(MonthGridStart(monthAnchor), MonthGridEnd(monthAnchor).AddDays(1), $"public-month:{monthAnchor:yyyyMM}", ct);

    /// <summary>The public Week view's data - same shape as GetMonthViewAsync, just scoped to a
    /// precise 7-day window instead of the month's whole displayed grid.</summary>
    public Task<PublicMonthView> GetWeekViewAsync(DateTime weekStart, CancellationToken ct = default) =>
        GetRangeViewAsync(weekStart.Date, weekStart.Date.AddDays(7), $"public-week:{weekStart:yyyyMMdd}", ct);

    /// <summary>The public Day view's data - same shape again, scoped to a single day.</summary>
    public Task<PublicMonthView> GetDayViewAsync(DateTime day, CancellationToken ct = default) =>
        GetRangeViewAsync(day.Date, day.Date.AddDays(1), $"public-day:{day:yyyyMMdd}", ct);

    private async Task<PublicMonthView> GetRangeViewAsync(DateTime start, DateTime end, string cacheKey, CancellationToken ct)
    {
        if (cache.TryGetValue(cacheKey, out PublicMonthView? cached) && cached is not null)
        {
            return cached;
        }

        var bookings = await bookingService.GetBookingsForAllSheetsAsync(start, end, ct);
        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);

        // Consolidates a multi-sheet booking's sibling sheet-events into one label, same as the
        // staff Week/Day grids (WeekGrid.razor/MonthGrid.razor's own DedupeKey) - the public
        // calendar shouldn't show "League Practice" five times for one 5-sheet booking either.
        // Unlike those two components (which group within a single already-selected day), this
        // runs over a whole month/week range at once, so Start/End stays part of the key - grouping
        // on DedupeKey alone would merge two different dates' occurrences of the same recurring
        // series, which share one BookingGroupId across every occurrence.
        var bookingLabels = bookings
            .GroupBy(b => (DedupeKey(b), b.Start, b.End))
            .Select(g =>
            {
                var b = g.First();
                var sheetCount = g.Count();
                var title = sheetCount > 1 ? $"{PublicTitle(b)} · {sheetCount} sheets" : PublicTitle(b);
                return new PublicMonthBooking(title, b.Category.ToString(), b.Start, b.End, b.State == BookingState.Confirmed);
            })
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

    private static bool OverlapsRange(DateTime start, DateTime end, ClubEvent closure)
    {
        // ClubEvent.End is already the inclusive last day for all-day events (converted back from
        // Graph's exclusive end-date convention in ClubEventService) - add a day to get the
        // exclusive boundary needed for this comparison. Timed closures already have an exact End.
        var closureEnd = closure.IsAllDay ? closure.End.Date.AddDays(1) : closure.End;
        return start < closureEnd && end > closure.Start;
    }

    // Staff are trusted to keep renter PII out of a booking title they type themselves (§2.3) - but
    // a Breely-originated booking's title (the customer's real name, RenterName = client_full_name)
    // is populated automatically, with no staff opportunity to redact it, and this app's own privacy
    // discipline (D11) says the public surface should never depend on staff remembering to do that.
    // Found live 2026-08-04 while reviewing the Breely integration: the public calendar was showing
    // real customer names for confirmed Breely bookings. A booking's ExternalBookingId is only ever
    // set by the webhook (never by staff-facing UI), so it's a reliable signal to substitute a
    // neutral label here without affecting the staff-facing calendar, which still shows the real name.
    private static string PublicTitle(SheetBooking b) =>
        !string.IsNullOrWhiteSpace(b.ExternalBookingId)
            ? CalendarStyles.CategoryLabel(b.Category)
            : (string.IsNullOrWhiteSpace(b.RenterName) ? b.Category.ToString() : b.RenterName);

    // Mirrors MonthGrid.razor/WeekGrid.razor's own DedupeKey exactly: BookingGroupId when it's a
    // real, persisted group id; falls back to (SheetMailbox, EventId) for Guid.Empty, since that's
    // the default for an untouched occurrence of a recurring series - grouping on it directly would
    // merge unrelated bookings that happen to share that same empty default.
    private static string DedupeKey(SheetBooking b) =>
        b.BookingGroupId != Guid.Empty ? b.BookingGroupId.ToString() : $"{b.SheetMailbox}|{b.EventId}";

    private static string SheetLabel(string sheetMailbox)
    {
        var localPart = sheetMailbox.Split('@')[0];
        var digits = new string(localPart.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? $"Sheet {digits}" : localPart;
    }
}
