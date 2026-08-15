namespace FacilityScheduler.Services;

/// <summary>
/// Practice ice slot-selection rules shared between the availability computation
/// (PublicAvailabilityService) and the request page's duration options - kept in one place so
/// neither side can silently drift from the other (e.g. offering a duration the browse view
/// wouldn't have surfaced a window for).
/// </summary>
public static class PracticeIceRules
{
    public const int SlotIntervalMinutes = 30;

    /// <summary>Shortest session worth surfacing as a candidate window or duration option.
    /// Sessions are typically 1-2 hours (docs/practice-ice-hosting-design.md §2) - this floor keeps
    /// the browse view from listing 30-minute slivers nobody would actually host the whole club's
    /// practice ice in.</summary>
    public const int MinSessionMinutes = 60;

    /// <summary>Cap on the member-supplied notes field. Nothing else bounded it, and it lands in the
    /// booking's JSON extended property alongside the host's name and email - the only size the
    /// original Graph spike established as safe is 4000 characters for a single extended property
    /// value (architecture doc §8), and exceeding it would fail the Graph write partway through a
    /// five-sheet create. Generous for "first time hosting, bringing spare brooms" while leaving
    /// ample headroom in the blob.</summary>
    public const int MaxNotesLength = 1000;

    /// <summary>Every selectable duration (in minutes), from MinSessionMinutes up to
    /// <paramref name="maxMinutes"/> in SlotIntervalMinutes steps - empty if even the shortest
    /// session doesn't fit.</summary>
    public static IEnumerable<int> DurationOptionsMinutes(int maxMinutes)
    {
        for (var minutes = MinSessionMinutes; minutes <= maxMinutes; minutes += SlotIntervalMinutes)
        {
            yield return minutes;
        }
    }
}
