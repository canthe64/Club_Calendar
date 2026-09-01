namespace FacilityScheduler.Domain;

public enum BookingCategory
{
    GroupEvent,
    League,
    Event,
    Bonspiel,
    Maintenance,
    PracticeIce,
    // Inserted before Other, not appended after it - CalendarStyles.SheetCategories (the on-ice
    // category picker/filter row) derives its display order directly from this declaration order,
    // and Other is meant to read as the trailing catch-all everywhere that list is used.
    LearnToCurl,
    Other
}
