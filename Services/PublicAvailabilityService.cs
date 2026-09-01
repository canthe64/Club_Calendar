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
public class PublicAvailabilityService(SheetBookingService bookingService, ClubEventService clubEventService, IMemoryCache cache, FacilityConfiguration facility, ViewCacheRegistry viewCache, SchedulingWindowService window)
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
        viewCache.Track(cacheKey);
        return response;
    }

    private async Task<PublicAvailabilityResponse> ComputeAvailabilityAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        // Publish-cutoff filtering happens here, not inside GetOpenSlotsAsync - that method is
        // shared with GetConcurrentAvailabilityAsync (/public/search), which the cutoff deliberately
        // does not apply to (season filtering, applied inside GetOpenSlotsAsync itself, does cover
        // both). Season filtering already ran by the time openSlots gets here.
        var openSlots = (await GetOpenSlotsAsync(start, end, ct))
            .Where(s => !window.IsPastPublicCutoff(s.Start))
            .ToList();

        var clubEvents = (await clubEventService.GetEventsAsync(start, end, ct))
            .Where(ce => !window.IsPastPublicCutoff(ce.Start));
        // No PublicClubEventNotes(ce) here (unlike the calendar-page mapping below) - Notes is
        // [JsonIgnore]d off PublicClubEventLabel's wire format (D108), so computing/truncating it for
        // a response that will never carry it is pure waste.
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

                // Season filtering, not cutoff - this method feeds both the JSON widget and
                // /public/search, and the season window (unlike the publish cutoff) applies to
                // both: a member should never find and click a slot the facility isn't open for.
                if (window.IsOutsideSeason(segStart))
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
        viewCache.Track(cacheKey);
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
    /// Windows, aligned to a 30-minute grid, where every sheet is simultaneously free of any booking
    /// - any category, any state - and not covered by an ice-blocking club event: the pool of times
    /// a member could volunteer to host practice ice (docs/practice-ice-hosting-design.md §3.1).
    /// Deliberately different from GetOpenSlotsAsync above, which reports existing GroupEvent+Hold
    /// rental inventory - group events, by policy, always take priority over practice ice, so
    /// anything already on the calendar (sold or not) blocks a window here. Clipped to
    /// [now + PracticeIceMinLeadHours, now + PracticeIceMaxHorizonDays] and floored at
    /// PracticeIceRules.MinSessionMinutes so a sliver too short for any real session isn't offered.
    /// </summary>
    public async Task<List<PublicAvailabilityWindow>> GetPracticeIceWindowsAsync(CancellationToken ct = default)
    {
        var now = facility.Now;
        var earliestStart = RoundUpToGrid(now.AddHours(facility.PracticeIceMinLeadHours), PracticeIceRules.SlotIntervalMinutes);
        var latestEnd = RoundDownToGrid(now.AddDays(facility.PracticeIceMaxHorizonDays), PracticeIceRules.SlotIntervalMinutes);

        // Season window layered on top of the existing lead-time/horizon clamp, same shape: a
        // member should never be offered (or able to submit against) a practice ice slot the
        // facility isn't operating for. The final per-window clamp below already drops anything
        // that ends up with End <= Start after this, so no separate filter is needed past this point.
        if (window.SeasonStartDate is { } seasonStart && earliestStart < seasonStart)
        {
            earliestStart = seasonStart;
        }
        if (window.SeasonEndDate is { } seasonEnd)
        {
            var seasonEndExclusive = seasonEnd.Date.AddDays(1);
            if (latestEnd > seasonEndExclusive)
            {
                latestEnd = seasonEndExclusive;
            }
        }

        var rangeStart = facility.Today;
        var rangeEnd = latestEnd.Date.AddDays(1);
        var cacheKey = $"practice-ice-windows:{rangeStart:yyyyMMdd}:{rangeEnd:yyyyMMdd}";

        if (cache.TryGetValue(cacheKey, out List<PublicAvailabilityWindow>? cached) && cached is not null)
        {
            return cached;
        }

        var freeSlots = await GetFreeSlotsAsync(rangeStart, rangeEnd, ct);
        var merged = FindConcurrentAvailability(freeSlots, facility.SheetMailboxes.Length);

        var windows = merged
            .Select(w => new PublicAvailabilityWindow(RoundUpToGrid(w.Start, PracticeIceRules.SlotIntervalMinutes), RoundDownToGrid(w.End, PracticeIceRules.SlotIntervalMinutes)))
            .Select(w => new PublicAvailabilityWindow(w.Start < earliestStart ? earliestStart : w.Start, w.End > latestEnd ? latestEnd : w.End))
            .Where(w => w.End - w.Start >= TimeSpan.FromMinutes(PracticeIceRules.MinSessionMinutes))
            .OrderBy(w => w.Start)
            .ToList();

        cache.Set(cacheKey, windows, CacheTtl);
        viewCache.Track(cacheKey);
        return windows;
    }

    /// <summary>The practice ice window (if any) that contains <paramref name="start"/> - used both
    /// to populate the request page's duration options and to re-validate a submission server-side,
    /// since the query string carrying the chosen start is untrusted and can be stale. This is a
    /// courtesy check against a up-to-60s-cached view, not the safety mechanism - CreateAcrossSheetsAsync's
    /// own live, locked conflict check is what actually prevents a double-booking (§4.3).</summary>
    public async Task<PublicAvailabilityWindow?> FindPracticeIceWindowContainingAsync(DateTime start, CancellationToken ct = default)
    {
        var windows = await GetPracticeIceWindowsAsync(ct);
        return windows.FirstOrDefault(w => start >= w.Start && start < w.End);
    }

    /// <summary>Every sheet's free time within eligible hours, per day, over [start,end) - the
    /// complement of GetOpenSlotsAsync's "already-advertised rental inventory": here, ANY booking
    /// (every category, every state) and any ice-blocking club event removes time from what's
    /// offered, since practice ice is only allowed when nothing else is planned, confirmed or not.</summary>
    private async Task<List<PublicSheetSlot>> GetFreeSlotsAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(start, end, ct);
        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);
        var closures = clubEvents
            .Where(ce => ce.MarksSheetsUnavailable)
            .Select(ce => (ce.Start, End: ce.ExclusiveEnd))
            .ToList();

        var result = new List<PublicSheetSlot>();
        foreach (var sheet in facility.SheetMailboxes)
        {
            var sheetBookings = bookings.Where(b => b.SheetMailbox == sheet).ToList();

            for (var day = start.Date; day < end; day = day.AddDays(1))
            {
                var dayStart = day.AddHours(facility.PracticeIceEligibleStartHour);
                var dayEnd = day.AddHours(facility.PracticeIceEligibleEndHour);

                var blockers = sheetBookings
                    .Where(b => b.Start < dayEnd && b.End > dayStart)
                    .Select(b => (b.Start, b.End))
                    .Concat(closures.Where(c => c.Start < dayEnd && c.End > dayStart))
                    .ToList();

                foreach (var (segStart, segEnd) in CalendarStyles.SubtractIntervals(dayStart, dayEnd, blockers))
                {
                    if (segEnd > segStart)
                    {
                        result.Add(new PublicSheetSlot(SheetLabel(sheet), segStart, segEnd));
                    }
                }
            }
        }

        return result;
    }

    private static DateTime RoundUpToGrid(DateTime t, int minutes)
    {
        var gridTicks = TimeSpan.FromMinutes(minutes).Ticks;
        var remainder = t.Ticks % gridTicks;
        return remainder == 0 ? t : t.AddTicks(gridTicks - remainder);
    }

    private static DateTime RoundDownToGrid(DateTime t, int minutes)
    {
        var gridTicks = TimeSpan.FromMinutes(minutes).Ticks;
        return t.AddTicks(-(t.Ticks % gridTicks));
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

        // Cutoff, not season - this feeds /public/calendar, which is display of what's actually on
        // the books (already impossible to create off-season going forward), not "advertised open
        // inventory". A club event straddling the cutoff shows if it starts on or before it, same
        // rule IsPastPublicCutoff already encodes (> cutoff, not >=).
        var bookings = (await bookingService.GetBookingsForAllSheetsAsync(start, end, ct))
            .Where(b => !window.IsPastPublicCutoff(b.Start))
            .ToList();
        var clubEvents = (await clubEventService.GetEventsAsync(start, end, ct))
            .Where(ce => !window.IsPastPublicCutoff(ce.Start))
            .ToList();

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
                return new PublicMonthBooking(title, b.Category.ToString(), b.Start, b.End, b.State == BookingState.Confirmed, PublicBookingNotes(b));
            })
            .ToList();

        var eventLabels = clubEvents
            .Select(ce => new PublicClubEventLabel(ce.Title, ce.Category, ce.Start, ce.End, ce.IsAllDay, ce.MarksSheetsUnavailable, PublicClubEventNotes(ce)))
            .ToList();

        var view = new PublicMonthView(bookingLabels, eventLabels);
        cache.Set(cacheKey, view, CacheTtl);
        viewCache.Track(cacheKey);
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
    private static string PublicTitle(SheetBooking b)
    {
        if (!string.IsNullOrWhiteSpace(b.ExternalBookingId))
        {
            return CalendarStyles.CategoryLabel(b.Category);
        }

        if (b.Category == BookingCategory.PracticeIce)
        {
            return PracticeIceTitle(b.RenterName);
        }

        return string.IsNullOrWhiteSpace(b.RenterName) ? b.Category.ToString() : b.RenterName;
    }

    // A publishable cap, not a chip-width one - Notes only ever reaches the public calendar's
    // click-to-detail overlay (a 340px modal that wraps text over multiple lines), never an inline
    // cell, so this is sized for "a genuinely useful sentence or two" rather than TruncateForConflict-
    // Display's 40-char default for a single dense line. Reuses that same helper (its own maxChars
    // override) rather than a second ellipsis implementation.
    private const int PublicNotesMaxChars = 300;

    // Same reasoning and same signal as PublicTitle's Breely check above (D52), applied to Notes
    // instead of the title (D108, staff feedback 2026-08-27): a Breely-originated booking's Notes is
    // BuildNotes' own fixed template (BreelyBookingProcessor), never staff-reviewed, so it's withheld
    // here exactly like the real customer name already is. A staff-typed Note is trusted the same way
    // a staff-typed title already is.
    private static string? PublicBookingNotes(SheetBooking b) =>
        string.IsNullOrWhiteSpace(b.ExternalBookingId) && !string.IsNullOrWhiteSpace(b.Notes)
            ? CalendarStyles.TruncateForConflictDisplay(b.Notes, PublicNotesMaxChars)
            : null;

    // The Club Event equivalent of PublicBookingNotes above. ClubEvent has no ExternalBookingId - the
    // Breely webhook never creates a booking there, only the occasional "⚠ Web booking needs review"
    // triage marker (FlagNeedsTriageAsync) when a claim matches no open hold. That marker's own Notes
    // embeds the real customer name Breely sent (client_full_name) plus an internal admin URL -
    // exactly the kind of unreviewed, PII-bearing text this gate exists to catch, just reached by a
    // different door than the booking case. BookedBy is the reliable signal instead: never staff-
    // settable through the UI (ClubEventDraft.ToClubEvent always writes the signed-in user's own
    // name), and set to this one constant only by that webhook path.
    private static string? PublicClubEventNotes(ClubEvent ce) =>
        ce.BookedBy != BreelyBookingProcessor.BookedByLabel && !string.IsNullOrWhiteSpace(ce.Notes)
            ? CalendarStyles.TruncateForConflictDisplay(ce.Notes, PublicNotesMaxChars)
            : null;

    // The host volunteers to run this session on behalf of the whole club, so naming them publicly
    // is a deliberate, accepted use of PII (docs/practice-ice-hosting-design.md §3.6) - but the
    // title must never be JUST the name (that's the generic RenterName-as-title behavior every other
    // category uses above, and reads as anonymous/unlabeled for a session anyone should feel
    // welcome at) and must never show a raw UPN/email if that's all a sign-in claim supplied instead
    // of a real display name - "Practice Ice" alone is the safe fallback either way.
    private static string PracticeIceTitle(string? renterName)
    {
        var label = CalendarStyles.CategoryLabel(BookingCategory.PracticeIce);
        return !string.IsNullOrWhiteSpace(renterName) && !renterName.Contains('@')
            ? $"{label} - Hosted by {renterName}"
            : label;
    }

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
