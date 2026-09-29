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
            // Projected to the public wire DTO here, at the point it actually leaves this service -
            // everything upstream of this line (including GetConcurrentAvailabilityAsync's own use of
            // GetOpenSlotsAsync below) works with the mailbox-carrying SheetSlot instead (code review
            // C2), so grouping/counting logic never has to trust the label as if it were unique.
            .Select(s => new PublicSheetSlot(s.SheetLabel, s.Start, s.End))
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

    /// <summary>Internal-only counterpart to <see cref="PublicSheetSlot"/> - carries the real sheet
    /// mailbox alongside the display label (code review C2). <see cref="PublicSheetSlot"/> itself
    /// stays label-only because it's also the public JSON wire shape (D11's minimization stance -
    /// never expose a raw mailbox address publicly); this type exists so grouping/counting logic can
    /// key on the mailbox - which is always unique - rather than the label, which two differently-
    /// named mailboxes could collapse to the same value under (e.g. "north1@..."/"south1@..." both
    /// reducing to "Sheet 1"). Never serialized; only <see cref="PublicSheetSlot"/> crosses the wire.</summary>
    private readonly record struct SheetSlot(string SheetMailbox, string SheetLabel, DateTime Start, DateTime End);

    /// <summary>Every genuinely open (GroupEvent+Hold) slot in a window, across every sheet - the
    /// same minimization/closure-exclusion rule GetAvailabilityAsync already applies, factored out
    /// so GetConcurrentAvailabilityAsync can reuse it for an arbitrary staff-facing search range
    /// instead of only "the next N days from today".</summary>
    private async Task<List<SheetSlot>> GetOpenSlotsAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(start, end, ct);
        var clubEvents = await clubEventService.GetEventsAsync(start, end, ct);

        // Never publicly promise a sheet that's actually closed for a club-wide event - this is a
        // public-view-only correctness check, distinct from D13's staff-side "no cross-check"
        // decision (which was specifically about the internal write path).
        var closures = clubEvents.Where(ce => ce.MarksSheetsUnavailable).ToList();

        var holds = bookings.Where(b => b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold).ToList();
        var result = new List<SheetSlot>();

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

                result.Add(new SheetSlot(hold.SheetMailbox, SheetLabel(hold.SheetMailbox), segStart, segEnd));
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
    private static List<PublicAvailabilityWindow> FindConcurrentAvailability(List<SheetSlot> slots, int minSheets)
    {
        // Grouped by mailbox, not label (code review C2) - the label is display-only and two
        // differently-named mailboxes can reduce to the same one (see SheetSlot's own doc comment),
        // which would previously collapse two genuinely distinct sheets into one for counting
        // purposes, under-reporting concurrent availability.
        var perSheetBlocks = slots
            .GroupBy(s => s.SheetMailbox)
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
    /// Every time a member can start hosting practice ice (docs/practice-ice-hosting-design.md §3.1,
    /// widened 2026-09-28): a 30-minute grid start, inside the eligible hours, lead time, horizon,
    /// and booking season, from which at least PracticeIce:MinOpenSheets sheets stay free for the
    /// shortest session. "Free" is <see cref="MemberFreeTime"/>'s rule - nothing booked, or an open
    /// Group Event hold guests can no longer book. Some sheets may be in use; a session runs on the
    /// free ones only.
    /// </summary>
    public async Task<List<PracticeIceStartOption>> GetPracticeIceStartsAsync(CancellationToken ct = default)
    {
        var (earliestStart, latestEnd) = MemberIceRange();
        var cacheKey = $"practice-ice-starts:{earliestStart:yyyyMMddHHmm}:{latestEnd:yyyyMMddHHmm}";
        if (cache.TryGetValue(cacheKey, out List<PracticeIceStartOption>? cached) && cached is not null)
        {
            return cached;
        }

        var (free, _) = await GetMemberIceSnapshotAsync(latestEnd, ct);
        var result = new List<PracticeIceStartOption>();

        for (var day = earliestStart.Date; day < latestEnd; day = day.AddDays(1))
        {
            var dayEnd = Min(day.AddHours(facility.PracticeIceEligibleEndHour), latestEnd);
            for (var t = Max(day.AddHours(facility.PracticeIceEligibleStartHour), earliestStart);
                 t.AddMinutes(PracticeIceRules.MinSessionMinutes) <= dayEnd;
                 t = t.AddMinutes(PracticeIceRules.SlotIntervalMinutes))
            {
                var runs = new List<SheetFreeRun>();
                foreach (var sheet in facility.SheetMailboxes)
                {
                    var interval = free[sheet].FirstOrDefault(i => i.Start <= t && i.End > t);
                    if (interval == default)
                    {
                        continue;
                    }

                    var freeUntil = RoundDownToGrid(Min(interval.End, dayEnd), PracticeIceRules.SlotIntervalMinutes);
                    if (freeUntil >= t.AddMinutes(PracticeIceRules.MinSessionMinutes))
                    {
                        runs.Add(new SheetFreeRun(sheet, freeUntil));
                    }
                }

                if (runs.Count >= facility.PracticeIceMinOpenSheets)
                {
                    result.Add(new PracticeIceStartOption(t, runs));
                }
            }
        }

        cache.Set(cacheKey, result, CacheTtl);
        viewCache.Track(cacheKey);
        return result;
    }

    /// <summary>The practice ice start option at exactly <paramref name="start"/>, if it's still
    /// offered - used both to build the request page's duration/sheet choices and to re-validate a
    /// submission server-side, since the query string carrying the start is untrusted and can be
    /// stale. A courtesy check against an up-to-60s-cached view, not the safety mechanism - the
    /// write path's own live, locked conflict check is what prevents a double booking (§4.3).</summary>
    public async Task<PracticeIceStartOption?> GetPracticeIceStartAsync(DateTime start, CancellationToken ct = default) =>
        (await GetPracticeIceStartsAsync(ct)).FirstOrDefault(o => o.Start == start);

    /// <summary>
    /// Every two-hour make-up game slot (staff request 2026-09-28): a 30-minute grid start inside
    /// the practice ice eligible start hours, lead time, horizon, and booking season, where some
    /// sheet is free (<see cref="MemberFreeTime"/>'s rule) for the whole two hours AND another sheet
    /// has a confirmed booking for the whole two hours - make-up games need someone already running
    /// the club, so open or tentative ice alone never qualifies. The game goes on the
    /// highest-numbered free sheet.
    /// </summary>
    public async Task<List<MakeUpGameOption>> GetMakeUpGameOptionsAsync(CancellationToken ct = default)
    {
        var (earliestStart, latestEnd) = MemberIceRange();
        var cacheKey = $"make-up-game-options:{earliestStart:yyyyMMddHHmm}:{latestEnd:yyyyMMddHHmm}";
        if (cache.TryGetValue(cacheKey, out List<MakeUpGameOption>? cached) && cached is not null)
        {
            return cached;
        }

        var (free, confirmedBusy) = await GetMemberIceSnapshotAsync(latestEnd, ct);
        var sheetsHighestFirst = facility.SheetMailboxes.Reverse().ToList();
        var result = new List<MakeUpGameOption>();

        static bool Covers(List<(DateTime Start, DateTime End)> intervals, DateTime from, DateTime to) =>
            intervals.Any(i => i.Start <= from && i.End >= to);

        for (var day = earliestStart.Date; day < latestEnd; day = day.AddDays(1))
        {
            // The eligible hours bound when a game can START; a 2-hour game may run past the end hour.
            var lastStart = day.AddHours(facility.PracticeIceEligibleEndHour);
            for (var t = Max(day.AddHours(facility.PracticeIceEligibleStartHour), earliestStart);
                 t < lastStart && t.AddMinutes(MakeUpGameRules.DurationMinutes) <= latestEnd;
                 t = t.AddMinutes(PracticeIceRules.SlotIntervalMinutes))
            {
                var end = t.AddMinutes(MakeUpGameRules.DurationMinutes);
                var sheet = sheetsHighestFirst.FirstOrDefault(s => Covers(free[s], t, end));
                if (sheet is null || !facility.SheetMailboxes.Any(s => s != sheet && Covers(confirmedBusy[s], t, end)))
                {
                    continue;
                }

                result.Add(new MakeUpGameOption(t, end, sheet));
            }
        }

        cache.Set(cacheKey, result, CacheTtl);
        viewCache.Track(cacheKey);
        return result;
    }

    /// <summary>The make-up game option starting at exactly <paramref name="start"/>, if still
    /// offered - same courtesy re-validation role as <see cref="GetPracticeIceStartAsync"/>.</summary>
    public async Task<MakeUpGameOption?> GetMakeUpGameOptionAsync(DateTime start, CancellationToken ct = default) =>
        (await GetMakeUpGameOptionsAsync(ct)).FirstOrDefault(o => o.Start == start);

    /// <summary>[earliest start, latest end] for member-hosted ice: now + lead time to now +
    /// horizon, both grid-aligned, then clipped to the booking season - a member is never offered a
    /// slot the facility isn't operating for.</summary>
    private (DateTime EarliestStart, DateTime LatestEnd) MemberIceRange()
    {
        var now = facility.Now;
        var earliestStart = RoundUpToGrid(now.AddHours(facility.PracticeIceMinLeadHours), PracticeIceRules.SlotIntervalMinutes);
        var latestEnd = RoundDownToGrid(now.AddDays(facility.PracticeIceMaxHorizonDays), PracticeIceRules.SlotIntervalMinutes);

        if (window.SeasonStartDate is { } seasonStart && earliestStart < seasonStart)
        {
            earliestStart = seasonStart;
        }
        if (window.SeasonEndDate is { } seasonEnd && latestEnd > seasonEnd.Date.AddDays(1))
        {
            latestEnd = seasonEnd.Date.AddDays(1);
        }

        return (earliestStart, latestEnd);
    }

    /// <summary>Per sheet: its member-usable free time (<see cref="MemberFreeTime"/>) and its merged
    /// confirmed-busy time, from today through <paramref name="latestEnd"/>'s day.</summary>
    private async Task<(Dictionary<string, List<(DateTime Start, DateTime End)>> Free, Dictionary<string, List<(DateTime Start, DateTime End)>> ConfirmedBusy)>
        GetMemberIceSnapshotAsync(DateTime latestEnd, CancellationToken ct)
    {
        var rangeStart = facility.Today;
        var rangeEnd = latestEnd.Date.AddDays(1);
        var bookings = await bookingService.GetBookingsForAllSheetsAsync(rangeStart, rangeEnd, ct);
        var clubEvents = await clubEventService.GetEventsAsync(rangeStart, rangeEnd, ct);

        var free = MemberFreeTime(facility.SheetMailboxes, bookings, clubEvents, rangeStart, rangeEnd, facility.GroupEventHoldReleaseCutoff);
        var confirmedBusy = facility.SheetMailboxes.ToDictionary(sheet => sheet, sheet => MergeIntervals(bookings
            .Where(b => b.SheetMailbox == sheet && b.State == BookingState.Confirmed)
            .Select(b => (b.Start, b.End))
            .OrderBy(i => i.Start)
            .ToList()));

        return (free, confirmedBusy);
    }

    /// <summary>
    /// Per sheet, the time over [start, end) a member may use for practice ice or a make-up game:
    /// nothing booked there, and not inside an ice-blocking club event. An open Group Event hold is
    /// the one booking that doesn't block, for the part of it before <paramref name="holdCutoff"/> -
    /// guests can only book a group event more than PracticeIce:GroupEventHoldReleaseDays ahead, so
    /// hold time inside that window can no longer sell; whichever request takes it trims the hold.
    /// Every other booking - any category, any state, including pending practice ice - blocks.
    /// </summary>
    internal static Dictionary<string, List<(DateTime Start, DateTime End)>> MemberFreeTime(
        IEnumerable<string> sheets, IEnumerable<SheetBooking> bookings, IEnumerable<ClubEvent> clubEvents,
        DateTime start, DateTime end, DateTime holdCutoff)
    {
        var bookingList = bookings.ToList();
        var closures = clubEvents
            .Where(ce => ce.MarksSheetsUnavailable)
            .Select(ce => (ce.Start, End: ce.ExclusiveEnd))
            .ToList();

        var result = new Dictionary<string, List<(DateTime Start, DateTime End)>>();
        foreach (var sheet in sheets)
        {
            var blockers = new List<(DateTime Start, DateTime End)>(closures);
            foreach (var b in bookingList.Where(b => b.SheetMailbox == sheet && b.Start < end && b.End > start))
            {
                if (!PracticeIceRules.IsReleasableHold(b))
                {
                    blockers.Add((b.Start, b.End));
                }
                else if (b.End > holdCutoff)
                {
                    blockers.Add((Max(b.Start, holdCutoff), b.End));
                }
            }

            result[sheet] = CalendarStyles.SubtractIntervals(start, end, blockers)
                .Where(seg => seg.End > seg.Start)
                .ToList();
        }

        return result;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

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

        // Only a booking that came through the member request flow (PracticeIceRequestService.
        // SubmitAsync) has RenterName = the host's own name - and only that flow ever sets
        // RenterEmail on a PracticeIce booking (the staff form hides the email field for every
        // category but Rental). A staff-created PracticeIce booking's RenterName is a free-text
        // *title* (e.g. "Practice Ice Hosted by Jeff Pearson"), which PracticeIceTitle's own
        // "Hosted by" wrapper would double up into "Practice Ice - Hosted by Practice Ice Hosted by
        // Jeff Pearson" - live-found 2026-09-24. Staff titles show as typed, same as every other
        // category (§2.3).
        if (b.Category == BookingCategory.PracticeIce)
        {
            if (!string.IsNullOrWhiteSpace(b.RenterEmail))
            {
                return PracticeIceTitle(b.RenterName);
            }

            // Staff-typed title: as typed, except still never a bare address, and never blank.
            return !string.IsNullOrWhiteSpace(b.RenterName) && !b.RenterName.Contains('@')
                ? b.RenterName
                : CalendarStyles.CategoryLabel(BookingCategory.PracticeIce);
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

    // Delegates to the shared helper (code review O10) - this copy used to be left deliberately
    // separate (architecture doc §4.12) on the reasoning that it was display-only and "genuinely
    // different" from CalendarStyles' own use. C2 above shows that reasoning no longer holds: this
    // label was doubling as an identity key for a real correctness property (concurrent-availability
    // counting), not just display - now that grouping/counting is keyed on the mailbox instead
    // (SheetSlot), there's nothing left distinguishing this copy from the canonical one.
    private static string SheetLabel(string sheetMailbox) => CalendarStyles.SheetLabel(sheetMailbox);
}
