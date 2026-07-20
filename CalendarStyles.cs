using FacilityScheduler.Domain;

namespace FacilityScheduler;

/// <summary>
/// The app's own color/border mapping for categories and hold-vs-confirmed state, independent
/// of Exchange's category colors (architecture doc S4.1 design rule). Blue=group event, green=league,
/// purple=event, orange=bonspiel, grey=maintenance, teal=practice ice, a distinct lighter neutral=other
/// (deliberately neutral rather than colorful, same as maintenance, but a different shade so the two
/// remain visually distinguishable). Dashed border = hold (not yet confirmed), solid = confirmed.
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
        BookingCategory.PracticeIce => "#1e8a8a",
        BookingCategory.Other => "#9c9690",
        _ => "#9c9690"
    };

    public static string CategoryLightBg(BookingCategory category) => category switch
    {
        BookingCategory.GroupEvent => "#eaf1f8",
        BookingCategory.League => "#e8f3ec",
        BookingCategory.Event => "#f3e8f5",
        BookingCategory.Bonspiel => "#f7e8e0",
        BookingCategory.Maintenance => "#eef1f3",
        BookingCategory.PracticeIce => "#e3f3f2",
        BookingCategory.Other => "#f1efec",
        _ => "#f1efec"
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
    public const double RowHeightPx = 34;
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
}
