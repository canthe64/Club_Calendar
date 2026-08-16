using FacilityScheduler.Domain;

namespace FacilityScheduler;

/// <summary>
/// The app's own color/border mapping for categories and hold-vs-confirmed state, independent
/// of Exchange's category colors (architecture doc S4.1 design rule). Blue=group event, green=league,
/// magenta-purple=event, orange=bonspiel, grey=maintenance, lavender=practice ice, red=other.
/// Dashed border = hold (not yet confirmed), solid = confirmed.
///
/// Practice Ice's lavender is deliberately blue-leaning (hue ~250) rather than a true light lavender:
/// it has to sit next to Event's magenta-leaning purple (hue ~294) on the same grid and stay
/// distinguishable, and a confirmed booking uses this color as a chip background with white text, so
/// it also has to stay dark enough to read - the whole palette sits around a 4:1 contrast ratio.
/// `docs/provision-categories.ps1` mirrors these choices onto Exchange's own master category list so
/// the Outlook read-only fallback view (D2) doesn't show a different color scheme than the web UI.
/// </summary>
public static class CalendarStyles
{
    public static string CategoryColor(BookingCategory category) => category switch
    {
        BookingCategory.GroupEvent => "#2d5f8a",
        BookingCategory.League => "#4a8a5f",
        BookingCategory.Event => "#a05fa8",
        BookingCategory.Bonspiel => "#c2622f",
        BookingCategory.Maintenance => "#8a97a3",
        BookingCategory.PracticeIce => "#7a6acb",
        BookingCategory.Other => "#c0392b",
        _ => "#c0392b"
    };

    public static string CategoryLightBg(BookingCategory category) => category switch
    {
        BookingCategory.GroupEvent => "#eaf1f8",
        BookingCategory.League => "#e8f3ec",
        BookingCategory.Event => "#f3e8f5",
        BookingCategory.Bonspiel => "#f7e8e0",
        BookingCategory.Maintenance => "#eef1f3",
        BookingCategory.PracticeIce => "#edeaf9",
        BookingCategory.Other => "#fbeae8",
        _ => "#fbeae8"
    };

    /// <summary>
    /// Human-readable display text for a category - separate from the enum's own ToString(),
    /// which stays the literal value written to/read from Graph's Categories property (so renaming
    /// a display label here never touches the wire format or breaks Enum.TryParse round-tripping).
    /// Only multi-word categories need an entry; everything else already reads fine as its raw name.
    /// </summary>
    public static string CategoryLabel(BookingCategory category) => category switch
    {
        BookingCategory.GroupEvent => "Group Event",
        BookingCategory.PracticeIce => "Practice Ice",
        _ => category.ToString()
    };

    public static string BorderStyle(SheetBooking booking) =>
        booking.State == BookingState.Hold
            ? $"1.5px dashed {CategoryColor(booking.Category)}"
            : $"1.5px solid {CategoryColor(booking.Category)}";

    public static string BackgroundStyle(SheetBooking booking) =>
        booking.State == BookingState.Hold ? CategoryLightBg(booking.Category) : CategoryColor(booking.Category);

    public static string TextColorStyle(SheetBooking booking) =>
        booking.State == BookingState.Hold ? CategoryColor(booking.Category) : "#ffffff";

    public static string EmptySlotBg { get; } = "#f6f8f9";

    /// <summary>
    /// Categories selectable for a per-sheet booking. Event is reserved for Club Events (Phase 6) -
    /// a whole-club resource, not a per-sheet booking - so it's excluded here even though it stays
    /// in the BookingCategory enum. Not migrating any existing dev-tenant data tagged Event.
    /// </summary>
    public static readonly BookingCategory[] SheetCategories =
        Enum.GetValues<BookingCategory>().Where(c => c != BookingCategory.Event).ToArray();

    /// <summary>Club Events category colors: Bonspiel=orange (mirrors the Exchange master category
    /// provisioned in Phase 1), Activities=teal, Closure=gray, Other=neutral.</summary>
    public static string ClubEventCategoryColor(ClubEventCategory category) => category switch
    {
        ClubEventCategory.Bonspiel => "#c2622f",
        ClubEventCategory.Activities => "#2e7d8c",
        ClubEventCategory.Closure => "#6b7680",
        ClubEventCategory.Other => "#9c9690",
        _ => "#9c9690"
    };

