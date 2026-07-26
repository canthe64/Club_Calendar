# FacilityScheduler API Reference

## Overview

This app exposes exactly **two** HTTP API endpoints, and both are public/anonymous. There is no
staff-facing REST/JSON API. Staff functionality (creating and managing bookings, club events) is
built entirely as Blazor Server components rendered over an authenticated SignalR circuit - staff
never call an HTTP API directly, and no such API exists to call (architecture doc §5.4, §6.2).

This is a deliberate architectural decision, confirmed by a live incident: at one point
`.AllowAnonymous()` was tried on the shared Razor Components registration to carve out a public
page, and it silently disabled authorization for *every* page in the app. The fix was to make the
rule absolute - any anonymous surface must be a plain Minimal API endpoint living entirely outside
the Blazor component tree, never a page inside it (`Program.cs:106-112`).

This document covers:

1. **[Public API](#public-api)** - the two real, callable HTTP endpoints. Anyone can call these
   without signing in.
2. **[Staff-facing surface](#staff-facing-surface)** - why there's no staff API, and how staff
   actions actually reach the server.
3. **[Appendix: internal service-layer contract](#appendix-internal-service-layer-contract)** - the
   C# methods the Blazor UI calls to perform staff operations. Not an HTTP API and not reachable
   from outside the process, but documented here for any developer extending the app or wiring in
   a future real staff API on top of the same service layer.

---

## Public API

Both endpoints are registered with `.AllowAnonymous()`, rate-limited via a shared fixed-window
limiter (`public-api`: 60 requests/minute per limiter instance, no queueing - excess requests get an
immediate `429`), and never touch or expose renter-identifying data, resource mailbox addresses, or
any booking category the club hasn't chosen to publish (architecture doc §5.4). Non-public categories
(League, Bonspiel, Maintenance, Practice Ice, Other) are excluded from the availability feed entirely;
the month calendar shows every category but strips nothing except the underlying mailbox address.

### `GET /api/public/availability`

Returns open-for-group-event time slots and upcoming club-wide events as JSON. Backs the CMS embed widget
(`wwwroot/embed/availability-widget.js`) - see [public-embed-instructions.md](public-embed-instructions.md).

- **Auth:** none (anonymous).
- **CORS:** `AllowAnyOrigin`, `GET` only (safe because the response carries no credentials/cookies
  and nothing sensitive - `Program.cs:48-54`).
- **Rate limit:** shared `public-api` limiter, 60 req/min, no queue.
- **Cache:** server-side, 60 seconds per `(start-date, days)` key. A repeated call within that window
  returns the same cached snapshot rather than re-querying Graph.

**Query parameters**

| Parameter | Type | Required | Default | Notes |
|---|---|---|---|---|
| `days` | integer | No | `30` | Clamped server-side to `[1, 60]` regardless of what's requested. |

**Response `200 OK`** — `application/json`

```json
{
  "generatedAtUtc": "2026-07-20T15:04:00Z",
  "sheetSlots": [
    { "sheetLabel": "Sheet 1", "start": "2026-07-22T18:00:00", "end": "2026-07-22T20:00:00" }
  ],
  "clubEvents": [
    {
      "title": "Summer Bonspiel",
      "category": "Bonspiel",
      "start": "2026-08-15T00:00:00",
      "end": "2026-08-17T00:00:00",
      "isAllDay": true,
      "marksSheetsUnavailable": true
    }
  ]
}
```

| Field | Type | Description |
|---|---|---|
| `generatedAtUtc` | datetime | When this response was computed (UTC). |
| `sheetSlots` | array | Open-for-group-event windows only - an existing Group Event-category booking still in **Hold** state. Confirmed group event bookings and every other category (League/Bonspiel/Maintenance/Practice Ice/Other) are never included here. |
| `sheetSlots[].sheetLabel` | string | Public-safe display name (e.g. `"Sheet 1"`) - never the underlying resource mailbox address. |
| `sheetSlots[].start` / `.end` | datetime | Local facility time (not UTC), ISO 8601, no offset. |
| `clubEvents` | array | Every club-wide event in the window, regardless of category. |
| `clubEvents[].category` | string | One of `Bonspiel`, `Activities`, `Closure`, `Other`. |
| `clubEvents[].marksSheetsUnavailable` | boolean | `true` when this event closes every sheet for its duration - the widget shows "all sheets reserved" wording specifically for these. |

A slot that overlaps a `marksSheetsUnavailable` club event is excluded from `sheetSlots` even if a
Group Event hold technically still exists on Graph, so the public feed never promises ice that's
actually closed.

**Errors:** a malformed `days` value (non-integer) is ignored and the default (`30`) is used - there
is no `400` response path on this endpoint today.

---

### `GET /public/calendar`

Returns a complete, self-contained HTML page (not JSON) - a browsable calendar for anonymous
visitors, with Month, Week, and Day views. Deliberately hand-built HTML with inline CSS/JS rather
than a Blazor page, for the reason in [Overview](#overview) - no Blazor circuit, no client runtime
to reject. A view or date-range change is a full page reload (server-rendered, no client-side
routing) - a "Loading…" overlay appears immediately on any nav-link click so that reload isn't
silent while the server computes the next view.

- **Auth:** none (anonymous).
- **CORS:** not applicable (same-origin page navigation/iframe embed, not a cross-origin fetch).
- **Rate limit:** shared `public-api` limiter, 60 req/min, no queue.
- **Cache:** server-side, 60 seconds per requested range (a distinct cache key per month/week/day
  ever viewed within the clamped window below - each entry still expires after 60s regardless).

**Query parameters**

| Parameter | Type | Required | Default | Notes |
|---|---|---|---|---|
| `view` | `month` \| `week` \| `day` | No | `month` | Selects which grid renders. Omitting it (or any unrecognized value) falls back to Month, so existing links/embeds using only `?month=` keep working unchanged. |
| `month` | string, `yyyy-MM` | No (Month view only) | current month | Clamped server-side to a window from one year ago through two years ahead. An unparseable value falls back to the current month rather than erroring. |
| `date` | string, `yyyy-MM-dd` | No (Week/Day views only) | today | Same clamping as `month`. For Week, the grid shows the 7-day week containing this date (starting Sunday); for Day, this exact date. |

**Response `200 OK`** — `text/html; charset=utf-8`

**Month view:** a 7-column month grid with color-coded entry chips (confirmed booking, hold, club
event - club event chips get a dotted border, matching the staff calendar's visual language for
distinguishing them from sheet bookings). Every day shows up to 3 entries with a "+N more" expander
for busier days.

**Week view** (7-day) **and Day view** (single day): hourly grids sharing the same hour-axis math
and lane-layout algorithm as the staff Week/Day grids (`CalendarStyles.LayoutLanes`) - genuinely
concurrent items render side-by-side instead of overlapping. All-day club events pin to a row at
the top of their column; timed club events and bookings position at their actual hour. A
multi-sheet booking that produced identical entries (same title/time/category/state) collapses to
one displayed item via record equality, the same de-facto dedup Month view already relied on -
these DTOs never carry sheet/group identity to dedupe by more precisely.

A Month/Week/Day toggle plus Prev/Today/Next navigation appears in the header on every view;
switching views preserves your place (e.g. Week → Month lands on the month containing that week's
first day).

Every entry chip's visible text is its start time followed by its title (e.g. "7PM - League
Practice") - renter name if present, else the category name; the one exception is a confirmed
booking's renter name, which staff practice keeps private rather than the page stripping it.
Clicking a chip reveals the full category + hold/confirmed state and exact date/time. No phone
numbers, emails, notes, or resource mailbox addresses ever appear.

Intended for direct browsing or `<iframe>` embedding on the club's own site. Note: this page has no
`Content-Security-Policy: frame-ancestors` restriction today, so any site can iframe it, not just the
club's own - a known, deliberately deferred hardening item (see
[public-embed-instructions.md](public-embed-instructions.md)).

---

## Staff-facing surface

There is no staff API to call. Staff sign in via Entra ID (`Program.cs:56-62`) and interact entirely
through Blazor Server pages - every page except the two public endpoints above requires
authentication by default (`FallbackPolicy = RequireAuthenticatedUser()`, `Program.cs:64-71`).
Guest-vs-member-vs-any-authenticated-user access is controlled entirely by the Entra Enterprise
Application's "Assignment required?" setting, not by any code in this app (see the deployment guide's
§1.2 for how to restrict it).

Because everything runs over one shared, authenticated SignalR circuit, there's no meaningful sense
in which a staff "request" has its own URL, verb, or independent auth check the way the two public
endpoints do - a page load establishes the circuit, and every subsequent staff action (create a
booking, cancel a series, etc.) is a method call within that same already-authenticated circuit, not
a new HTTP request.

If a real staff-facing API is ever needed (e.g. for a future mobile app or third-party integration),
it would need to be built as its own set of Minimal API endpoints - following the same pattern as the
public ones, but with `RequireAuthorization()` instead of `.AllowAnonymous()` - calling into the same
service layer documented below rather than duplicating its logic.

---

## Appendix: internal service-layer contract

Not an HTTP API - these are C# methods on singleton services, called directly by Blazor components
within the authenticated circuit. Documented here as the closest thing to a "staff API contract" in
this codebase, and as the reuse surface for any future real staff API.

### `SheetBookingService`

Owns conflict enforcement for sheet bookings. Direct Graph writes bypass the Resource Booking
Attendant, so this service is the only thing preventing two overlapping bookings on the same sheet.

| Method | Signature | Behavior |
|---|---|---|
| `CreateHoldAsync` | `Task<BookingResult> CreateHoldAsync(SheetBooking booking)` | Creates a single-sheet booking in `Hold` state. Conflict-checked against that sheet's existing events; returns `BookingResult.Conflict` (no write) if anything overlaps. |
| `CreateConfirmedAsync` | `Task<BookingResult> CreateConfirmedAsync(SheetBooking booking)` | Same as above, in `Confirmed` state. |
| `CreateAcrossSheetsAsync` | `Task<GroupBookingResult> CreateAcrossSheetsAsync(IEnumerable<string> sheetMailboxes, SheetBooking template)` | Creates the same conceptual booking on multiple sheets at once, sharing one `BookingGroupId`. All-or-nothing: any conflict on any sheet aborts the whole request and reports every conflict found. |
| `ConfirmAsync` | `Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId)` | Flips a single event from Hold to Confirmed (`ShowAs: Busy`). |
| `CancelAsync` | `Task CancelAsync(string sheetMailbox, string eventId)` | Hard-deletes a single event. |
| `UpdateGroupAsync` | `Task<GroupBookingResult> UpdateGroupAsync(IEnumerable<SheetBooking> members, SheetBooking updatedFields, Guid? newBookingGroupId = null)` | Updates every event in a booking group (time, category, renter/contact/notes, hold/confirmed state). Re-checks conflicts against the new time before writing (excluding the group's own events); all-or-nothing. `newBookingGroupId` splits an edited subset off into its own group when only some sheets in the original group were touched. |
| `CancelGroupAsync` | `Task CancelGroupAsync(IEnumerable<SheetBooking> members, bool reopenAsGroupEventHold)` | Cancels every event in a group. `reopenAsGroupEventHold: true` converts each slot back to an unclaimed open Group Event hold (publicly bookable again) instead of deleting it. |
| `PreviewSeriesConflictsAsync` | `Task<Dictionary<DateTime, List<SheetBooking>>> PreviewSeriesConflictsAsync(IEnumerable<string> sheetMailboxes, IReadOnlyCollection<DateTime> candidateDates, TimeSpan startTime, TimeSpan endTime)` | Informational only - reports conflicts per candidate date so staff can choose to skip that date. Never blocks anything itself. |
| `CreateSeriesAsync` | `Task<List<SheetBooking>> CreateSeriesAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, DateTime lastOccurrenceDate, IEnumerable<DateTime> excludedDates)` | Creates one native weekly-recurring Graph event per sheet (sharing a `BookingGroupId`), then deletes the specific `excludedDates` occurrences staff chose to skip during review. Does not conflict-check - that's `PreviewSeriesConflictsAsync`'s job, expected to already have run. |
| `CancelSeriesAsync` | `Task CancelSeriesAsync(IEnumerable<SheetBooking> members)` | Deletes an entire recurring series (all occurrences) for every sheet in the group - the "backdoor" for correcting a data-entry mistake at series creation, not a primary UX path. |
| `GetBookingsAsync` | `Task<List<SheetBooking>> GetBookingsAsync(string sheetMailbox, DateTime start, DateTime end)` | Reads one sheet's bookings in a window. Always live (never cached) - used by every conflict check. |
| `GetBookingsForAllSheetsAsync` | `Task<List<SheetBooking>> GetBookingsForAllSheetsAsync(DateTime start, DateTime end)` | Reads every configured sheet's bookings in a window, in parallel. View-rendering read path only (Calendar page, public availability) - cached for 30 seconds, invalidated on every write. |

### `ClubEventService`

Owns the single dedicated Club Events mailbox. Simpler than `SheetBookingService` by design: one
low-volume mailbox, no per-sheet locking, no `BookingGroupId` concept, and no conflict checking at
all (neither against sheet bookings nor between club events).

| Method | Signature | Behavior |
|---|---|---|
| `CreateAsync` | `Task<ClubEvent> CreateAsync(ClubEvent clubEvent)` | Creates a club event. No conflict check. |
| `UpdateAsync` | `Task UpdateAsync(ClubEvent clubEvent)` | Updates an existing club event by `EventId`. |
| `CancelAsync` | `Task CancelAsync(string eventId)` | Hard-deletes a club event. |
| `GetEventsAsync` | `Task<List<ClubEvent>> GetEventsAsync(DateTime start, DateTime end)` | Reads club events in a window. Cached for 30 seconds, invalidated on every write. |

### Shared domain shapes

| Type | Shape | Notes |
|---|---|---|
| `SheetBooking` | `EventId`, `ICalUId`, `SheetMailbox`, `Start`, `End`, `Category` (`BookingCategory`), `State` (`BookingState`), `RenterName`, `RenterPhone`, `RenterEmail`, `Notes`, `BookedBy`, `BookingGroupId` (Guid), `SeriesMasterId` | `BookingGroupId` links every sheet's event belonging to one conceptual booking, even single-sheet ones. `SeriesMasterId` is set only on occurrences of a recurring series. |
| `ClubEvent` | `EventId`, `ICalUId`, `Title`, `Category` (`ClubEventCategory`), `Start`, `End`, `IsAllDay`, `MarksSheetsUnavailable`, `Notes`, `BookedBy` | Not tied to any sheet. |
| `BookingCategory` | `GroupEvent`, `League`, `Event`, `Bonspiel`, `Maintenance`, `PracticeIce`, `Other` | Display labels ("Group Event", "Practice Ice") are kept separate from these wire values via `CalendarStyles.CategoryLabel` - the values above are what's actually round-tripped through Graph's `categories` property. |
| `BookingState` | `Hold`, `Confirmed` | |
| `ClubEventCategory` | `Bonspiel`, `Activities`, `Closure`, `Other` | |
| `BookingResult` | `IsSuccess`, `Booking?`, `Conflicts: List<SheetBooking>` | Result of a single-sheet create. |
| `GroupBookingResult` | `IsSuccess`, `Bookings: List<SheetBooking>`, `Conflicts: List<SheetBooking>` | Result of a multi-sheet create/update. |

### `FacilityConfiguration`

Singleton exposing the tenant's runtime configuration to every service above: `SheetMailboxes`
(string array), `ClubEventsMailbox`, `TimeZone`, `ZoneInfo`, `Name`, `LogoPath`, plus
`ToUtcQueryString(DateTime)`/`FromUtcResponseString(string)` helpers for Graph's UTC query-parameter
and response conventions. Constructed eagerly at startup (`Program.cs:78-81`) so a misconfigured
deployment fails immediately rather than on first request.
