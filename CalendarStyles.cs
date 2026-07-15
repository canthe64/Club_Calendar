using FacilityScheduler.Domain;

namespace FacilityScheduler;

/// <summary>
/// The app's own color/border mapping for categories and hold-vs-confirmed state, independent
/// of Exchange's category colors (architecture doc S4.1 design rule). Blue=rental, green=league,
/// purple=event, orange=bonspiel, grey=maintenance, a distinct lighter neutral=other (deliberately
/// neutral rather than colorful, same as maintenance, but a different shade so the two remain
/// visually distinguishable). Dashed border = hold (not yet confirmed), solid = confirmed.
/// </summary>
public static class CalendarStyles
{
    public static string CategoryColor(BookingCategory category) => category switch
    {
        BookingCategory.Rental => "#2d5f8a",
        BookingCategory.League => "#4a8a5f",
        BookingCategory.Event => "#a05fa8",
        BookingCategory.Bonspiel => "#c2622f",
        BookingCategory.Maintenance => "#8a97a3",
        BookingCategory.Other => "#9c9690",
        _ => "#9c9690"
    };

    public static string CategoryLightBg(BookingCategory category) => category switch
    {
        BookingCategory.Rental => "#eaf1f8",
        BookingCategory.League => "#e8f3ec",
        BookingCategory.Event => "#f3e8f5",
        BookingCategory.Bonspiel => "#f7e8e0",
        BookingCategory.Maintenance => "#eef1f3",
        BookingCategory.Other => "#f1efec",
        _ => "#f1efec"
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
}
