# FacilityScheduler API Reference

## Overview

This app exposes **seven** HTTP API endpoints. Staff functionality (creating and managing bookings,
club events) is built almost entirely as Blazor Server components rendered over an authenticated
SignalR circuit, not called via HTTP — the endpoints below are the deliberate exceptions.

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/public/availability` | Anonymous | Minimized JSON availability feed for the CMS embed widget |
| `GET /public/calendar` | Anonymous | Full public calendar page (Month/Week/Day) |
| `GET /public/search` | Anonymous | "Find a window with ≥N sheets open" search page |
| `GET /public/practice-ice` | Anonymous | Open times a member could volunteer to host practice ice |
| `POST /api/webhooks/breely` | Shared secret | The one anonymous **write** surface — ingests Breely booking notifications (architecture doc §4.8/§5.5). A deliberate, bounded exception to "public surfaces are read-only," not a broadening of the rule. |
| `GET /settings/logs/download` | Staff sign-in | Log archive download (architecture doc §5.6) |
| `GET /search/export.csv` | Staff sign-in | CSV export of the staff event search (architecture doc §4.12/§5.7) |

**Why these are Minimal API endpoints rather than Blazor pages** — a hard rule (D15), confirmed by a
live incident: `.AllowAnonymous()` was once tried on the shared Razor Components registration to
carve out a public page, and it silently disabled authorization for *every* page in the app, because
`MapRazorComponents<App>()` maps every routable component through one shared endpoint set. Any
anonymous surface must live entirely outside the Blazor component tree. The staff-authenticated log
download follows the same pattern for a different reason: streaming a file download doesn't fit the
SignalR circuit.

Two authenticated Blazor **pages** also serve practice ice hosting (`/practice-ice/request`,
`/practice-ice/approvals`) — they're pages, not API endpoints, so they aren't listed above; see
[Staff-facing surface](#staff-facing-surface).

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

The four read-only member/customer-facing endpoints below are all registered with
`.AllowAnonymous()`, rate-limited via a shared fixed-window limiter (`public-api`: 60
requests/minute per limiter instance, no queueing - excess requests get an immediate `429`), and
never expose resource mailbox addresses. Renter-identifying data is governed by the three-case rule
in the architecture doc §2.3: staff-typed titles shown as-is, Breely titles programmatically
replaced, practice ice host names deliberately published. The JSON availability feed carries only
open Group Event holds, so no other category appears in it at all; the calendar page shows every
category.

The booking webhook is also `.AllowAnonymous()` but sits on its own separate `booking-webhook` rate
limiter and carries secret-based auth on top of route isolation - see its own section below.

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
      "category": "OutOfTownBonspiels",
      "start": "2026-08-15T00:00:00",
      "end": "2026-08-17T00:00:00",
      "isAllDay": true,
      "marksSheetsUnavailable": true,
      "notes": null
    }
  ]
}
```

| Field | Type | Description |
|---|---|---|
| `generatedAtUtc` | datetime | When this response was computed (UTC). |
| `sheetSlots` | array | Open-for-group-event windows only - an existing Group Event-category booking still in **Hold** state. Confirmed group event bookings and every other category (League/Bonspiel/Maintenance/Practice Ice/Learn To Curl/Other) are never included here. |
| `sheetSlots[].sheetLabel` | string | Public-safe display name (e.g. `"Sheet 1"`) - never the underlying resource mailbox address. |
| `sheetSlots[].start` / `.end` | datetime | Local facility time (not UTC), ISO 8601, no offset. |
| `clubEvents` | array | Every club-wide event in the window, regardless of category. |
| `clubEvents[].category` | string | One of `OutOfTownBonspiels`, `Competitions`, `Activities`, `Meetings`, `Closure`, `Other` — the **enum member name**, not the display label (so "Out of Town Bonspiels" is `OutOfTownBonspiels` here). **Changed 2026-08-17** (D79): this previously serialized as the enum's integer ordinal, contradicting this document. `PublicJsonOptions` now registers a string-enum converter, so the name is the wire value. `Bonspiel` was renamed to `OutOfTownBonspiels` and `Competitions` added 2026-08-18 (D81). |
| `clubEvents[].marksSheetsUnavailable` | boolean | `true` when this event closes every sheet for its duration - the widget shows "all sheets reserved" wording specifically for these. |
| `clubEvents[].notes` | string \| null | **Added 2026-09-01 (D108).** The staff-written Notes field, when there is one - `null` otherwise, including for the one Club Event this app creates itself (the "⚠ Web booking needs review" triage marker, architecture doc §4.8), whose Notes always embeds a real customer name and is never published. Truncated to 300 characters. **A sheet booking's own Notes has no counterpart on this feed** - `sheetSlots` only ever describes open windows, never an occupied booking, so there's currently nowhere for one to go (architecture doc §8). |

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