    public static string ClubEventCategoryLightBg(ClubEventCategory category) => category switch
    {
        ClubEventCategory.Bonspiel => "#f7e8e0",
        ClubEventCategory.Activities => "#e2eef0",
        ClubEventCategory.Closure => "#eef0f2",
        ClubEventCategory.Other => "#f1efec",
        _ => "#f1efec"
    };

    /// <summary>
    /// A third, distinct border style layered on top of every club event chip/band (Month/Week/Day),
    /// on top of whatever solid category-color fill it already has - dashed = sheet booking hold,
    /// solid = sheet booking confirmed, dotted = club event, so the border style alone identifies
    /// what kind of thing a calendar item is, independent of its category color. White (not the
    /// category color) so it reads consistently against every club event background, including the
    /// darker closure red.
    /// </summary>
    public const string ClubEventBorderStyle = "1.5px dotted rgba(255,255,255,.85)";

    /// <summary>
    /// Compact start-time prefix for calendar cell titles (e.g. "7PM - League Practice", or
    /// "7:30PM - ..." when not exactly on the hour) - no space before the AM/PM designator, and no
    /// ":00" for on-the-hour times. Shared by every staff grid (Month/Week/Day) and the public
    /// calendar, so both surfaces read the same way.
    /// </summary>
    public static string CellStartTimeLabel(DateTime start) =>
        start.ToString(start.Minute == 0 ? "htt" : "h:mmtt");

    /// <summary>
    /// Half-hour increments covering a full 24-hour day (midnight through the following midnight,
    /// inclusive), shared by every booking/series/club-event time picker - staff can book any sheet
    /// at any hour, not just a fixed daytime window. Midnight-at-the-end is expressed as 1440
    /// (minutes from the anchor day's midnight) rather than 0, so an end time reads unambiguously
    /// as the end of the current day rather than the start of it; 0 itself is unambiguous as a
    /// start time (there's nothing earlier in the same day to confuse it with).
    /// </summary>
    public static readonly int[] TimeOptionsMinutes =
        Enumerable.Range(0, 49).Select(i => i * 30).ToArray();

    public static string FormatMinutes(int minutesFromMidnight)
    {
        if (minutesFromMidnight >= 24 * 60)
        {
            return "Midnight";
        }

        var t = new DateTime(1, 1, 1, 0, 0, 0).AddMinutes(minutesFromMidnight);
        return t.ToString(minutesFromMidnight % 60 == 0 ? "h tt" : "h:mm tt");
    }

    /// <summary>
    /// The shared vertical time axis for the Week/Day hourly grids - full 24-hour range (item 5),
    /// centralized here so both grids always agree on hour positions instead of each maintaining its
    /// own copy that could drift out of sync.
    /// </summary>
    public static readonly int[] HourRows = Enumerable.Range(0, 24).ToArray();

    // Raised from 34px alongside the cell font-size increase. The binding constraint is a
    // half-hour booking, whose chip is only half a row tall: at 34px that's ~17px of box for ~13.5px
    // of text plus padding, which clips. 38px gives it room without stretching the day out further
    // than the text increase warrants. Both the staff and public Week/Day grids read this, so they
    // stay in lockstep by construction.
    public const double RowHeightPx = 38;
    public const double RowGapPx = 3;
    public static readonly int FirstHour = HourRows[0];
    public static readonly int LastHour = HourRows[^1] + 1; // exclusive upper bound (midnight cap)
    public const double PxPerHour = RowHeightPx + RowGapPx;

    public static string FormatHour(int hour) => new DateTime(1, 1, 1, hour, 0, 0).ToString("h tt");

