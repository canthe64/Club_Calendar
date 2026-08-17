namespace FacilityScheduler.Domain;

/// <summary>
/// Member NAMES are load-bearing in two places at once: they are the wire value
/// /api/public/availability publishes for `clubEvents[].category` (PublicJsonOptions, D79), and the
/// literal string written to and parsed back from the Graph event's Categories collection
/// (ClubEventService) - where a mismatch degrades silently to Other rather than erroring. Renaming a
/// member is therefore a breaking change on both fronts, and needs the Exchange master category in
/// docs/provision-categories.ps1 renamed to match. Ordinals are not published, so declaration order
/// is free; display order lives in CalendarStyles.ClubEventCategories regardless.
/// </summary>
public enum ClubEventCategory
{
    Bonspiel,
    Activities,
    Closure,
    Other,
    Meetings
}
