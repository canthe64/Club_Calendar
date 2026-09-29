using FacilityScheduler.Domain;

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

    /// <summary>An open Group Event hold - the one kind of booking member-hosted ice (practice ice,
    /// make-up games) may take over, and only inside FacilityConfiguration.GroupEventHoldReleaseCutoff.
    /// Shared by the availability read (PublicAvailabilityService) and the write
    /// (SheetBookingService.CreateTakingReleasedHoldsAsync) so the two can't disagree.</summary>
    public static bool IsReleasableHold(SheetBooking b) =>
        b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold;

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

/// <summary>Make-up game slot rules (staff request 2026-09-28). Start times, lead time, horizon,
/// and eligible hours are practice ice's (PracticeIceRules/PracticeIceOptions).</summary>
public static class MakeUpGameRules
{
    /// <summary>Every make-up game is two hours.</summary>
    public const int DurationMinutes = 120;

    /// <summary>The title when there's no usable requester name.</summary>
    public const string Title = "Make-Up Game";

    /// <summary>The booking title, shown on both calendars: "Make-Up Game Requested by {name}"
    /// (operator decision 2026-09-28 - the requester is named publicly). Never a bare email/UPN,
    /// which is what a sign-in without a display-name claim can fall back to - same guard as
    /// practice ice's host name (D69).</summary>
    public static string BookingTitle(string? requesterName) =>
        string.IsNullOrWhiteSpace(requesterName) || requesterName.Contains('@')
            ? Title
            : $"{Title} Requested by {requesterName.Trim()}";
}