**Month view:** a 7-column month grid with color-coded entry chips (confirmed booking, hold, off-ice
event). Every day shows up to 3 entries with a "+N more" expander for busier days.

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
chip reveals the full category + hold/confirmed state and exact date/time, plus a staff-written Note
when there is one (added 2026-09-01, D108) - shown in its own line, separate from the "All sheets
closed" warning a closure carries. No phone numbers, emails, or resource mailbox addresses ever
appear, and a Note is withheld the same way a Breely-originated title already is: never shown for a
Breely-originated booking or the "⚠ Web booking needs review" triage marker it can create, both of
which carry unreviewed customer-supplied text. Truncated to 300 characters.

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

### `GET /public/practice-ice`

Returns a complete, self-contained HTML page (not JSON) - the anonymous half of practice ice hosting
(architecture doc §5.4.4; full design rationale in `docs/practice-ice-hosting-design.md`). Lists
upcoming windows where every sheet is genuinely free of any activity (a different, stricter
"available" than every other endpoint on this page uses - see D68), each open half-hour linking to
`/practice-ice/request?start=...`, an authenticated Blazor page (not part of this HTTP API - see
[Staff-facing surface](#staff-facing-surface)) where a signed-in member actually submits the request.

- **Auth:** none (anonymous) for this page; the linked request page requires sign-in.
- **CORS:** not applicable (page navigation, not a cross-origin fetch).
- **Rate limit:** shared `public-api` limiter, 60 req/min, no queue.
- **Cache:** server-side, 60 seconds, keyed by date range - the lead-time/horizon window itself
  shifts continuously with the current time, so results reflect "now" within that cache window.

**Response `200 OK`** — `text/html; charset=utf-8`. No query parameters; the eligible-hours/lead-time/
horizon bounds come entirely from configuration (`PracticeIce:*`, deployment guide Appendix A).

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

One of two staff-facing HTTP endpoints in the app (architecture doc §5.6) - built as a plain Minimal
API endpoint rather than a Blazor page for the same "raw HTTP semantics don't fit the SignalR circuit"
reason the anonymous endpoints above are, just gated by sign-in instead of `.AllowAnonymous()`.
Backs the **Settings** page's (`/settings`, architecture doc §4.9) "Download full log archive" link.

- **Auth:** staff sign-in required - explicitly bound to `StaffAuthorizationPolicies.StaffOnly`
  rather than left to the app's default authenticated fallback policy (`Program.cs:64-71`), the same
  belt-and-suspenders choice `/search/export.csv` below makes.
- **Response `200 OK`** - `application/zip`, containing every rotated log file in `AppLog:LogDirectory`
  (architecture doc §4.9) as separate entries, built in memory (small enough at this app's log
  volume). Filename: `facility-scheduler-logs-yyyy-MM-dd.zip`.
- **Response `404`** - no log files exist yet (e.g. immediately after a fresh deploy with nothing
  logged).

---

### `GET /search/export.csv`

The other staff-facing HTTP endpoint (architecture doc §4.12/§5.7) - added for the same "a file
download is a real HTTP response, not circuit work" reason as the log download above. Backs the
**Event Search** page's (`/search`) "Export CSV" link, which only appears once a search has actually
returned at least one result.

- **Auth:** staff sign-in required, explicitly bound to `StaffAuthorizationPolicies.StaffOnly`.
- **Query parameters:** `q` (the raw search text, same grammar as the page - architecture doc §4.12),
  `start`/`end` (`yyyy-MM-dd`, optional - same defaulting/clamping as the page's own date range via
  `SearchRange.Resolve`).
- **Response `200 OK`** - `text/csv`, UTF-8 with a leading BOM (so Excel on Windows doesn't misread
  non-ASCII renter names). Filename: `event-search-yyyy-MM-dd.csv`. Columns: `Date, Start, End, Title,
  Type, Category, Sheets, Status, All day` - never `RenterPhone`/`RenterEmail` (operator decision).
  Contains **every** match, not a capped subset - the 300-row render cap on the live page is a
  render-cost concern, not a real result limit. A title beginning with `=`, `+`, `-`, `@`, tab, or CR
  is prefixed with a literal `'` (CSV-formula-injection guard) before it's written.
- **Response `400 Bad Request`** - the query resolved to nothing at all (a blank/whitespace `q`).
- **Stateless:** re-parses `q` and re-fetches from `SheetBookingService`/`ClubEventService` rather than
  reading anything held by a live circuit, which is what makes the URL shareable and lets it hit the
  30-second view cache (architecture doc §4.3) instead of always re-fanning-out to Graph.

---

## Staff-facing surface

Aside from the two exceptions directly above, there is no staff API to call. Staff sign in via Entra
ID (`Program.cs:56-62`) and interact entirely through Blazor Server pages - every page except the
public endpoints above requires authentication by default (`FallbackPolicy =
RequireAuthenticatedUser()`, `Program.cs:64-71`). Guest-vs-member-vs-any-authenticated-user access is
controlled entirely by the Entra Enterprise Application's "Assignment required?" setting, not by any
code in this app (see the deployment guide's Step 4b for how to restrict it).

Because everything else runs over one shared, authenticated SignalR circuit, there's no meaningful
sense in which a staff "request" has its own URL, verb, or independent auth check the way the public
endpoints do - a page load establishes the circuit, and every subsequent staff action (create a
booking, cancel a series, etc.) is a method call within that same already-authenticated circuit, not
a new HTTP request.

If a real staff-facing API is ever needed (e.g. for a future mobile app or third-party integration),
it would need to be built as its own set of Minimal API endpoints - following the same pattern as the
public ones, but with `RequireAuthorization()` instead of `.AllowAnonymous()` - calling into the same
service layer documented below rather than duplicating its logic.

**Two pages added for practice ice hosting (§5.4.4) don't fit "staff-facing" cleanly.**
`/practice-ice/request` is meant for *members*, not staff; `/practice-ice/approvals` is staff-only.
Per architecture doc §6.5/D74, the app's default authorization policy requires the `facility:staff`
claim (decided by live Entra group membership, not Entra's own App Role assignment - this tenant is
Entra ID Free, which doesn't support the group-based version of that feature), applied to every page
except `/practice-ice/request`'s own explicit
`[Authorize(Policy = StaffAuthorizationPolicies.AnyAuthenticatedUser)]` carve-out.

Note the claim type: an app-owned `facility:staff` matched with `RequireClaim`, deliberately **not**
`ClaimTypes.Role` + `RequireRole` — that pairing silently never matches under Microsoft.Identity.Web's
overridden `RoleClaimType` and caused a full staff lockout on first deploy (architecture doc §6.5/D75).
The carve-out's interaction with the routing pipeline is **still not verified against a real non-staff
sign-in** - see architecture doc §6.5/§8 before inviting members as guests at any real volume.

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
| `CreateAcrossSheetsAsync` | `Task<GroupBookingResult> CreateAcrossSheetsAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, string actingUser)` | Creates the same conceptual booking on multiple sheets at once, sharing one `BookingGroupId`. All-or-nothing: any conflict on any sheet aborts the whole request and reports every conflict found. Season-gated (architecture doc §4.10, D84) - a request outside the configured booking season is rejected up front, before any lock or Graph call, via a synthetic `GroupBookingResult.Conflict` entry (`SheetMailbox = "__season__"`). Called by both the staff booking form and `PracticeIceRequestService.SubmitAsync`, so this one check covers both. |
| `ConfirmAsync` | `Task<SheetBooking> ConfirmAsync(string sheetMailbox, string eventId, string actingUser)` | Flips a single event from Hold to Confirmed (`ShowAs: Busy`). |
| `CancelAsync` | `Task CancelAsync(string sheetMailbox, string eventId, string actingUser)` | Hard-deletes a single event. Tolerates a `404` from Graph as "already gone" (e.g. the Breely webhook already claimed/trimmed it) rather than throwing (architecture doc D37) - logged at Debug tier as a no-op in that case, `BookingCancelled` at Standard tier otherwise. |
| `UpdateGroupAsync` | `Task<GroupBookingResult> UpdateGroupAsync(IEnumerable<SheetBooking> members, SheetBooking updatedFields, string actingUser, IEnumerable<string>? newSheetMailboxes = null, Guid? newBookingGroupId = null)` | Updates every event in a booking group (time, category, renter/contact/notes, hold/confirmed state), and creates fresh events for any sheet in `newSheetMailboxes` that isn't already a member (added 2026-08-03 - a sheet added mid-edit previously had no existing event to update and was silently dropped). Re-checks conflicts against the new time before writing, for both existing and new sheets; all-or-nothing. `newBookingGroupId` splits an edited subset off into its own group when only some sheets in the original group were touched; new sheets join whichever group id the rest of the edit settles on. |
| `CancelGroupAsync` | `Task CancelGroupAsync(IEnumerable<SheetBooking> members, bool reopenAsGroupEventHold, string actingUser)` | Cancels every event in a group. `reopenAsGroupEventHold: true` converts each slot back to an unclaimed open Group Event hold (publicly bookable again) instead of deleting it - and, as of 2026-08-03, first absorbs any Group Event hold on the same sheet immediately touching the reopened slot into one contiguous hold (`AbsorbAdjacentHoldsAsync`), rather than leaving separate back-to-back chips. Explicitly clears `BookedBy`/`ExternalBookingId` on the reopened hold rather than leaving the departed booking's values in place (fixed 2026-08-04 - see `ToGraphEvent`'s `clearUnsetOptionalProperties`, architecture doc D48). Now locks per sheet (it didn't before), since that absorption reads other holds before writing. Same per-member 404-tolerance as `CancelAsync` (D37) - one member already being gone doesn't abort the rest of the group. |
| `PreviewSeriesConflictsAsync` | `Task<Dictionary<DateTime, List<SheetBooking>>> PreviewSeriesConflictsAsync(IEnumerable<string> sheetMailboxes, IReadOnlyCollection<DateTime> candidateDates, TimeSpan startTime, TimeSpan endTime)` | Informational only - reports conflicts per candidate date so staff can choose to skip that date. Never blocks anything itself. |
| `CreateSeriesAsync` | `Task<List<SheetBooking>> CreateSeriesAsync(IEnumerable<string> sheetMailboxes, SheetBooking template, DateTime lastOccurrenceDate, IEnumerable<DateTime> excludedDates, string actingUser)` | Creates one native weekly-recurring Graph event per sheet (sharing a `BookingGroupId`), then deletes the specific `excludedDates` occurrences staff chose to skip during review. Does not conflict-check - that's `PreviewSeriesConflictsAsync`'s job, expected to already have run. |
| `UpdateSeriesAsync` | `Task<GroupBookingResult> UpdateSeriesAsync(IEnumerable<SheetBooking> members, SheetBooking updatedFields, IEnumerable<string> targetSheetMailboxes, string actingUser)` | Whole-series edit, added 2026-08-18 (D82) - category/renter/notes across every occurrence, past included, plus add/remove sheets. **Time is not editable**; `updatedFields.Start`/`End` are ignored (kept sheets are PATCHed with `includeTime: false`). Sheets in `targetSheetMailboxes` but not already members are added: occurrence windows are read back live via `GetInstancesAsync` against a reference sheet that's staying (not one being removed), conflict-checked on the new sheet for every window, all-or-nothing - a single colliding date refuses the whole edit. Members not in `targetSheetMailboxes` are removed (series master deleted; never conflicts). An added sheet's series replicates the reference master's `Recurrence` verbatim, then has the same dates deleted that the reference series already excludes, so it doesn't gain occurrences the rest of the series doesn't have. Passing an empty `targetSheetMailboxes` is a no-op, not an implicit delete - use `CancelSeriesAsync`. |
| `CancelSeriesAsync` | `Task CancelSeriesAsync(IEnumerable<SheetBooking> members, string actingUser)` | Deletes an entire recurring series (all occurrences) for every sheet in the group - the "backdoor" for correcting a data-entry mistake at series creation, not a primary UX path. Tolerates a missing series master (`404`) as "already gone," the same pattern `CancelAsync`/`CancelGroupAsync` above follow (D37). |
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

### `PracticeIceRequestService`

Added for practice ice hosting (architecture doc §5.4.4). The write path - `PublicAvailabilityService`
(also new: `GetPracticeIceWindowsAsync`/`FindPracticeIceWindowContainingAsync`) stays read-only,
matching the existing split between it and `SheetBookingService`.

| Method | Signature | Behavior |
|---|---|---|
| `SubmitAsync` | `Task<PracticeIceSubmitResult> SubmitAsync(DateTime start, int durationMinutes, string hostName, string hostEmail, bool certified, string? notes)` | Re-validates everything server-side against a fresh availability check (the query string/form inputs are untrusted), then writes a `PracticeIce`+`Hold` booking across every sheet via `SheetBookingService.CreateAcrossSheetsAsync` (same all-or-nothing guarantee, same live per-sheet-locked conflict check as every other write path). Blocks outright with `PracticeIceSubmitResult.Invalid` if `FacilityConfiguration.PracticeIceMailConfigured` is false, rather than silently creating an unnotified hold. On success, emails the approver group - a failed send doesn't undo the write or throw; it's reported via `NotificationSent` instead (D70). |
| `GetPendingAsync` | `Task<List<PracticeIceRequestSummary>> GetPendingAsync()` | Every pending (`PracticeIce`+`Hold`) group, one row per `BookingGroupId`, ordered by upcoming start time (not submission age - `SheetBooking` has no created-at field). |
| `ApproveAsync` | `Task<PracticeIceActionResult> ApproveAsync(Guid bookingGroupId, string actingUser)` | Confirms the group (`UpdateGroupAsync`) and emails the volunteer. `Success` reflects the confirm; `NotificationSent` is reported separately and independently (D70). |
| `DeclineAsync` | `Task<PracticeIceActionResult> DeclineAsync(Guid bookingGroupId, string reason, string actingUser)` | Requires a non-empty `reason` (thrown as `ArgumentException` otherwise - the UI is expected to validate first). Cancels the group (`CancelGroupAsync`, hard delete per D9) and emails the volunteer with the reason. |

### `StaffAccessService`

Added for staff vs. member authorization (architecture doc §6.5/D74). Called from `Program.cs`'s
`OnTokenValidated` hook at sign-in, not from any Blazor component - not part of the request-handling
service layer the rest of this appendix documents, but included here for completeness.

| Method | Signature | Behavior |
|---|---|---|
| `IsStaffAsync` | `Task<bool> IsStaffAsync(string userObjectId)` | Checks live Entra group membership (`IGraphGroupGateway.IsMemberOfGroupAsync`, `graphClient.Users[id].CheckMemberGroups`) against `FacilityConfiguration.StaffGroupId`. **Fails closed** - a Graph error is logged (`StaffGroupCheckFailed`, Standard tier so it's visible without Debug on) and treated as `false`, never propagated, so a transient outage or a missing/unconsented Graph permission degrades a sign-in to non-staff rather than risking an accidental grant. Requires **both** `GroupMember.Read.All` and `User.Read.All` (application) - see deployment guide Step 5. |

Also exposes `StaffClaimType` (`facility:staff`) and `StaffClaimValue`, the claim `Program.cs` adds at
sign-in and the policies below match on.

### `StaffAuthorizationPolicies`

The app's authorization policies, in one place used by both `Program.cs` and the tests. Defined here
rather than inline specifically so the policy objects are reachable from the test project — inline
lambdas were not, which is how a full staff lockout reached production (architecture doc §6.5/D75).

| Member | Purpose |
|---|---|
| `StaffOnly` (const) / `BuildStaffOnly()` | Signed in **and** carrying the `facility:staff` claim. Registered as both the `FallbackPolicy` (so every page/endpoint without explicit authorization metadata inherits it) and a named policy for endpoints that opt in explicitly, like `/settings/logs/download` and `/search/export.csv`. |
| `AnyAuthenticatedUser` (const) / `BuildAnyAuthenticatedUser()` | Signed in, staff or not — the single deliberate carve-out, used only by `/practice-ice/request`. |
| `Configure(AuthorizationOptions)` | Wires both policies plus the fallback. Called by `Program.cs`; called directly by `StaffAuthorizationPolicyTests` so what's asserted is exactly what runs. |

### Shared domain shapes

| Type | Shape | Notes |
|---|---|---|
| `SheetBooking` | `EventId`, `ICalUId`, `SheetMailbox`, `Start`, `End`, `Category` (`BookingCategory`), `State` (`BookingState`), `RenterName`, `RenterPhone`, `RenterEmail`, `Notes`, `BookedBy`, `BookingGroupId` (Guid), `SeriesMasterId`, `ExternalBookingId` | `BookingGroupId` links every sheet's event belonging to one conceptual booking, even single-sheet ones. `SeriesMasterId` is set only on occurrences of a recurring series. `ExternalBookingId` (added for the Breely webhook, architecture doc §4.8) is non-null only for bookings that originated from an external platform's notification rather than staff entry - never set by staff-facing UI. |
| `PracticeIceSubmitResult` | `IsSuccess`, `IsConflict`, `Message?`, `NotificationSent` | Result of `SubmitAsync`. `IsSuccess` reflects the booking write alone - `NotificationSent` is a separate flag so a mail failure never reads as a failed submission (D70). |
| `PracticeIceRequestSummary` | `BookingGroupId`, `Start`, `End`, `HostName`, `HostEmail?`, `Notes?`, `SheetCount` | One pending request as shown on the approvals queue - the group's sheet-events collapsed into a single row. |
| `PracticeIceActionResult` | `Success`, `NotificationSent` | Result of `ApproveAsync`/`DeclineAsync` - same split as `PracticeIceSubmitResult`, for the same reason. |
| `ClubEvent` | `EventId`, `ICalUId`, `Title`, `Category` (`ClubEventCategory`), `Start`, `End`, `IsAllDay`, `MarksSheetsUnavailable`, `Notes`, `BookedBy` | Not tied to any sheet. |
| `BookingCategory` | `GroupEvent`, `League`, `Event`, `Bonspiel`, `Maintenance`, `PracticeIce`, `LearnToCurl`, `Other` | Display labels ("Group Event", "Practice Ice", "Learn To Curl") are kept separate from these wire values via `CalendarStyles.CategoryLabel` - the values above are what's actually round-tripped through Graph's `categories` property. |
| `BookingState` | `Hold`, `Confirmed` | |
| `ClubEventCategory` | `OutOfTownBonspiels`, `Competitions`, `Activities`, `Closure`, `Other`, `Meetings` | Member **names** are the public API wire value (D79) and the Graph category literal - renaming one is a breaking change on both fronts. Ordinals are unpublished, so declaration order is free; picker display order is `CalendarStyles.ClubEventCategories`. Display label differs from the member name only for `OutOfTownBonspiels` ("Out of Town Bonspiels") - see `CalendarStyles.ClubEventCategoryLabel`. |
| `BookingResult` | `IsSuccess`, `Booking?`, `Conflicts: List<SheetBooking>` | Result of a single-sheet create. |
| `GroupBookingResult` | `IsSuccess`, `Bookings: List<SheetBooking>`, `Conflicts: List<SheetBooking>` | Result of a multi-sheet create/update. |

### `FacilityConfiguration`

Singleton exposing the tenant's runtime configuration to every service above: `SheetMailboxes`
(string array), `ClubEventsMailbox`, `TimeZone`, `ZoneInfo`, `Name`, `LogoPath`, plus
`ToUtcQueryString(DateTime)`/`FromUtcResponseString(string)` helpers for Graph's UTC query-parameter
and response conventions. Constructed eagerly at startup (`Program.cs:78-81`) so a misconfigured
deployment fails immediately rather than on first request.

Also exposes the practice ice settings (`PracticeIceEligibleStartHour`/`EligibleEndHour`,
`PracticeIceMinLeadHours`/`MaxHorizonDays`, `PracticeIceApproverEmail`, `PracticeIceMailerMailbox`,
`PracticeIceMailConfigured`). Unlike `Facility:TenantDomain`/`SheetMailboxLocalParts`/`TimeZone`,
the two mail addresses are allowed to be blank at startup - `PracticeIceMailConfigured` gates the
feature at request time instead, so an incremental feature rollout doesn't stop an already-running
deployment from booting (deployment guide Appendix A).

`StaffGroupId` (architecture doc §6.5/D74) is **not** treated leniently like the practice ice mail
addresses - it's validated in the same required-field block as `Facility:TenantDomain` above and
throws at construction if blank, since an unset value would lock every staff page for everyone, not
just disable one feature (deployment guide Appendix A).

### `AppLogService`

The app-level activity/debug log (architecture doc §4.9) backing the Settings
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

### `SchedulingWindowService`

The publish cutoff and booking season (architecture doc §4.10) backing the Settings page's "Public
Calendar Visibility" and "Booking Season" sections. Persists all three values as one JSON file
(`scheduling-window.json`, D83) in the same log directory `AppLogService` uses.

| Method | Signature | Behavior |
|---|---|---|
| `PublicCutoffDate` / `SeasonStartDate` / `SeasonEndDate` | `DateTime? {name} { get; }` | Current values, `null` if unconfigured. |
| `IsPastPublicCutoff` | `bool IsPastPublicCutoff(DateTime date)` | `date.Date > PublicCutoffDate?.Date` - null-safe, always `false` if no cutoff is set. A date exactly on the cutoff is not past it. Consulted by `PublicAvailabilityService.GetRangeViewAsync`/`ComputeAvailabilityAsync`. |
| `IsOutsideSeason` | `bool IsOutsideSeason(DateTime date)` | True if `date` is before `SeasonStartDate` or after `SeasonEndDate`, either bound independently null-safe. Consulted by `SheetBookingService.CreateAcrossSheetsAsync`, `PublicAvailabilityService.GetOpenSlotsAsync`, and (via a horizon-clamp adjustment rather than a direct call) `GetPracticeIceWindowsAsync`. |
| `SetPublicCutoffAsync` | `Task SetPublicCutoffAsync(DateTime? date, string actor, CancellationToken ct = default)` | Called by the Settings page's Apply/Clear buttons. Updates in-memory immediately, persists best-effort, invalidates the public view cache, and logs `PublicCutoffChanged` if the value actually changed. |
| `SetSeasonWindowAsync` | `Task SetSeasonWindowAsync(DateTime? start, DateTime? end, string actor, CancellationToken ct = default)` | Sets both bounds together (Settings page's single Save/Clear for the pair). Same invalidation/logging pattern; logs `SeasonWindowChanged`. |

Not consulted by `/public/search` or `/public/practice-ice`'s cutoff behavior (the publish cutoff
deliberately doesn't apply there - architecture doc §4.10), and not called at all from
`CreateSeriesAsync` - season exclusion for a series happens client-side in the wizard's preview step
instead (architecture doc §4.5/§4.10, D85), not as a service-layer check.
