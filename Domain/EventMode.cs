namespace FacilityScheduler.Domain;

/// <summary>Whether an event happens on the ice (a per-sheet booking) or off it (an event on the
/// dedicated club-events mailbox). This is the distinction staff actually think in; which mailbox
/// each lands on is an implementation detail the UI no longer surfaces (architecture doc §4.4).
///
/// Public rather than internal because it appears on a public component [Parameter].</summary>
public enum EventMode
{
    OnIce,
    OffIce
}
