using FacilityScheduler.Domain;
using Microsoft.Graph.Models;

namespace FacilityScheduler.Services;

// ── External booking source support (e.g. a booking-platform webhook) ─────────────────────────
// Split out of SheetBookingService.cs as a partial class (code review O8) - the file had grown to
// 1,505 lines carrying two distinct concerns, and this boundary (the comment banner these methods
// sat behind) was already the recognized seam between them. Zero behavior change: same type, same
// DI registration, same primary-constructor fields (graph/cache/facility/log/viewCache/window),
// just a second file. These three public methods exist for one caller: an external booking
// notification that already happened in the real world and must be reflected here, not
// re-validated against it. Unlike everything in SheetBookingService.cs, a hold on the same sheet
// is treated as claimable rather than a conflict - that's exactly what an open hold means - and a
// booking that doesn't match any known availability is still written rather than dropped (see
// ForceCreateConfirmedAsync).
public partial class SheetBookingService
{
    /// <summary>
    /// Finds the booking (on whichever sheet it currently lives on, at whatever time it's currently
    /// scheduled) tagged with this external booking id, or null if none exists yet. Used to upsert
    /// on repeat/rescheduled notifications for the same external booking - there is no companion
    /// database (architecture doc D7) to look this up in, so it's a live Graph query, one per
    /// configured sheet in the worst case (fine at this app's volume).
    /// </summary>
    public async Task<SheetBooking?> FindByExternalIdAsync(string externalBookingId, CancellationToken ct = default)
    {
        if (!ExternalIdPattern.IsMatch(externalBookingId))
        {
            throw new ArgumentException("externalBookingId contains characters unsafe for a Graph $filter query.", nameof(externalBookingId));
        }

        foreach (var sheet in facility.SheetMailboxes)
        {
            var matches = await graph.FindEventsAsync(sheet,
                $"singleValueExtendedProperties/Any(ep: ep/id eq '{ExternalIdPropertyId}' and ep/value eq '{externalBookingId}')",
                ExtendedPropertiesExpand, ct);

            if (matches is { Count: > 0 })
            {
                // Defense in depth: this app's own writers always keep at most one live match per
                // external id (ToGraphEvent's clearUnsetOptionalProperties clears it on reopen), but
                // if that invariant is ever violated - or a stray match exists from data written
                // outside the app - prefer a Confirmed booking over a Hold, since a released hold
                // wrongly carrying a stale id is exactly the scenario that invariant guards against.
                var booked = matches.Select(e => FromGraphEvent(sheet, e)).ToList();
                return booked.FirstOrDefault(b => b.State == BookingState.Confirmed) ?? booked[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Claims an open Group Event hold for an externally-sourced booking - tries sheets in
    /// <see cref="FacilityConfiguration.SheetMailboxes"/> order (Sheet 1 first) and claims the
    /// first one whose open hold(s) fully cover [start, end). The claimed hold is trimmed to
    /// reflect the remaining open time (deleted if nothing remains, patched if one segment
    /// remains, split into two events if the claim was in the middle of it) - a hold that's an
    /// occurrence of a recurring series has that occurrence deleted and standalone events created
    /// for any remainder instead, since Graph rejects a time-bearing PATCH on a recurring
    /// occurrence even when the time is technically unchanged. Returns null if no sheet's hold(s)
    /// fully cover the window; the caller decides how to handle that (see ForceCreateConfirmedAsync).
    /// <paramref name="groupId"/> is set explicitly by the caller (BreelyBookingProcessor) rather
    /// than minted here, so multiple sheets claimed from the same Breely submission - or a sheet
    /// reclaimed on reschedule - can share (or keep) one BookingGroupId instead of each claim getting
    /// its own random one.
    /// </summary>
    public async Task<SheetBooking?> ClaimHoldAsync(DateTime start, DateTime end, SheetBooking template, Guid groupId, CancellationToken ct = default)
    {
        foreach (var sheet in facility.SheetMailboxes)
        {
            var sem = SheetLocks.GetOrAdd(sheet, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                var events = await GetEventsInRangeAsync(sheet, start, end, ct);
                var covering = events
                    .Select(e => FromGraphEvent(sheet, e))
                    .Where(b => b.Category == BookingCategory.GroupEvent && b.State == BookingState.Hold)
                    .Where(b => b.Start <= start && b.End >= end)
                    .ToList();

                if (covering.Count == 0)
                {
                    continue; // no covering hold on this sheet - try the next
                }

                var hold = covering[0];

                var booking = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = start,
                    End = end,
                    Category = template.Category,
                    State = BookingState.Confirmed,
                    RenterName = template.RenterName,
                    RenterPhone = template.RenterPhone,
                    RenterEmail = template.RenterEmail,
                    Notes = template.Notes,
                    BookedBy = template.BookedBy,
                    ExternalBookingId = template.ExternalBookingId,
                    BookingGroupId = groupId
                };

                var graphEvent = ToGraphEvent(booking);
                var created = await graph.CreateEventAsync(sheet, graphEvent, ct);
                booking.EventId = created?.Id;
                booking.ICalUId = created?.ICalUId;

                await TrimHoldAsync(sheet, hold, start, end, ct);

                InvalidateViewCache();
                return booking;
            }
            finally
            {
                sem.Release();
            }
        }

        return null;
    }

    private async Task TrimHoldAsync(string sheet, SheetBooking hold, DateTime claimedStart, DateTime claimedEnd, CancellationToken ct)
    {
        var minInterval = TimeSpan.FromMinutes(_minimumGroupEventBookingIntervalMinutes);

        // A leftover fragment shorter than the configured minimum (Settings page) is dropped rather
        // than offered as its own bookable slot - nobody can realistically use a 5-minute sliver of
        // ice. Filtering here means every branch below (delete-if-none/patch-if-one/split-if-two)
        // just works on whatever's left, unchanged.
        var remainders = CalendarStyles.SubtractIntervals(hold.Start, hold.End, [(claimedStart, claimedEnd)])
            .Where(r => r.End - r.Start >= minInterval)
            .ToList();

        if (hold.SeriesMasterId is not null)
        {
            // Delete this occurrence and create standalone events for whatever remains, rather than
            // trying to PATCH it in place - Graph rejects a time-bearing PATCH on a recurring
            // occurrence with "Modified occurrence is crossing or overlapping adjacent occurrence"
            // even when the resulting time doesn't actually conflict with anything.
            if (hold.EventId is not null)
            {
                await graph.DeleteEventAsync(sheet, hold.EventId, ct);
            }

            foreach (var (segStart, segEnd) in remainders)
            {
                var remainderHold = new SheetBooking
                {
                    SheetMailbox = sheet,
                    Start = segStart,
                    End = segEnd,
                    Category = BookingCategory.GroupEvent,
                    State = BookingState.Hold,
                    BookingGroupId = Guid.NewGuid()
                };
                await graph.CreateEventAsync(sheet, ToGraphEvent(remainderHold, titleOverride: AvailableForGroupEventsTitle), ct);
            }

            return;
        }

        if (remainders.Count == 0)
        {
            if (hold.EventId is not null)
            {
                await graph.DeleteEventAsync(sheet, hold.EventId, ct);
            }
            return;
        }

        // One remainder: patch the existing hold in place to the shrunken window.
        var (firstStart, firstEnd) = remainders[0];
        var patch = new Event
        {
            Subject = AvailableForGroupEventsTitle,
            Start = new DateTimeTimeZone { DateTime = firstStart.ToString("s"), TimeZone = facility.TimeZone },
            End = new DateTimeTimeZone { DateTime = firstEnd.ToString("s"), TimeZone = facility.TimeZone }
        };
        await graph.PatchEventAsync(sheet, hold.EventId!, patch, ct);

        if (remainders.Count < 2)
        {
            return;
        }

        // Two remainders (the claim was in the middle of the hold): the patch above covers the
        // first fragment; create a new event for the second.
        var (secondStart, secondEnd) = remainders[1];
        var secondHold = new SheetBooking
        {
            SheetMailbox = sheet,
            Start = secondStart,
            End = secondEnd,
            Category = BookingCategory.GroupEvent,
            State = BookingState.Hold,
            BookingGroupId = Guid.NewGuid()
        };
        await graph.CreateEventAsync(sheet, ToGraphEvent(secondHold, titleOverride: AvailableForGroupEventsTitle), ct);
    }

    /// <summary>
    /// Writes a Confirmed booking directly, bypassing the conflict check entirely - the last-resort
    /// fallback when an externally-sourced booking doesn't match any advertised open hold on any
    /// sheet. Graph itself never enforces non-overlap (D3) - this app's own conflict check is the
    /// only thing that normally prevents an overlap, and this method deliberately steps around it
    /// because the booking already happened in the real world regardless of what this calendar
    /// currently shows; never dropping a real booking matters more than keeping the calendar tidy.
    /// The caller is responsible for flagging this for staff review - this method never runs silently.
    /// </summary>
    public async Task<SheetBooking> ForceCreateConfirmedAsync(string sheetMailbox, SheetBooking booking, CancellationToken ct = default)
    {
        var sem = SheetLocks.GetOrAdd(sheetMailbox, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            booking.SheetMailbox = sheetMailbox;
            booking.State = BookingState.Confirmed;
            if (booking.BookingGroupId == Guid.Empty)
            {
                booking.BookingGroupId = Guid.NewGuid();
            }

            var graphEvent = ToGraphEvent(booking);
            var created = await graph.CreateEventAsync(sheetMailbox, graphEvent, ct);
            booking.EventId = created?.Id;
            booking.ICalUId = created?.ICalUId;

            InvalidateViewCache();
            return booking;
        }
        finally
        {
            sem.Release();
        }
    }
}
