# FacilityScheduler API Reference

## Overview

This app exposes **six** HTTP API endpoints. Staff functionality (creating and managing bookings,
club events) is still built almost entirely as Blazor Server components rendered over an
authenticated SignalR circuit, not called via HTTP - but as of Phase 10 that's no longer
*absolute*: one endpoint below exists specifically because a file download doesn't fit the SignalR
circuit (architecture doc §5.6).

Five of the six are anonymous. Four of those five are read-only (three customer/member-facing, plus
a staff-viewable diagnostic listener). The fifth anonymous endpoint, added in Phase 10, is the app's
only anonymous **write** surface: a webhook that ingests booking notifications from the Breely
booking platform (architecture doc §4.8/§5.5). It's a deliberate, bounded exception to "public
surfaces are read-only," not a broadening of the general rule - see
[`POST /api/webhooks/breely`](#post-apiwebhooksbreely).

The sixth, also added in Phase 10, is the one **staff-authenticated** HTTP endpoint in the app: a
log-file download (architecture doc §5.6) - see
[`GET /settings/logs/download`](#get-settingslogsdownload). Every other staff action still happens
as a method call inside the authenticated Blazor circuit, not a distinct HTTP request; this one
endpoint exists only because Blazor Server's SignalR circuit isn't a good way to stream a file
download, the same reasoning that keeps the anonymous endpoints below out of the Blazor component
tree in the first place.

This is otherwise a deliberate architectural decision, confirmed by a live incident: at one point
`.AllowAnonymous()` was tried on the shared Razor Components registration to carve out a public
page, and it silently disabled authorization for *every* page in the app. The fix was to make the
rule absolute - any anonymous surface must be a plain Minimal API endpoint living entirely outside
the Blazor component tree, never a page inside it (`Program.cs:106-112`).

This document covers:

1. **[Public API](#public-api)** - the six real, callable HTTP endpoints. Five need no sign-in
   (the webhook additionally requires a shared secret); one requires staff sign-in.
2. **[Staff-facing surface](#staff-facing-surface)** - why there's (almost) no staff API, and how
   staff actions actually reach the server.
3. **[Appendix: internal service-layer contract](#appendix-internal-service-layer-contract)** - the
   C# methods the Blazor UI calls to perform staff operations. Not an HTTP API and not reachable
   from outside the process, but documented here for any developer extending the app or wiring in
   a future real staff API on top of the same service layer.

---

## Public API

The three read-only member/customer-facing endpoints below are all registered with
`.AllowAnonymous()`, rate-limited via a shared fixed-window limiter (`public-api`: 60
requests/minute per limiter instance, no queueing - excess requests get an immediate `429`), and
never touch or expose renter-identifying data, resource mailbox addresses, or any booking category
the club hasn't chosen to publish (architecture doc §5.4). Non-public categories (League, Bonspiel,
Maintenance, Practice Ice, Other) are excluded from the availability feed entirely; the month
calendar shows every category but strips nothing except the underlying mailbox address.

The booking webhook and the diagnostic capture listener (documented after the three read endpoints
below) are also `.AllowAnonymous()`, but sit on their own separate `booking-webhook` rate limiter
and carry their own secret-based auth on top of route isolation - see each endpoint's own section.

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
actually closed. A hold's own advertised start/end is also never trusted blindly: any portion that
overlaps *another* booking on the same sheet (of any category/state) is subtracted out before being
reported, splitting the hold into 0, 1, or 2 remaining open sub-ranges as needed. The app's own
write-path conflict check should prevent that overlap from ever being created through the app, but
data written outside it (direct Graph/Outlook writes, seeded test data) isn't protected by that
invariant, and the public feed must never promise ice that's actually occupied regardless of how
the conflicting data got there - live-found 2026-07-28 via a hold that fully covered a separately
booked, confirmed League game on the same sheet.

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
Practice") - renter name if present, else the category name; for a staff-typed title, staff practice
keeps a renter's identity private rather than the page stripping it programmatically. **One
exception (fixed 2026-08-04):** a Breely-originated booking's title is always the category label,
never the customer's real name - `RenterName` there is populated automatically from Breely's own
data with no staff opportunity to redact it first, unlike a title staff type themselves. Clicking a
chip reveals the full category + hold/confirmed state and exact date/time. No phone numbers, emails,
notes, or resource mailbox addresses ever appear.

Intended for direct browsing or `<iframe>` embedding on the club's own site. Note: this is the **one
route in the whole app** that doesn't send `X-Frame-Options`/a `frame-ancestors` CSP (added
2026-08-04 to every other route, architecture doc §6.4) - so any site can iframe this specific page,
not just the club's own; a known, deliberately deferred hardening item (see
[public-embed-instructions.md](public-embed-instructions.md)).

---

### `GET /public/search`

Returns a complete, self-contained HTML page (not JSON) - a form plus results, letting an anonymous
visitor search for a date/time window with at least N sheets simultaneously open for a group event.
Delivers the "≥N sheets available" view the architecture doc originally scoped as R6 and marked
backlogged during the initial build; built as its own page rather than folded into `/public/calendar`.
Same hand-built-HTML approach and rationale as the other two endpoints.

- **Auth:** none (anonymous).
- **CORS:** not applicable (page navigation, not a cross-origin fetch).
- **Rate limit:** shared `public-api` limiter, 60 req/min, no queue.
- **Cache:** server-side, 60 seconds per `(start, end, sheets)` combination.

**Query parameters**

| Parameter | Type | Required | Default | Notes |
|---|---|---|---|---|
| `start` | string, `yyyy-MM-dd` | No | today | Clamped to a window from one year ago through two years ahead, same rationale as `/public/calendar`'s `?date=`. |
| `end` | string, `yyyy-MM-dd` | No | `start` + 7 days | Clamped to at most 60 days after `start` (the search's own span cap - a search this wide across every sheet is a meaningfully larger Graph fan-out than a single month/week/day view) and to the same outer ±1yr/+2yr window. |
| `sheets` | integer | No | `1` | Minimum number of sheets that must be simultaneously open. Clamped to `[1, N]` where N is the tenant's actual configured sheet count (`FacilityConfiguration.SheetMailboxes.Length`) - never hardcoded, per the app's configuration-driven design (architecture doc §4.6, D17). |

**Response `200 OK`** — `text/html; charset=utf-8`

A form (From date, To date, minimum-sheets dropdown) followed by a list of matching windows, each
rendered as a start-time-prefixed date/time range (e.g. "Monday, Aug 3 · 10:00AM-11:00AM") linking
to that day's `/public/calendar?view=day` for full detail. Computed via a two-pass interval
algorithm: each sheet's own open (Group Event + Hold) slots are first merged into that sheet's
maximal contiguous blocks, then a sweep across every sheet's blocks counts how many are open at each
point in time; contiguous stretches meeting the requested minimum are reported as one window. Uses
the same per-sheet-overlap-subtraction and closure-exclusion rules as `/api/public/availability`
(`PublicAvailabilityService.GetOpenSlotsAsync`) - a window is never reported as available if another
booking or a `marksSheetsUnavailable` club event actually occupies part of it. Cross-linked with
`/public/calendar` (a link each way in the header).

---

### `POST /api/webhooks/breely`

The app's only anonymous **write** endpoint - ingests booking notifications from the Breely
third-party booking platform and reflects them onto the matching sheet's calendar (architecture doc
§4.8/§5.5). Breely is the source of truth for what a customer was actually promised; this app's copy
is a best-effort, one-way reflection kept current for staff's benefit, not relied on as authoritative.

- **Auth:** static shared-secret header, `X-Webhook-Secret`, compared against configuration
  (`Webhook:BreelySharedSecret`) using a constant-time comparison. Missing/unconfigured secret or a
  mismatch → `401 Unauthorized`. Not HMAC-signed - Breely's own webhook configuration has no
  capability to compute a per-request signature (confirmed empirically, not from documentation).
- **CORS:** not applicable (server-to-server POST, not a browser fetch).
- **Rate limit:** its own `booking-webhook` limiter, 30 req/min, no queue - deliberately separate
  from `public-api` so a flood aimed at one can't starve the other.

**Request body** — `application/json`, `{ "event": { ... }, "submission": { "events": [ ... ] } }`.
Only a subset of Breely's real (much larger) payload is read:

| Field | Type | Notes |
|---|---|---|
| `event.id` | integer | Stable identity across reschedules; stored as `breely:{id}` in the `ExternalBookingId` extended property for upsert matching. |
| `event.start_date` / `event.start_time` | string | e.g. `"Sep 25, 2026"` / `"9:00am"`. Parsed as facility-local time; any timezone abbreviation Breely also sends is ignored. |
| `event.duration_in_minutes` | integer | Combined with the above to compute the booking's end time. |
| `event.booked_with` | string | Must equal the configured sheet resource-type name (currently `"Curling Sheet"`) or the event is ignored as not applicable to this calendar. |
| `event.canceled` | boolean | `true` releases the matching booking back to an open Group Event hold. |
| `event.client_full_name` / `client_email` / `client_phone` | string | Stored on the booking as renter contact info. |
| `event.event_type` / `admin_url` | string | Stored in the booking's notes for staff reference back to Breely's own admin view. |
| `submission.events[]` | array | Same shape as `event` above, one entry per sheet in the *original* multi-sheet reservation this notification belongs to (architecture doc §4.8). Present on every call, including later reschedule/cancellation calls for a single sibling - on those, it's a **stale snapshot from the original creation call**, not updated data; the top-level `event` object always wins for its own id. Added 2026-08-03 after a real 3-sheet booking only claimed 1 sheet - this array is the only place the sibling ids are discoverable at all. |

Everything else in the real payload (CRM/marketing fields, signed-PDF blobs, raw form-answer
dumps) is silently ignored - `System.Text.Json` drops unmapped properties.

**Multi-sheet handling.** The endpoint resolves every id named by either `event` or
`submission.events[]` (deduplicated, `event`'s own data winning for its own id) and processes each
independently - each may create, reschedule, or cancel a booking depending on its own `canceled`
flag and whether it's already been claimed before. All sheets resolved from one call that don't
already belong to an existing group share one freshly-minted `BookingGroupId`; a sibling that's
already been claimed (a reschedule, or a straggler picked up on a later call) reuses its existing
group id instead. One event failing doesn't stop the others from being processed. A sibling id
resolved only from `submission.events[]` (as opposed to the top-level `event`) can only ever
*create* a never-before-seen booking - it can never reschedule or cancel one that's already claimed,
since that array can be a stale snapshot from the original creation call (fixed 2026-08-04).

**Response:** always `200 OK` once past the secret check, returned **immediately, before processing
runs** (changed 2026-08-04 - previously awaited processing first, which risked an HTTP-timeout abort
mid-write on a large multi-sheet batch; see architecture doc §4.8). A malformed JSON body, an
unrecognized shape, or an internal processing exception never surfaces as anything other than `200`
either way - a deliberate "dumb webhook" design (§4.8): the booking already happened in the real
world, so this endpoint never rejects or signals failure back to the sender. Failures are surfaced
via server logs and, for an unmatched booking, a non-blocking `⚠ Web booking needs review` Club
Event marker for staff to reassign manually.

---

### `GET /settings/logs/download`

The one staff-facing HTTP endpoint in the app (architecture doc §5.6) - built as a plain Minimal API
endpoint rather than a Blazor page for the same "raw HTTP semantics don't fit the SignalR circuit"
reason the anonymous endpoints above are, just gated by sign-in instead of `.AllowAnonymous()`.
Backs the **Settings** page's (`/settings`, architecture doc §4.9) "Download full log archive" link.

- **Auth:** staff sign-in required - covered by the app's default authenticated fallback policy
  (`Program.cs:64-71`), same as every other page; no separate check needed since this route was
  never marked anonymous.
- **Response `200 OK`** - `application/zip`, containing every rotated log file in `AppLog:LogDirectory`
  (architecture doc §4.9) as separate entries, built in memory (small enough at this app's log
  volume). Filename: `facility-scheduler-logs-yyyy-MM-dd.zip`.
- **Response `404`** - no log files exist yet (e.g. immediately after a fresh deploy with nothing
  logged).

---

## Staff-facing surface

Aside from the one exception directly above, there is no staff API to call. Staff sign in via Entra
ID (`Program.cs:56-62`) and interact entirely through Blazor Server pages - every page except the
public endpoints above requires authentication by default (`FallbackPolicy =
RequireAuthenticatedUser()`, `Program.cs:64-71`). Guest-vs-member-vs-any-authenticated-user access is
controlled entirely by the Entra Enterprise Application's "Assignment required?" setting, not by any
code in this app (see the deployment guide's §1.2 for how to restrict it).

Because everything else runs over one shared, authenticated SignalR circuit, there's no meaningful
sense in which a staff "request" has its own URL, verb, or independent auth check the way the public
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
| `CreateHoldAsync` | `Task<BookingResult> CreateHoldAsync(SheetBooking booking, string actingUser)` | Creates a single-sheet booking in `Hold` state. Conflict-checked against that sheet's existing events; returns `BookingResult.Conflict` (no write) if anything overlaps. Logs `BookingCreated` on success (`actingUser`, architecture doc §4.9). |
| `CreateConfirmedAsync` | `Task<BookingResult> CreateConfirmedAsync(SheetBooking booking, string actingUser)` | Same as above, in `Confirmed` state. |
| `CreateAcrossSheetsAsync` | `Task<GroupBookingResult> CreateAcrossSheetsAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, string actingUser)` | Creates the same conceptual booking on multiple sheets at once, sharing one `BookingGroupId`. All-or-nothing: any conflict on any sheet aborts the whole request and reports every conflict found. |
| `ConfirmAsync` | `Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId, string actingUser)` | Flips a single event from Hold to Confirmed (`ShowAs: Busy`). |
| `CancelAsync` | `Task CancelAsync(string sheetMailbox, string eventId, string actingUser)` | Hard-deletes a single event. Tolerates a `404` from Graph as "already gone" (e.g. the Breely webhook already claimed/trimmed it) rather than throwing (architecture doc D37) - logged at Debug tier as a no-op in that case, `BookingCancelled` at Standard tier otherwise. |
| `UpdateGroupAsync` | `Task<GroupBookingResult> UpdateGroupAsync(IEnumerable<SheetBooking> members, SheetBooking updatedFields, string actingUser, IEnumerable<string>? newSheetMailboxes = null, Guid? newBookingGroupId = null)` | Updates every event in a booking group (time, category, renter/contact/notes, hold/confirmed state), and creates fresh events for any sheet in `newSheetMailboxes` that isn't already a member (added 2026-08-03 - a sheet added mid-edit previously had no existing event to update and was silently dropped). Re-checks conflicts against the new time before writing, for both existing and new sheets; all-or-nothing. `newBookingGroupId` splits an edited subset off into its own group when only some sheets in the original group were touched; new sheets join whichever group id the rest of the edit settles on. |
| `CancelGroupAsync` | `Task CancelGroupAsync(IEnumerable<SheetBooking> members, bool reopenAsGroupEventHold, string actingUser)` | Cancels every event in a group. `reopenAsGroupEventHold: true` converts each slot back to an unclaimed open Group Event hold (publicly bookable again) instead of deleting it - and, as of 2026-08-03, first absorbs any Group Event hold on the same sheet immediately touching the reopened slot into one contiguous hold (`AbsorbAdjacentHoldsAsync`), rather than leaving separate back-to-back chips. Explicitly clears `BookedBy`/`ExternalBookingId` on the reopened hold rather than leaving the departed booking's values in place (fixed 2026-08-04 - see `ToGraphEvent`'s `clearUnsetOptionalProperties`, architecture doc D48). Now locks per sheet (it didn't before), since that absorption reads other holds before writing. Same per-member 404-tolerance as `CancelAsync` (D37) - one member already being gone doesn't abort the rest of the group. |
| `PreviewSeriesConflictsAsync` | `Task<Dictionary<DateTime, List<SheetBooking>>> PreviewSeriesConflictsAsync(IEnumerable<string> sheetMailboxes, IReadOnlyCollection<DateTime> candidateDates, TimeSpan startTime, TimeSpan endTime)` | Informational only - reports conflicts per candidate date so staff can choose to skip that date. Never blocks anything itself. |
| `CreateSeriesAsync` | `Task<List<SheetBooking>> CreateSeriesAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, DateTime lastOccurrenceDate, IEnumerable<DateTime> excludedDates, string actingUser)` | Creates one native weekly-recurring Graph event per sheet (sharing a `BookingGroupId`), then deletes the specific `excludedDates` occurrences staff chose to skip during review. Does not conflict-check - that's `PreviewSeriesConflictsAsync`'s job, expected to already have run. |
| `CancelSeriesAsync` | `Task CancelSeriesAsync(IEnumerable<SheetBooking> members, string actingUser)` | Deletes an entire recurring series (all occurrences) for every sheet in the group - the "backdoor" for correcting a data-entry mistake at series creation, not a primary UX path. Already tolerated a missing series master (`404`) before Phase 10 - the pattern `CancelAsync`/`CancelGroupAsync` above now also follow. |
| `GetBookingsAsync` | `Task<List<SheetBooking>> GetBookingsAsync(string sheetMailbox, DateTime start, DateTime end)` | Reads one sheet's bookings in a window. Always live (never cached) - used by every conflict check. |
| `GetBookingsForAllSheetsAsync` | `Task<List<SheetBooking>> GetBookingsForAllSheetsAsync(DateTime start, DateTime end)` | Reads every configured sheet's bookings in a window, in parallel. View-rendering read path only (Calendar page, public availability) - cached for 30 seconds, invalidated on every write. |
| `FindByExternalIdAsync` | `Task<SheetBooking?> FindByExternalIdAsync(string externalBookingId, CancellationToken ct)` | Added for the Breely webhook (architecture doc §4.8). Validates the id against a strict allow-list charset, then queries every configured sheet via a Graph `$filter` on the `ExternalBookingId` extended property. Returns `null` if nothing matches; if more than one result comes back on a sheet, prefers a Confirmed match over a Hold (added 2026-08-04, defense in depth alongside D48's fix) - not staff-facing, called only by `BreelyBookingProcessor`. |
| `ClaimHoldAsync` | `Task<SheetBooking?> ClaimHoldAsync(DateTime start, DateTime end, SheetBooking template, Guid groupId, CancellationToken ct)` | Added for the Breely webhook. Tries sheets in configured order; on each, looks for a Group Event hold fully covering the window, converts it to Confirmed (tagged with the caller-supplied `groupId` rather than minting its own - added 2026-08-03 so multiple sheets from one Breely submission, or a sheet reclaimed on reschedule, can share/keep one `BookingGroupId`), and trims the remainder (delete/patch/split, dropping any remainder shorter than the Settings page's configured minimum interval, titled "Available for Group Events"). Returns the claimed booking, or `null` if no sheet had a covering hold. Unlike every other create path above, this treats an existing hold as claimable rather than a conflict. |
| `ForceCreateConfirmedAsync` | `Task<SheetBooking> ForceCreateConfirmedAsync(string sheetMailbox, SheetBooking booking, CancellationToken ct)` | Added for the Breely webhook. Bypasses the conflict check entirely - the deliberate "never drop a real booking" fallback used only when `ClaimHoldAsync` finds no matching hold anywhere. Callers are expected to also flag the result for staff review, since it may land on the wrong sheet. Respects a `BookingGroupId` the caller already set on `booking` (used by the Breely processor to keep it in its submission's group) rather than always minting its own. |
| `MinimumGroupEventBookingIntervalMinutes` / `SetMinimumGroupEventBookingIntervalAsync` | `int MinimumGroupEventBookingIntervalMinutes { get; }` / `Task SetMinimumGroupEventBookingIntervalAsync(int minutes, string actor, CancellationToken ct = default)` | Added 2026-08-03, backs the Settings page's "Minimum group event booking interval" field (default 60, a fixed 30/60/90/120-minute dropdown as of 2026-08-04). Read once at construction from a small file next to the log files; `SetMinimumGroupEventBookingIntervalAsync` updates the in-memory value immediately, re-persists it, and logs `MinimumGroupEventBookingIntervalChanged` (`actor`, added 2026-08-04) - taking effect on the next `ClaimHoldAsync`/`TrimHoldAsync` call with no restart needed. |

### `ClubEventService`

Owns the single dedicated Club Events mailbox. Simpler than `SheetBookingService` by design: one
low-volume mailbox, no per-sheet locking, no `BookingGroupId` concept, and no conflict checking at
all (neither against sheet bookings nor between club events).

| Method | Signature | Behavior |
|---|---|---|
| `CreateAsync` | `Task<ClubEvent> CreateAsync(ClubEvent clubEvent, string actingUser)` | Creates a club event. No conflict check. Logs `ClubEventCreated` on success (architecture doc §4.9). |
| `UpdateAsync` | `Task UpdateAsync(ClubEvent clubEvent, string actingUser)` | Updates an existing club event by `EventId`. |
| `CancelAsync` | `Task CancelAsync(string eventId, string actingUser)` | Hard-deletes a club event. |
| `GetEventsAsync` | `Task<List<ClubEvent>> GetEventsAsync(DateTime start, DateTime end)` | Reads club events in a window. Cached for 30 seconds, invalidated on every write. |

### Shared domain shapes

| Type | Shape | Notes |
|---|---|---|
| `SheetBooking` | `EventId`, `ICalUId`, `SheetMailbox`, `Start`, `End`, `Category` (`BookingCategory`), `State` (`BookingState`), `RenterName`, `RenterPhone`, `RenterEmail`, `Notes`, `BookedBy`, `BookingGroupId` (Guid), `SeriesMasterId`, `ExternalBookingId` | `BookingGroupId` links every sheet's event belonging to one conceptual booking, even single-sheet ones. `SeriesMasterId` is set only on occurrences of a recurring series. `ExternalBookingId` (added for the Breely webhook, architecture doc §4.8) is non-null only for bookings that originated from an external platform's notification rather than staff entry - never set by staff-facing UI. |
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

### `AppLogService`

Added in Phase 10 (architecture doc §4.9) - the app-level activity/debug log backing the Settings
page, deliberately separate from the framework's own `ILogger`. Every service above that writes to
Graph calls into this one after a successful write.

| Method | Signature | Behavior |
|---|---|---|
| `LogActionAsync` | `Task LogActionAsync(string action, string actor, string? eventId = null, string? sheet = null, string? details = null, CancellationToken ct = default)` | Standard tier - always written. Used for definitive actions (booking/series/club-event created, edited, canceled). |
| `LogSecurityAsync` | `Task LogSecurityAsync(string action, string actor, string? details = null, CancellationToken ct = default)` | Standard tier - always written. Used for security-relevant events regardless of level, e.g. a failed webhook secret check. |
| `LogDebugAsync` | `Task LogDebugAsync(string action, string actor, string? eventId = null, string? sheet = null, string? details = null, CancellationToken ct = default)` | Debug tier - a no-op unless the current level is Debug. |
| `SetLevelAsync` | `Task SetLevelAsync(AppLogLevel level, string actor, CancellationToken ct = default)` | Called by the Settings page. Updates the in-memory level immediately and persists it to a marker file in the log directory, so it survives a restart without a redeploy. `actor` (added 2026-08-04) is the real signed-in staff identity, not a hardcoded literal - logged on the resulting `LoggingLevelChanged` line. |
| `TailAsync` | `Task<List<string>> TailAsync(int count, CancellationToken ct = default)` | Returns up to `count` of the most recent lines, oldest-to-newest, reading backward through older rotated files if the current day's file doesn't have enough on its own. Backs the Settings page's 500-line viewer. |
| `ListLogFiles` | `List<string> ListLogFiles()` | Every rotated log file, newest first. Used by `GET /settings/logs/download` to build the zip. |
| `CurrentLevel` / `LogDirectory` | `AppLogLevel CurrentLevel { get; }` / `string LogDirectory { get; }` | Current level and the resolved log directory path (after the `AppLog:LogDirectory` fallback, if unset - see the deployment guide). |

Log lines are single-line, space-separated `key=value` pairs (timestamp, tier, `action`, `actor`,
optionally `eventId`/`sheet`/`details`) - human-readable and grep-able, not JSON. Files rotate daily
(`app-yyyy-MM-dd.log`) and are deleted automatically past `AppLog:RetentionDays` (default 30).