    /// <summary>Hours-from-midnight as a double (not DateTime.Hour) so a half-hour start/end (e.g.
    /// 6:30 PM) positions correctly instead of rounding down to the containing hour. Measured from
    /// the grid's own anchor date rather than dt.Date so a time landing exactly at midnight the next
    /// day reads as 24.0, not 0.0.</summary>
    public static double HoursFromMidnight(DateTime dt, DateTime anchorDate) => (dt - anchorDate.Date).TotalHours;

    public static double TopPx(DateTime start, DateTime anchorDate)
    {
        var s = Math.Max(HoursFromMidnight(start, anchorDate), FirstHour);
        return (s - FirstHour) * PxPerHour;
    }

    public static double HeightPx(DateTime start, DateTime end, DateTime anchorDate)
    {
        var s = Math.Max(HoursFromMidnight(start, anchorDate), FirstHour);
        var e = Math.Min(HoursFromMidnight(end, anchorDate), LastHour);
        var hours = Math.Max(0.5, e - s);
        return hours * PxPerHour - RowGapPx;
    }

    /// <summary>
    /// Removes every blocker interval from [start, end), splitting it into 0-2+ remaining
    /// sub-ranges as needed (e.g. a blocker in the middle splits one range into two). Shared by
    /// PublicAvailabilityService (excluding other bookings from a hold's advertised window) and
    /// SheetBookingService (trimming a hold when an external booking claims part of it) so both
    /// use the exact same interval math.
    /// </summary>
    public static List<(DateTime Start, DateTime End)> SubtractIntervals(DateTime start, DateTime end, List<(DateTime Start, DateTime End)> blockers)
    {
        var segments = new List<(DateTime Start, DateTime End)> { (start, end) };

        foreach (var blocker in blockers.OrderBy(b => b.Start))
        {
            var next = new List<(DateTime Start, DateTime End)>();
            foreach (var seg in segments)
            {
                if (blocker.End <= seg.Start || blocker.Start >= seg.End)
                {
                    next.Add(seg);
                    continue;
                }

                if (blocker.Start > seg.Start)
                {
                    next.Add((seg.Start, blocker.Start));
                }

                if (blocker.End < seg.End)
                {
                    next.Add((blocker.End, seg.End));
                }
            }
            segments = next;
        }

        return segments;
    }

    /// <summary>
    /// Classic calendar-view lane layout, generic so both the staff Week grid and the public Week
    /// view use the exact same algorithm instead of each maintaining their own copy: walk items in
    /// start order, grouping any that time-overlap into a cluster, then greedily assign each
    /// cluster's items to the first lane whose previous occupant has already ended - concurrent
    /// items render side-by-side instead of overlapping, with each cluster's own lane count (not
    /// the whole day's busiest moment) determining every item's width within it.
    /// </summary>
    public static List<(T Item, int Lane, int LaneCount)> LayoutLanes<T>(
        IEnumerable<T> items, Func<T, DateTime> start, Func<T, DateTime> end)
    {
        var sorted = items.OrderBy(start).ToList();
        var result = new List<(T Item, int Lane, int LaneCount)>();
        var cluster = new List<T>();
        var clusterEnd = DateTime.MinValue;

        void FlushCluster()
        {
            if (cluster.Count == 0)
            {
                return;
            }

            var laneEnds = new List<DateTime>();
            var startIndex = result.Count;
            foreach (var item in cluster)
            {
                var lane = laneEnds.FindIndex(e => e <= start(item));
                if (lane == -1)
                {
                    laneEnds.Add(end(item));
                    lane = laneEnds.Count - 1;
                }
                else
                {
                    laneEnds[lane] = end(item);
                }

                result.Add((item, lane, 0)); // lane count backfilled below
            }

            var laneCount = laneEnds.Count;
            for (var i = startIndex; i < result.Count; i++)
            {
                result[i] = (result[i].Item, result[i].Lane, laneCount);
            }

            cluster.Clear();
        }

        foreach (var item in sorted)
        {
            if (cluster.Count > 0 && start(item) >= clusterEnd)
            {
                FlushCluster();
            }

            cluster.Add(item);
            clusterEnd = cluster.Count == 1 ? end(item) : (end(item) > clusterEnd ? end(item) : clusterEnd);
        }

        FlushCluster();
        return result;
    }
}
