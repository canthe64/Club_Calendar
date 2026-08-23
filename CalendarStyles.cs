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

    /// <summary>
    /// The one key that identifies "the same conceptual booking, same occurrence" - combining
    /// <see cref="SheetBooking.BookingGroupId"/> (real bookings) with its own Start/End, or a
    /// per-event fallback for the <see cref="Guid.Empty"/> default.
    ///
    /// A <c>Guid.Empty</c> BookingGroupId isn't a real, shared identity - it's the default for an
    /// untouched occurrence of a recurring series (only reliably persists once an occurrence has
    /// been individually PATCHed, e.g. via an edit; live-confirmed 2026-07-16). Matching on it
    /// directly would risk bundling two completely unrelated bookings that happen to share that same
    /// empty default AND the same start/end time by coincidence; falling back to
    /// (SheetMailbox, EventId) - always unique per booking - means only bookings that share a
    /// genuine, real group id ever get merged.
    ///
    /// The Start/End component folds in what a click-to-detail handler needs beyond the group id
    /// alone: a multi-week recurring series shares one BookingGroupId across every occurrence, so
    /// group-id-only matching would merge every week's occurrence of a multi-sheet series into one
    /// "group" the instant more than one week's worth of data is loaded at once (Month view, or a
    /// wide search result set) - scoping by Start/End as well keeps a "group" to what it visually
    /// is: the same conceptual booking, on the same date.
    /// </summary>
    public static string BookingGroupKey(SheetBooking b) =>
        b.BookingGroupId != Guid.Empty
            ? $"{b.BookingGroupId}|{b.Start:O}|{b.End:O}"
            : $"{b.SheetMailbox}|{b.EventId}";

    /// <summary>Every booking in <paramref name="all"/> that shares <paramref name="clicked"/>'s
    /// group key - i.e. every sheet of the same conceptual booking, at the same occurrence. Shared by
    /// the calendar grids' click-to-detail handler and the search page, so both open the exact same
    /// set for the exact same booking rather than each keeping its own copy of this rule.</summary>
    public static List<SheetBooking> SiblingGroup(IEnumerable<SheetBooking> all, SheetBooking clicked)
    {
        var key = BookingGroupKey(clicked);
        return all.Where(b => BookingGroupKey(b) == key).ToList();
    }

    /// <summary>What a booking reads as when there's no dedicated title field - the renter's name if
    /// one was given, or the category label otherwise. Shared by every calendar grid chip, the detail
    /// modal, and the search page's result rows and title matcher, so all of them agree on what a
    /// booking "is called" by construction instead of each keeping its own copy in sync.</summary>
    public static string BookingDisplayTitle(SheetBooking b) =>
        string.IsNullOrWhiteSpace(b.RenterName) ? CategoryLabel(b.Category) : b.RenterName;

    public static string EmptySlotBg { get; } = "#f6f8f9";

    /// <summary>
    /// Categories selectable for a per-sheet booking. Event is reserved for Club Events (Phase 6) -
    /// a whole-club resource, not a per-sheet booking - so it's excluded here even though it stays
    /// in the BookingCategory enum. Not migrating any existing dev-tenant data tagged Event.
    /// </summary>
    public static readonly BookingCategory[] SheetCategories =
        Enum.GetValues<BookingCategory>().Where(c => c != BookingCategory.Event).ToArray();

    /// <summary>
    /// Club Events categories in the order they're offered in the picker - deliberately its own list
    /// rather than Enum.GetValues, so display order is a presentation decision rather than a
    /// consequence of declaration order. Every enum member must appear here; ClubEventCategoryTests
    /// enforces that, since one missing from this list would render on the calendar while being
    /// impossible to select.
    /// </summary>
    public static readonly ClubEventCategory[] ClubEventCategories =
    [
        ClubEventCategory.OutOfTownBonspiels,
        ClubEventCategory.Competitions,
        ClubEventCategory.Activities,
        ClubEventCategory.Meetings,
        ClubEventCategory.Closure,
        ClubEventCategory.Other
    ];

    /// <summary>
    /// Display text for a club event category. Same split as CategoryLabel does for sheet
    /// categories: the enum member name is the wire value (public API, and the literal string on
    /// the Graph event), the label is what a person reads. Only multi-word categories need an entry.
    /// </summary>
    public static string ClubEventCategoryLabel(ClubEventCategory category) => category switch
    {
        ClubEventCategory.OutOfTownBonspiels => "Out of Town Bonspiels",
        _ => category.ToString()
    };

    /// <summary>Club Events category colors: OutOfTownBonspiels=orange, Competitions=gold,
    /// Activities=teal, Meetings=cranberry, Closure=gray, Other=neutral. Mirrored onto Exchange's
    /// master category list by docs/provision-categories.ps1.</summary>
    public static string ClubEventCategoryColor(ClubEventCategory category) => category switch
    {
        ClubEventCategory.OutOfTownBonspiels => "#c2622f",
        ClubEventCategory.Competitions => "#b8860b",
        ClubEventCategory.Activities => "#2e7d8c",
        ClubEventCategory.Meetings => "#a63a5d",
        ClubEventCategory.Closure => "#6b7680",
        ClubEventCategory.Other => "#9c9690",
        _ => "#9c9690"
    };

    public static string ClubEventCategoryLightBg(ClubEventCategory category) => category switch
    {
        ClubEventCategory.OutOfTownBonspiels => "#f7e8e0",
        ClubEventCategory.Competitions => "#f7f0d9",
        ClubEventCategory.Activities => "#e2eef0",
        ClubEventCategory.Meetings => "#f5e5eb",
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

    /// <summary>
    /// A width floor for the calendar toolbar's date label, so the ‹/Today/› controls immediately
    /// right of it hold still while you step through dates. Without one the label sizes to its
    /// content and the controls move under the cursor - enough that clicking Next twice can land the
    /// second click somewhere else.
    ///
    /// Per view, not one global maximum: a single floor sized for Day view's longest string left a
    /// large dead gap in Month view, and nobody expects the controls to stay put across a view
    /// *switch* - only while stepping within one. Each value is the widest string that format can
    /// produce, measured exhaustively in the app's own 16px/600 font stack, plus ~10% headroom for
    /// host font-stack variance (the same variance that wrapped "OFF ICE" on a machine where it
    /// measured 46px inside a 52px box):
    ///   month "September 2026" 120px | week "May 28 - May 30, 2026" 166px |
    ///   day "Wednesday, September 30, 2026" 237px.
    ///
    /// Keyed by the lowercase view name both calendars already use in their own URLs, since the
    /// staff and public pages each have their own private ViewMode enum.
    /// </summary>
    public static int AnchorLabelMinWidthPx(string view) => view.ToLowerInvariant() switch
    {
        "day" => 260,
        "week" => 182,
        _ => 132,
    };

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

    /// <summary>Whether an item spanning [start, end] (inclusive, by date) occurs on <paramref name="day"/> -
    /// the range-containment form every multi-day rendering filter needs. Club Events already used this
    /// shape inline; Bookings were fixed to match here, since a same-day-only equality check silently
    /// dropped a multi-day booking after its first day.</summary>
    public static bool OccursOnDay(DateTime start, DateTime end, DateTime day) =>
        start.Date <= day.Date && day.Date <= end.Date;

    /// <summary>
    /// Whether a multi-day item's chip on <paramref name="day"/> should show a continuation mark:
    /// ShowsBefore when part of the item happened on an earlier day, ShowsAfter when more of it
    /// continues on a later day (both can be true on a middle day). Single-day items
    /// (Start.Date == End.Date) never show either. Shared by every Month/Week rendering spot (staff
    /// and public) so a multi-sheet, multi-day booking doesn't read as unrelated repeated chips.
    /// </summary>
    public static (bool ShowsBefore, bool ShowsAfter) ContinuationMarks(DateTime start, DateTime end, DateTime day) =>
        start.Date == end.Date
            ? (false, false)
            : (day.Date > start.Date, day.Date < end.Date);

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
