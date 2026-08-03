# Facility Scheduling System — Architecture & Design

**Project:** Curling sheet scheduling and availability management on Exchange Online
**Status:** As-built. Phases 0–9 complete; Phase 10 (production hardening) in progress.
**Date:** 2026-07-16 (wholesale rewrite reflecting the actual built system — supersedes the 2026-07-11 pre-build design this document originally described)
**Author:** Design iteration between club operator and Claude
**Stack:** .NET / C#, Blazor Server (.NET 10) — see §9, D14

---

## 1. Executive Summary

A web-based system for managing the scheduling and availability of curling sheets, built on Microsoft Exchange Online (EXO) resource mailboxes as the system of record. Each sheet is modeled as an EXO resource mailbox; every booking is a calendar event on that mailbox. A custom Blazor Server application — not Outlook — is the operational interface for staff: per-sheet/consolidated calendar views (Month/Week/Day), one-off and recurring bookings spanning multiple sheets at once, a separate whole-club "Club Events" resource, three read-only public-facing surfaces (a minimized JSON availability API for a thin CMS embed, a full public calendar page members browse directly with its own Month/Week/Day views, and a search page for finding a window with enough open sheets for a group event), one inbound write surface: a webhook that ingests booking notifications from Breely, the club's separate customer-facing booking platform, as a Phase 10 stopgap pending real bidirectional sync (§4.8), and a staff-only Settings page (§4.9) giving visibility into what the app has actually done in production - a rotating activity/debug log, since none previously existed once the Breely webhook started acting on its own.

The design still avoids any adjacent authoritative datastore: all booking data, including rich metadata (renter contact, notes), lives on the calendar event itself. The only additional infrastructure is a short-lived, disposable read cache, deliberately scoped to never touch the paths that enforce double-booking prevention.

Every tenant-specific value (the M365 tenant domain, which mailboxes are sheets vs. Club Events, the facility's local time zone) is configuration, not code — the same deployed app can be repointed at a different tenant, or stood up fresh for a different facility, without a recompile (§4.6).

The pattern generalizes to other bookable facilities (bowling lanes, tennis courts, etc.) — nothing in the architecture is curling-specific except the vocabulary and the configured sheet count.

---

## 2. Scope and Requirements

### 2.1 Delivered

| # | Requirement | Status |
|---|-------------|--------|
| R1 | Model each curling sheet as an independently bookable resource with its own calendar | Done — sheet count and mailbox addresses are configuration (§4.6), not a hardcoded "5" |
| R2 | Staff-mediated booking: staff create, modify, and cancel all bookings through a custom web UI | Done |
| R3 | Booking states beyond free/busy: **Hold** (soft, blocks other bookings) and **Confirmed** (hard) | Done — Hold is available only for Group Event; every other category is always Confirmed (§4.2) |
| R4 | Booking categories, consistently represented across all sheets | Done — sheet categories: Group Event / League / Bonspiel / Maintenance / Practice Ice / Other (Event is reserved for Club Events, §4.4) |
| R5 | Rich contextual metadata attached to the booking itself: renter name, contact, notes | Done (Price was cut from scope during build — never used) |
| R6 | Multiple views: per-sheet/all-sheets Month, Week, and Day grids | Done. The originally-scoped derived "≥N sheets available" consolidated view (interval-merge engine) was deprioritized twice during the initial build, then delivered in Phase 10 as its own dedicated page (`/public/search`, §5.4.3) once member feedback raised it again |
| R7 | Anonymous public read-only view, embeddable in the club website | Done, as **two** distinct surfaces (§5.4): a minimized JSON availability API + CMS embed widget, and a separate full public month calendar page |
| R8 | Outlook/OWA remains available as a read-only fallback | Done |
| R9 | Recurring bookings supported via native calendar recurrence | Done (§4.5) |
| R10 | Double-booking prevention, enforced by the application | Done (§6.1); the Phase 7 read cache is deliberately scoped to never weaken this (§4.3) |
| R11 | **Club Events**: a whole-club resource for large events, separate from individual sheet reservations | Done (§4.4), including a closure-conflict cross-check added after build (§4.4) |
| R12 | Configuration-driven tenant/mailbox/timezone, no hardcoded tenant values in code | Done (§4.6), added during Phase 10 |
| R13 | Reflect bookings made through the club's separate customer-facing booking platform (Breely) onto this calendar | Done (§4.8), added during Phase 10 as an explicit fallback/stopgap - not a replacement for real bidirectional sync, which remains future work (§2.2) |
| R14 | Staff-visible record of what the app has actually done in production (who created/edited/canceled what, plus optional deeper detail while troubleshooting) | Done (§4.9), added during Phase 10 after the operator found the Breely webhook's production behavior opaque with no way to see it - a Settings page exposes a Standard/Debug level toggle and a viewer/download for a rotating log file |

### 2.2 Out of Scope (explicitly deferred or rejected)

- Payments, fees, deposits (Price field was added then removed — never used)
- Membership rules, booking caps, priority tiers, waitlists
- Member/public self-service booking (no member identity object; public calendar is read-only)
- Post-season reporting or cancellation audit history — cancelled bookings are hard-deleted, metadata loss on cancellation is accepted
- Automatic expiration of rental holds
- ICS calendar publishing (evaluated and rejected based on prior operational experience)
- Companion/adjacent authoritative database (all data of record stays on the calendar event)
- Bulk rental-availability painting tool (a multi-weekday bulk-create wizard) — scoped, then explicitly shelved as overkill for a once-per-season, near-empty-calendar operation; the series wizard covers the real need
- Real bidirectional calendar sync with Breely (the club's booking platform) — the intended long-term answer to keeping the two systems consistent, deferred because it needs real development time the club didn't have. The Phase 10 webhook (§4.8) is an explicit, one-way, best-effort stopgap in the meantime, not a substitute for it - if/when sync is built, most of §4.8 becomes redundant and should be reassessed rather than kept running alongside it.

### 2.3 Constraints and Environment

| Constraint | Detail |
|---|---|
| Tenant | Configuration-driven (§4.6) — a trial tenant was used through most of the build; the app now deploys against whichever tenant its `Facility`/`Graph` configuration points at, without a recompile. |
| Concurrency | Effectively 1 staff user at a time; 2 by rare coincidence. |
| Source of truth | Exchange Online. The web app holds no authoritative data. |
| Cache | Ephemeral only: short-TTL, non-authoritative, fully rebuildable from EXO at any moment; deliberately never applied to conflict-check reads (§4.3). |
| Public surface | Must never expose booking metadata beyond what staff themselves type into a title (§5.4); server-side minimization is mandatory for the JSON API; the full public calendar shows titles by design (staff are trained not to put renter PII in a title, rather than the app stripping it programmatically). |
| CMS | Public view integrates as a thin embed calling the app's public endpoint — no Graph logic inside the CMS. |
| Deployment | Azure App Service is the primary target; see `docs/deployment-guide.md` for the full deployment process and a platform-agnostic requirements section for other hosts. |
| Source of truth for customer-facing availability | Breely, not this app. This calendar's copy of a Breely booking is best-effort and one-way (§4.8) - if the webhook is ever missed or wrong, Breely's own records are what's authoritative for what a customer was actually promised, not this calendar. |

---

## 3. System Architecture

### 3.1 Component Overview

```mermaid
flowchart TB
    subgraph M365 ["Microsoft 365 Tenant (configuration-driven, §4.6)"]
        EID["Entra ID<br/>(staff SSO + app registration)"]
        subgraph EXO ["Exchange Online — system of record"]
            SN["Resource mailboxes<br/>Sheet 1..N (configured count)"]
            CE["Resource mailbox<br/>Club Events"]
        end
    end

    subgraph App ["Blazor Server Application (single deployment)"]
        UI["Staff calendar UI<br/>(Month/Week/Day, series wizard,<br/>Club Events, filters)"]
        API["Services<br/>SheetBookingService · ClubEventService<br/>conflict enforcement · FacilityConfiguration"]
        CACHE["Ephemeral cache (IMemoryCache)<br/>view-reads only, 30s TTL<br/>never the conflict-check path"]
        PUBAPI["Public JSON endpoint<br/>+ embed widget JS"]
        PUBCAL["Public calendar endpoint<br/>(plain Minimal API, no Blazor circuit)"]
        PUBSEARCH["Public search endpoint<br/>(≥N sheets available, §5.4.3)"]
        WEBHOOK["Booking webhook endpoint<br/>(shared-secret auth, §4.8/§5.5)"]
        SETTINGS["Settings UI<br/>(logging level, log viewer/download, §4.9)"]
        LOG[["Rotating log files<br/>(outside app folder, §4.9)"]]
    end

    subgraph Web ["Club website (CMS)"]
        EMBED["Thin embed block<br/>(calls public JSON endpoint)"]
        IFRAME["iframe<br/>(embeds public calendar page)"]
    end

    BREELY(("Breely booking platform<br/>(external, non-authoritative for this app)"))

    STAFF(("Staff")) -->|"HTTPS + Entra SSO"| UI
    STAFF -->|"HTTPS + Entra SSO"| SETTINGS
    MEMBER(("Club members<br/>(anonymous)")) --> IFRAME
    MEMBER --> PUBSEARCH
    ANON(("Public visitors")) --> EMBED
    STAFF -.->|"read-only fallback<br/>(Reviewer permission)"| EXO
    BREELY -->|"HTTPS POST + shared secret<br/>(fire-and-forget notification)"| WEBHOOK

    UI --> API
    API <--> CACHE
    API -->|"Microsoft Graph<br/>(REST/JSON)"| EXO
    API <-->|"OAuth 2.0 tokens"| EID
    EMBED --> PUBAPI
    IFRAME --> PUBCAL
    PUBAPI --> API
    PUBCAL --> API
    PUBSEARCH --> API
    WEBHOOK --> API
    API -.->|"writes: actions, security events"| LOG
    WEBHOOK -.->|"writes: auth failures"| LOG
    SETTINGS -.->|"reads/downloads;<br/>sets level"| LOG
```

Key structural decisions visible above:

- **One Blazor Server deployment**, not a separate CMS-side service — the CMS integration is a thin embed/iframe with no credentials and no Graph logic.
- **Public read surfaces, not just one.** The JSON API + widget is a subordinate feature (per-sheet availability only). The public calendar page is the *primary* way club members see what's happening club-wide while unauthenticated. The search page (§5.4.3) answers "when can I get N sheets" directly rather than requiring a member to scan the calendar by hand. All three read through the same services, which read through the same cache.
- **One inbound write surface, structurally different in kind from the read surfaces above.** The booking webhook (§4.8/§5.5) is the only anonymous endpoint that *writes* to the calendar - a deliberate, bounded exception to "public surfaces are read-only," added in Phase 10 as a fallback until real bidirectional calendar sync exists (§2.2).
- **Public pages are plain Minimal API endpoints, never Blazor components** sharing the staff app's authenticated circuit (`MapRazorComponents<App>()`). This is a hard architectural rule established the hard way (§8) — not a style preference, and it applies to the webhook too.
- **The cache is scoped to view-rendering reads only.** Every conflict-check read (the thing standing between two staff members double-booking a sheet) always hits Graph live, never the cache. See §4.3.
- **Outlook is a read path only.** Staff hold Reviewer (read-only) calendar permission on the resource mailboxes.
- **The activity/debug log (§4.9) is a flat rotating file, not a database** — consistent with D7 (no companion datastore for booking data). It's written by the same services that write to Graph, and by the webhook endpoint's own auth check; the Settings page reads it and controls the log level, but nothing else in the app depends on it existing.

### 3.2 What Exchange Provides vs. What the App Owns

| Concern | Owner |
|---|---|
| Durable storage of bookings + metadata | Exchange Online |
| Recurrence semantics (series, occurrences, exceptions) | Exchange Online |
| Fallback human-readable calendar UI | Exchange Online (Outlook/OWA) |
| Mailbox permissions, audit logging | Exchange Online |
| **Conflict / double-booking enforcement** | **Application** (direct writes bypass the Resource Booking Attendant) |
| Multi-sheet grouping identity (`BookingGroupId`) | Application (§4.5) |
| State vocabulary and category schema integrity | Application (sole writer discipline; Exchange validates nothing) |
| Recurring series creation, per-occurrence edit/cancel semantics | Application |
| Club Events / closure-conflict cross-check | Application (§4.4) |
| Public data minimization | Application |
| Tenant/mailbox/timezone configuration | Application, externalized to config (§4.6) |

---

## 4. Data Architecture

### 4.1 Anatomy of a Booking (one EXO calendar event)

Every piece of booking data lives on the event object:

- **subject** — human-readable, for the Outlook fallback (`"{Category} - {RenterName}"` or just `{Category}` if blank).
- **start / end (+ timezone)** — the reserved slot, tagged with the facility's configured local time zone (§4.6), never UTC.
- **showAs** — `tentative` = Hold, `busy` = Confirmed. Drives free/busy and the hold-vs-confirmed encoding; conflict enforcement itself is the app's job regardless of `showAs` (§6.1).
- **categories** — one of Group Event / League / Bonspiel / Maintenance / Practice Ice / Other for sheets (Event is reserved for Club Events, never offered in the sheet-booking picker); Bonspiel / Activities / Closure / Other for Club Events. The category's *display* label (e.g. "Group Event", "Practice Ice") is kept separate from the enum's own wire value written to/read from this Graph property — renaming a label never risks breaking the read-back parse.
- **recurrence** — native Graph recurring series for league blocks etc. (§4.5).
- **Named extended properties** (server-side filterable): `BookedBy`, `BookingGroupId` (§4.5).
- **JSON blob** (one extended property, display-only): renter name, phone, email, notes.
- **iCalUId / EventId** — `EventId` is the Graph REST id (not durable across some mailbox operations); `ICalUId` is the durable identifier.

**Design rule (unchanged from original design):** anything filterable gets its own named extended property; everything else goes in the JSON blob, kept small.

**Read gotcha (confirmed during build):** `singleValueExtendedProperties` are never returned by default — a blanket `$expand` is insufficient; it must be scoped with a `$filter` sub-clause naming the specific property IDs. Every read path that needs metadata uses the filter-scoped form.

### 4.2 State Model

| Business state | `showAs` | Category | Blocks other bookings? |
|---|---|---|---|
| Open / available | *(no event)* | — | No |
| Hold | `tentative` | Group Event only | **Yes** (app-enforced) |
| Confirmed | `busy` | Any | Yes |

- **Only Group Event can be a Hold** — every other category is always a hard (Confirmed) booking, enforced client-side (the Hold/Confirmed toggle and phone/email fields are hidden entirely for non-Group-Event categories) and coerced server-side.
- **No category defaults on a new booking, series, or Club Event** — staff must explicitly pick one; Save/Create is disabled with a validation message until they do. This was added after live-testing feedback surfaced confusion from a silently-preselected category. Editing an existing item still loads its real stored category, unaffected.
- **Hold vs. Confirmed also has no default** — a new Group Event booking's state is `null` until staff explicitly picks Hold or Confirmed.
- Confirming a booking = update `showAs` `tentative` → `busy` on the existing event.
- Cancellation = hard delete, with one exception: a Group Event cancel offers "reopen for group event" (flips back to an unclaimed Hold, renter fields stripped) as an alternative to permanent deletion.
- Time entry is 30-minute increments, covering the full 24-hour day (not just a daytime window) — represented internally as minutes-from-midnight, with 1440 meaning "end of this day" rather than colliding with a start-of-day option.
- **End time must be after start time on every booking/series/Club Event form** — enforced the same way as every other required field (Save/Create disabled with a validation message). Added after a live-found bug: submitting an inverted range (end before start) previously reached Graph's calendar API unvalidated, which rejected it with an unhandled error that took down the Blazor circuit.

### 4.3 Ephemeral Cache

Two layers, added at different points in the build, deliberately kept separate:

| Layer | Scope | TTL | What it must never touch |
|---|---|---|---|
| Staff-facing view cache (Phase 7) | `SheetBookingService.GetBookingsForAllSheetsAsync`, `ClubEventService.GetEventsAsync` — the "everything in this window, for display" reads used by the calendar pages | 30s | `GetEventsInRangeAsync`/`GetBookingsAsync` (the per-sheet reads every conflict check uses) are **never cached** — a cached snapshot there could mask a just-created booking and allow a double-booking within the TTL window. This is the one invariant this cache design cannot compromise. |
| Public-facing response cache (Phase 8/9) | `PublicAvailabilityService`'s own computed `GetAvailabilityAsync`/`GetMonthViewAsync` responses | 60s | Sits as an outer layer on top of the staff-facing cache above — a cold public cache still benefits from a warm inner cache, and vice versa. |

Invalidation for the staff-facing layer is a full clear (not precise per-window overlap tracking) of that service's own tracked cache keys, on every successful write — simple, and proportionate given this app's actual write volume (1–2 staff). The public-facing layer expires on its own TTL only, since it doesn't sit behind a write path.

**Explicitly rejected:** Graph change-notification webhooks (subscriptions expire and need renewal/reconciliation infrastructure to catch out-of-band edits, of which there are structurally none — sole-writer app + read-only Outlook access).

### 4.4 Club Events

A dedicated resource mailbox, not tied to any physical sheet, for whole-club-scale events (bonspiels, closures, club activities) that would otherwise require booking every sheet simultaneously.

**Why a dedicated mailbox instead of writing the same event to every sheet calendar:** independent per-sheet Graph writes have no transactional guarantee; a single event on a single dedicated calendar is atomic by construction.

**Category taxonomy as built:** Bonspiel / Activities / Closure / Other (the original design's "Tournament" was renamed to "Activities" during build, and "Other" was added). Kept structurally separate from the sheet-level category enum.

**Display:** Club Events render **inline within the calendar itself**, never as a separate banner — sorted chronologically alongside sheet bookings (all-day events sort first). In Month view they appear as chips within each day cell; in Week and Day view (both hourly grids, §4.7) an all-day event pins to a slim row at the top of its column, and a timed event renders as a full-width band positioned at its actual hour, the same as a sheet booking. Every club event chip/band gets a dotted border (as opposed to bookings' dashed=hold/solid=confirmed), so the border style alone identifies what kind of calendar item something is, independent of its category color. Clicking a Club Event chip anywhere on the staff calendar opens its edit form directly (a bug where the click instead bubbled up to the day cell's own "jump to Day view" handler was found and fixed). A "Show club events" toggle lets staff hide them from the calendar entirely.

**Integration with sheet bookings — narrower than originally designed:**

| Mechanism | Behavior |
|---|---|
| Write-path conflict check | **Narrowed after build** from the original "no cross-check in either direction" (D13): a Club Event flagged `MarksSheetsUnavailable=true` now *is* cross-checked against new sheet bookings/series, since staff live-testing surfaced this as a real gap. Implemented at the `Calendar.razor` page level (not inside either service — `SheetBookingService`/`ClubEventService` stay mutually decoupled, per D13's original build-simplicity intent). Blocking for a single booking's create/edit (same UX as a real sheet conflict); informational-only for the series wizard preview (staff can still choose to skip a flagged date, matching how every other series-preview conflict already works). Club-Event-vs-Club-Event and non-closure-Club-Event-vs-sheet-booking checks remain intentionally absent. |
| Public view | Club Events get a distinct label on the public calendar rather than being folded into generic per-sheet blocks. |
| Provisioning | Add as step 1a: create the Club Events resource mailbox alongside the sheet mailboxes; same security group/access policy scope. |

### 4.5 Recurring Series and Multi-Sheet Bookings

Two related mechanisms added during build, beyond the original design's scope:

**Multi-sheet bookings.** A single conceptual booking (e.g., a rental spanning 3 sheets) is represented as one event per sheet, linked by a shared `BookingGroupId` (a named extended property) — every booking gets one, even single-sheet ones, so downstream code never branches on single-vs-multi. Creation across sheets is all-or-nothing: every requested sheet is conflict-checked before anything is written.

**Recurring series.** Graph has no concept of a recurring series spanning multiple mailboxes, so a multi-sheet recurring booking (e.g., a 5-sheet league) is five independent native Graph recurring series — one per sheet — sharing one `BookingGroupId`. Conflicts during series review are informational only, never auto-skipped; staff explicitly choose which candidate dates to skip.

**A real bug found and fixed via live testing:** `BookingGroupId` does **not** reliably propagate from a recurring series' master event down to its individual occurrences — it only persists on an occurrence once that specific occurrence has been individually edited. An untouched occurrence always reads back `BookingGroupId = Guid.Empty`. Naively grouping chips by `BookingGroupId` in Month/Week view therefore incorrectly merged multiple *unrelated* bookings that happened to share that same empty default, hiding all but one. Fixed by falling back to `(SheetMailbox, EventId)` — always unique per booking — as the grouping key whenever `BookingGroupId == Guid.Empty`. Diagnosed via a differential test: Day view (which doesn't group by `BookingGroupId` at all) showed the data correctly, isolating the bug to the dedup/display layer rather than the fetch.

### 4.6 Configuration Model (Phase 10)

Added during production hardening. Nothing tenant-specific is hardcoded in source:

- **`FacilityOptions`** (bound from a `Facility` config section, same pattern as the pre-existing `GraphOptions`): `TenantDomain`, `SheetMailboxLocalParts` (an explicit array, not a count — a different facility's mailboxes won't necessarily follow a `sheet1..sheetN` naming convention), `ClubEventsMailboxLocalPart`, `TimeZone`, plus `Name` and `LogoPath` (accepted now for future white-labeling; not yet wired into any UI).
- **`FacilityConfiguration`** — a singleton service that validates and derives the actual mailbox addresses/`TimeZoneInfo` from those options. Fails fast at application startup (not lazily on first request) if `TenantDomain`, `SheetMailboxLocalParts`, or `TimeZone` are missing — deliberate, given this app has already shipped two real bugs from silent wrong timezone defaults; a misconfigured deployment should error immediately, not limp along wrong.
- `GraphOptions` (the Entra app-registration credential: `TenantId`/`ClientId`/`ClientSecret`) remains a separate config concern, unchanged.
- The provisioning script (`docs/provision-categories.ps1`) takes `-TenantDomain` and `-SheetCount` parameters rather than hardcoding either.

See `docs/deployment-guide.md` for the full configuration reference and where each value belongs (user-secrets locally, Azure App Service Application Settings or equivalent in production).

### 4.7 Week and Day Views — Hourly Grids (Phase 10)

Both Week and Day are hourly time-grids sharing one time axis (`CalendarStyles.HourRows`, midnight through midnight — full 24-hour coverage, matching the 24-hour booking window in §4.2), rendered against a common set of positioning helpers (`TopPx`/`HeightPx`) so the two views can never drift out of sync with each other. Day view has one column per sheet; Week view has one column per day.

**Consolidation across sheets (Week view).** A single conceptual multi-sheet booking (one event per sheet, linked by `BookingGroupId`, §4.5) collapses into one displayed item rather than showing once per sheet, with a `· N sheets` suffix when the group spans more than one. This reuses the same `BookingGroupId`/`(SheetMailbox, EventId)` dedup key already established for Month view (§4.5's `BookingGroupId` propagation bug).

**Lane layout for concurrent items (Week view).** Because Week's columns are per-day (not per-sheet), two different bookings on two different sheets at overlapping times land in the same column. A classic calendar-view lane algorithm — cluster overlapping items, then greedily assign each to the first lane whose prior occupant has already ended — lays them out side-by-side instead of overlapping, with each cluster's own lane count (not the whole day's busiest moment) determining item width. Implemented once as a generic `CalendarStyles.LayoutLanes<T>` and reused verbatim by the public calendar's Week view (§5.4.2), rather than each maintaining its own copy of the same algorithm.

**A live-found rendering bug (2026-07-23):** the hour-label gutter's cells didn't set `box-sizing: border-box`, so a 2px top padding was added on top of the declared row height rather than included within it — a 2px-per-hour drift between the labels and the actual grid rows that compounded to a full row's offset by mid-afternoon. Fixed by adding `box-sizing: border-box` to every gutter cell (and the Week view's day-header row, which had the same class of issue from its own padding/border). A reminder that every fixed-height cell sharing a coordinate system with pixel-computed absolute positioning must be box-sizing-consistent, not just individually correctly sized.

**Every calendar cell title (Month/Week/Day, and the public calendar, §5.4) is prefixed with its start time** (e.g. `7PM - League Practice`, or `7:30PM - …` when not exactly on the hour) — added so the time is visible without needing to click a chip, even in Month view where nothing else conveys time-of-day. All-day Club Events are the one exception (no specific hour to show).

### 4.8 External Booking-Platform Integration (Breely webhook, Phase 10)

Bookings taken through Breely (a separate, third-party booking site with its own calendaring, used for public-facing group-event sales) are not entered by staff into this app directly. Breely is the source of truth for what a customer was actually promised (§2.3) — this app's copy is a best-effort, one-way reflection, kept current so staff have a working calendar without needing to also watch Breely, but not relied upon for the authoritative answer to "is this customer actually booked." A real bidirectional sync was the originally preferred design (§2.2) but wasn't ready in time; this webhook is the stopgap that ships instead, and becomes redundant if/when that sync exists.

**Trigger and identity.** Breely fires one webhook call per sheet-booking event, not one per purchase — a multi-sheet group reservation is several independent calls. Each call carries a stable `event.id` that persists across a reschedule (confirmed empirically against real reschedule samples during Phase 10 payload analysis — the same `id`, new `start_date`/`start_time`). Since there is no companion database (D7), "have I seen this external booking before" is answered by storing `breely:{event.id}` in a new extended property, `ExternalBookingId` (same named-extended-property pattern as `BookedBy`, §4.1), and querying for it live via a Graph `$filter` across every configured sheet (`SheetBookingService.FindByExternalIdAsync`) rather than any local index. The value is validated against a strict allow-list (`^[A-Za-z0-9:_-]+$`) before ever being embedded in a Graph filter string — the id is webhook-controlled input, and Graph's `$filter` syntax has no parameterization to fall back on.

**"Dumb webhook" design philosophy.** By the time this fires, the booking already happened in the real world — the job is to reflect that, never to reject or drop it. Concretely:
- Always acknowledge fast (HTTP 200), even on malformed JSON, an unrecognized shape, or a processing exception — there is no retry semantics this app controls either way, and a non-2xx wouldn't cause Breely to do anything more useful. Failures are surfaced via logs and the `NeedsTriage` marker below, never via the HTTP response.
- If a new booking doesn't match any existing open Group Event hold on any sheet (a customer bought a slot the public search never advertised, or Breely and this calendar's holds have drifted), the booking is force-written anyway (`ForceCreateConfirmedAsync`, bypassing the normal conflict check entirely) onto the first configured sheet, and a `NeedsTriage` Club Event marker (title `⚠ Web booking needs review`, non-blocking, `MarksSheetsUnavailable: false`) is created for staff to reassign manually. A real booking is never silently guessed at or dropped.
- Sheets are always tried in configured order (`Facility.SheetMailboxes`, i.e. sheet 1, then sheet 2, …) when claiming a hold or falling back — satisfies the operator's explicit "always assign sheets in numerical order" requirement without any extra sorting logic.

**Hold-claiming, not hold-blocking.** Every existing staff-facing write path treats a Group Event hold as something a new booking must avoid overlapping. This integration instead treats a hold covering the requested window as *claimable*: `SheetBookingService.ClaimHoldAsync` walks sheets in configured order, locks one sheet at a time (same per-sheet `SemaphoreSlim`, §6.1), finds a hold that fully covers the requested window, converts it to a Confirmed booking carrying the customer's name/phone/email and `ExternalBookingId`, then trims the remainder of the original hold (`TrimHoldAsync`) rather than deleting it outright — so a 10am–2pm hold claimed for a 10am–12pm booking still leaves 12pm–2pm open, and a hold covering multiple sheets still shows the others as available. Trimming deletes the hold (zero remainder), patches it in place (one remainder), or splits it into two events (two remainders); a hold that's a recurring occurrence is deleted and recreated as a standalone event for its remainder rather than PATCHed, since Graph rejects a Start/End change on an occurrence that would cross into an adjacent occurrence (the same restriction already known from `UpdateGroupAsync`, §5.1).

**Reschedule and cancellation.** A reschedule notification (same `event.id`, new time) is handled as cancel-then-reclaim: the existing booking is released back to an open Group Event hold (`CancelGroupAsync(reopenAsGroupEventHold: true)`), then the new window is claimed fresh via the same `ClaimHoldAsync` path above — which may land on a different sheet than before if the original sheet isn't free at the new time; that's expected. A cancellation notification releases the matching booking the same way, or is logged and ignored if no matching booking is found. **Cancellation is unverified against real Breely traffic as of this writing** — the operator has not yet cancelled a real booking through Breely, so this path has only been exercised against synthetic/sample payloads (§8).

**Payload handling.** Breely's real webhook payload is large and mostly irrelevant to this app (CRM/marketing fields, signed-PDF blobs, raw form-answer dumps); `BreelyBookingProcessor`'s DTOs (`BreelyEvent`/`BreelyWebhookPayload`) map only the handful of fields actually used (`id`, `start_date`, `start_time`, `duration_in_minutes`, `booked_with`, `canceled`, client contact fields, `event_type`, `admin_url`) via `[JsonPropertyName]`; everything else is silently ignored by `System.Text.Json`, not an oversight. `start_date`/`start_time` are parsed as facility-local time (`MMM d, yyyy h:mmtt`) — the "PDT"/"PST" abbreviation Breely also sends is deliberately ignored, since the facility's own configured time zone (§4.6) is the authority on local time here, matching how the rest of the app already treats `DateTime` as local-without-offset. An event whose `booked_with` isn't the configured sheet resource-type string (currently `"Curling Sheet"`, hardcoded — Breely has no API for this app to discover the name on its own) is ignored as not applicable to this calendar (e.g. a warm-room add-on, if Breely ever sends one as its own top-level event).

**Payload shape was reverse-engineered, not documented.** Breely's own webhook documentation was too sparse to build against directly. The actual shape was determined empirically during Phase 10 from a series of real captured payloads (a first booking, a reschedule pair, and a corrected single-sheet sample after an earlier sample turned out to be manually-edited and unreliable) via the diagnostic capture listener described in §5.5 — kept in place after this integration shipped, in case the payload shape needs re-inspecting for some future Breely change.

### 4.9 Application Activity/Debug Log and Settings Page (Phase 10)

Added immediately after the Breely webhook (§4.8) shipped, once the operator found its production behavior opaque: the framework's own `ILogger` output only reaches the console/Azure Log Stream, isn't retained anywhere staff can see without portal access, and wasn't answering "what did the webhook actually do." `AppLogService` is a second, deliberately separate log aimed at that gap — a flat rotating text file, not a database (same D7 spirit as the rest of this app's data model), with a staff-facing **Settings** page (`/settings`) to control it and read it.

**Two tiers, one on by default.** *Standard* entries are definitive actions — a booking, series, or Club Event created, edited, or canceled — and are always written, along with a small set of security-relevant events (a failed webhook-secret check) that matter regardless of level. *Debug* entries — the raw (PII-redacted) Breely webhook payload, the external-id lookup result, staff sign-in events — are a no-op unless the level is currently set to Debug. The level is chosen on the Settings page, takes effect immediately (no restart), and persists to a small marker file in the log directory itself rather than to `appsettings.json`, so a level change survives an app restart without needing a redeploy.

**Actor identity reuses the existing sign-in trust boundary, not a new one.** Every Standard-tier staff action logs the real signed-in Entra display name (`ClaimsPrincipal.Identity.Name`) — the same value already shown in the header and defaulted into the free-text "Booked By" field (§6.2) — rather than that editable free-text field itself, since a typed field isn't a reliable audit identity. Breely-originated actions log the actor as the literal string `"Breely webhook"`, matching the `BookedBy` value those bookings already carry.

**Storage location is deliberately outside the deployed app folder.** `AppLog:LogDirectory` (configuration, §4.6-style — never hardcoded) points at where daily-rotating files (`app-yyyy-MM-dd.log`) and the level marker live. Left unset, it falls back to `App_Data/logs` under the content root and logs a startup warning — adequate for local dev, but on Azure App Service that folder is replaced by every redeploy/zip-deploy, silently losing log history. Production deployments must point this at a persistent path outside the deployed folder (see the deployment guide). Files older than `AppLog:RetentionDays` (default 30) are deleted automatically on the next day's rotation — a deliberate bound so leaving Debug mode on doesn't grow the log without limit.

**PII handling in Debug-tier webhook logging.** Breely's payload carries the customer's name, email, and phone number. Logging it raw in Debug mode would create a second at-rest copy of customer contact information outside Exchange, for as long as retention keeps it — decided against explicitly with the operator. `BreelyBookingProcessor`'s Debug-tier payload log redacts those three fields (`client_full_name`/`client_email`/`client_phone` → `[redacted]`) while keeping everything else (booking id, times, sheet, `admin_url`, event type) intact, so the log stays useful for troubleshooting a payload-shape question without becoming a PII store.

**"All network traffic" was scoped down from its literal reading.** The original request for Debug mode was "all network traffic, authorizations, webhook calls, webhook actions." Literal HTTP-level tracing of every Microsoft Graph call would have meant hooking into the Graph SDK's HTTP client pipeline (Kiota's `IRequestAdapter`/`DelegatingHandler` plumbing) — riskier to get right without the ability to test locally against real Graph traffic (per the same constraint that shaped §4.8's build), and it would flood the log with routine calendar-page reads that have nothing to do with what an operator is actually trying to debug. What shipped instead: every step of the Breely webhook's own processing (payload received, external-id lookup, hold-claim attempt, force-book fallback), plus staff sign-in events. If Debug mode turns out not to show enough once exercised against real production traffic, this is the boundary to revisit first.

**Log viewer and download.** The Settings page shows the most recent 500 lines (`AppLogService.TailAsync`, walking backward through older rotated files if the current day's file doesn't have 500 lines on its own) with a manual Refresh button — not auto-refreshing, consistent with every other view in this app being a simple request/response read rather than a live-updating one. `GET /settings/logs/download` (§5.6) zips every rotated file for download, since a support conversation shouldn't be limited to "whatever's in today's file."

**A related live-found fix, surfaced by testing this feature.** Cancelling a booking (`SheetBookingService.CancelAsync`/`CancelGroupAsync`) could throw an unhandled `ODataError` ("The specified object was not found in the store," Graph's 404) if the target event no longer existed by the time the delete/patch ran — crashing the entire Blazor circuit, the same failure mode already on record elsewhere in this app (§8). Live-hit 2026-08-03: the most likely cause is the Breely webhook (§4.8) claiming or trimming a hold out from under a staff browser tab that had loaded it moments earlier and was now stale. Fixed by tolerating a 404 on cancel/reopen as "already gone, treat as already-cancelled" — the exact pattern `CancelSeriesAsync` already used for a missing series master, just not previously applied to the plainer single/group cancel paths (D37).

---

## 5. API Interactions

### 5.1 Graph Operations by Use Case

| Operation | Graph call | Notes |
|---|---|---|
| Per-sheet/consolidated calendar detail | `GET /users/{sheet}/calendarView?startDateTime=…&endDateTime=…` + `$expand` | `calendarView` (not `/events`) so recurrences expand into occurrences. Paginated — every read path follows `@odata.nextLink` until exhausted (a real bug: a wide Month-view window with several expanded recurring series could exceed one page, silently truncating results if only the first page was read). |
| Create booking (single or multi-sheet) | `POST /users/{sheet}/calendar/events` | Direct write; preceded by an app-side conflict check under a per-sheet lock (§6.1), across every requested sheet, all-or-nothing. |
| Create recurring series | `POST /users/{sheet}/calendar/events` + `Recurrence` | One native recurring series per sheet, per §4.5. |
| Confirm hold / edit booking | `PATCH /users/{sheet}/events/{id}` | Occurrence PATCHes must omit `Start`/`End` entirely unless the time actually changed — Graph rejects a resent-but-unchanged time on a recurring occurrence with "Modified occurrence is crossing or overlapping adjacent occurrence." |
| Cancel booking / series | `DELETE /users/{sheet}/events/{id}` (occurrence) or `.../{seriesMasterId}` (whole series) | Whole-series cancel is a deliberately de-emphasized "backdoor," not a primary UX path. |
| Category palette setup (one-time) | `GET/POST /users/{sheet}/outlook/masterCategories` | Provisioned via `docs/provision-categories.ps1`, parameterized by tenant domain and sheet count (§4.6). |

Timezone rule for every read/write: pass `Prefer: outlook.timezone` (reads) and tag `Start`/`End`/`RecurrenceTimeZone` (writes) with the facility's configured zone (§4.6) — never assume UTC. A distinct, separately-discovered gotcha: `calendarView`'s `startDateTime`/`endDateTime` **query parameters** are always interpreted as UTC when no explicit offset is present, and are *not* reinterpreted by the `Prefer` header the way an event body's own `Start`/`End` are — query bounds must be converted to a true UTC instant first (`FacilityConfiguration.ToUtcQueryString`).

### 5.2 Booking Creation (write path)

Unchanged in spirit from the original design: validate → acquire a per-sheet lock (sorted lock order across every requested sheet, to avoid deadlock on overlapping multi-sheet requests) → `calendarView` conflict check on every requested sheet → write only if every sheet is clear → invalidate the staff-facing view cache → release locks. All-or-nothing across sheets, not just per-sheet.

Added after build: the same page-level check also queries for an overlapping `MarksSheetsUnavailable` Club Event before writing (§4.4) — blocking for a direct create/edit, informational for series preview.

### 5.3 Consolidated Availability — Delivered as `/public/search` (Phase 10)

The originally-designed "≥N sheets available for rental" interval-merge view (R6) was deprioritized during the initial build and explicitly struck from the active plan, pending real feedback. That feedback arrived in Phase 10, and the view was built as `/public/search` (§5.4.3) rather than folded into `/public/calendar` - a dedicated search page, not a permanent calendar overlay.

### 5.4 Public Views (anonymous read path) — three distinct surfaces

**5.4.1 JSON availability API + CMS embed widget** (`/api/public/availability`, `/embed/availability-widget.js`) — a subordinate feature. "Available" here means an existing Group Event+Hold booking (the same "open for group event" slots staff already create), not raw free/busy — simpler than computing complementary free time, and more correct, since unbooked League/Bonspiel/Practice Ice time isn't necessarily something staff want the public booking. Excludes any window overlapping a `MarksSheetsUnavailable` Club Event. Rate-limited (fixed window, 60/min) and CORS-scoped (`AllowAnyOrigin`, GET-only) — safe specifically because this data is intentionally public and anonymous, no cookies/credentials ever flow through it.

**5.4.2 Public calendar** (`/public/calendar`) — the *primary* way club members see what's going on club-wide while unauthenticated. Three views, matching the staff calendar: Month (the default), Week, and Day, selected via `?view=`, with `?month=` (Month) or `?date=` (Week/Day) clamped to a bounded window around today (a year back, two years forward) — unclamped, `DateTime.TryParse` accepts ~120k distinct values, and every unseen month/week/day is a cache miss fanning out live Graph calls across every mailbox, an anonymous quota-exhaustion/cache-growth vector (found and closed for Month in the Phase 10 security review; the same clamp now applies to `?date=` too). Shows every category and state, with titles (a league's own name, a renter's own chosen title) prefixed with the start time (§4.7) — not just group-event-availability slots. The one deliberate privacy exception: a confirmed booking's actual renter name is not stripped programmatically; staff are expected to handle that via what they type into the title field, not the app. Chips are clickable, opening a small popup with the exact time (the month grid itself doesn't show hour-level detail; Week/Day already do via the hourly axis). Club Event chips share the same dotted-border visual language as the staff calendar (§4.4). Week and Day are hourly grids reusing the exact same hour-axis math and lane-layout algorithm as the staff Week/Day grids (`CalendarStyles.LayoutLanes`, extracted as a shared generic method specifically so the staff and public grids can't drift apart). Since every view/date change here is a full server-rendered page reload (no client-side routing, consistent with this endpoint never touching the Blazor component tree), a "Loading…" overlay appears immediately on any nav-link click - added after a live UX gap where the page gave no visible feedback while the server computed the next view. Iframe-embeddable; currently has **no `Content-Security-Policy: frame-ancestors` restriction** (documented, deliberate, revisit once the public surface gets more real-world scrutiny).

**5.4.3 Availability search** (`/public/search`) — finds date/time windows where at least N sheets have an open Group Event hold simultaneously (R6, §5.3). A form (date range, capped at 60 days span, plus the same ±1yr/+2yr window as the other two endpoints; a minimum-sheets dropdown sized to the tenant's actual configured sheet count, never hardcoded) followed by a results list, each entry linking to that day's `/public/calendar?view=day`. Computed with a two-pass interval algorithm in `PublicAvailabilityService`: merge each sheet's own open slots into that sheet's maximal contiguous blocks, then sweep across every sheet's blocks counting how many are open at each point in time, reporting contiguous stretches meeting the threshold. Reuses `GetOpenSlotsAsync` - the same open-slot computation `/api/public/availability` uses, including the per-sheet-overlap-subtraction correctness fix below - so a window is never reported as available if it isn't genuinely open. Cross-linked with `/public/calendar` (a link each way in the header).

**A live-found correctness bug in the open-slot computation (2026-07-28):** `GetOpenSlotsAsync` reported a Group Event hold's own advertised Start/End as fully open without checking whether *another* booking existed on the same sheet overlapping part of that window. The app's own write-path conflict check should prevent that overlap from ever being created through the app, but that invariant doesn't protect data written outside it (direct Graph/Outlook writes, seeded test data) - and the public feed must never promise ice that's actually occupied regardless of how the conflicting data got there. Found via a hold that fully covered a separately booked, confirmed League game on the same sheet, which `/public/search` then reported as available across its full advertised window. Fixed by subtracting every other overlapping booking on the same sheet out of each hold's window (0, 1, or 2 remaining open sub-ranges as needed) before reporting it - this fixes both `/public/search` and `/api/public/availability`, since both share `GetOpenSlotsAsync`.

**The architectural incident behind all four surfaces' final shape:** an earlier attempt to make public-calendar chips clickable used Blazor Server's own `@onclick`, which requires the interactive SignalR circuit — and a first fix attempt (`.AllowAnonymous()` applied to the shared `MapRazorComponents<App>()` registration) was live-tested and found to **disable authorization for every staff page in the app**, not just the intended public one, because ASP.NET Core's authorization rule is "if `AllowAnonymous` metadata is present anywhere on an endpoint, it wins" and `MapRazorComponents<App>()` maps every routable component through one shared endpoint set. Reverted immediately. A second attempt (vanilla JS instead of Blazor event handlers) fixed the clicks but still showed an unremovable "unhandled error" banner for anonymous visitors, traced to the shared host shell (`App.razor`) always loading `blazor.web.js` regardless of the specific page's render mode. The user explicitly rejected hiding the error banner rather than fixing the actual cause. **The real fix, and the standing rule for any future anonymous page (including the write-capable one in §5.5):** every public route is a plain Minimal API endpoint (`Endpoints/PublicAvailabilityEndpoints.cs`, `Endpoints/PublicCalendarEndpoint.cs`, `Endpoints/PublicSearchEndpoint.cs`, and now `Endpoints/BreelyBookingWebhookEndpoint.cs`), each with its own explicit `.AllowAnonymous()`, hand-building their response outside the Blazor component tree entirely — zero shared circuit, nothing for anonymous traffic to be rejected from. Every public page's HTML is hand-built via `StringBuilder`, with every dynamic string passed through `WebUtility.HtmlEncode` (no Razor auto-escaping to fall back on, and titles are staff-entered free text — a real stored-XSS risk if skipped).

### 5.5 Booking Webhook Endpoint (Phase 10 write surface)

`POST /api/webhooks/breely` (`BreelyBookingWebhookEndpoint`) is the one anonymous endpoint that writes rather than reads (§1, §4.8). Contract:

- **Auth:** a static shared-secret header, `X-Webhook-Secret`, compared against `Webhook:BreelySharedSecret` (configuration, §7) using `CryptographicOperations.FixedTimeEquals` (constant-time, avoids a timing side-channel on the comparison). A missing/unconfigured secret or a mismatch returns `401`. This is a materially weaker guarantee than an HMAC-signed body — a leaked secret is reusable indefinitely rather than scoped to one request — but it's what Breely's own webhook configuration actually supports (a fixed URL, static custom headers, and a body; no per-request signature capability, confirmed empirically since Breely's documentation didn't cover it).
- **Body:** `{ "event": { ... } }`; only the subset of fields `BreelyEvent` maps (§4.8) is read. Malformed JSON or a missing `event` object is logged and acknowledged (`200`), not rejected.
- **Response:** always `200` once past the secret check — including on a processing exception — per the "dumb webhook, never reject a real booking" philosophy (§4.8). The HTTP response is not the failure-signaling channel; server logs and the `NeedsTriage` Club Event marker are.
- **Rate limiting:** its own fixed-window limiter (`booking-webhook`, 30/min), separate from the `public-api` limiter the three read surfaces share — a flood aimed at one shouldn't starve the other.

**Diagnostic capture listener, still present alongside the real endpoint.** `POST /api/webhook-capture/{token}` (`WebhookCaptureEndpoint`/`WebhookCaptureService`) predates §4.8 and was built to reverse-engineer Breely's undocumented payload shape before the real integration could be written against it: it accepts any JSON body behind a static path token (`Webhook:CaptureToken`), stores the raw payload in memory, and exposes it for viewing at `/diagnostics` (staff-authenticated). It performs no processing and touches no calendar data — zero blast radius by design. It has been superseded by `/api/webhooks/breely` for real traffic but is deliberately kept registered, since a future Breely payload-shape change would need the same capture-and-inspect workflow again.

### 5.6 Log Download Endpoint (Settings page support, §4.9)

`GET /settings/logs/download` (`SettingsLogsEndpoint`) is the one staff-facing surface built as a plain Minimal API endpoint rather than a Blazor page — the same "raw HTTP semantics don't fit the SignalR circuit" reasoning as the public endpoints (D15), just gated by the app's default authenticated fallback policy instead of `.AllowAnonymous()`, since this one isn't meant to be public. It zips every rotated log file in `AppLog:LogDirectory` (§4.9) into a single in-memory archive and returns it as `application/zip` — small enough at this app's log volume to build in memory rather than streaming to a temp file. `404` if no log files exist yet (e.g. immediately after a fresh deploy with nothing logged).

---

## 6. Identity, Security, and Permissions

### 6.1 Conflict Enforcement — Why the App Owns It

Unchanged: the Resource Booking Attendant only processes meeting requests, and this app writes events directly — the attendant never runs, and Exchange accepts overlapping events. Confirmed via spike, not just documentation. Direct writes + application-owned conflict enforcement (validate → lock per sheet → check → write) is trivially safe at the 1–2-user concurrency profile; the Phase 7 cache is deliberately scoped so it can never weaken this (§4.3).

### 6.2 Identity Model

| Principal | Mechanism | Used for |
|---|---|---|
| Staff (interactive) | Entra ID SSO, identity/audit only | Booking create/edit/delete from the UI. Graph itself stays on the app-only credential below, not a delegated on-behalf-of flow — deliberately, to avoid per-request token acquisition complexity for a benefit (native Exchange attribution) the design accepted skipping. As of Phase 10 (§4.9) this same identity is also the "actor" recorded on every Standard-tier activity-log line for a staff action, reusing this trust boundary rather than introducing a new one. |
| App service identity | Client credentials → application permissions | All Graph reads/writes, including the public endpoints' data source. |
| Staff (fallback viewing) | Reviewer (read-only) calendar permission | Opening sheet calendars in Outlook/OWA. |
| Anonymous public (read) | None — never touches Graph directly | Served only by the three read-only plain Minimal API endpoints (§5.4), through the app's own service layer. |
| Anonymous external system (write) | Static shared-secret header (§5.5) | Breely's booking webhook (§4.8) — the one anonymous caller that writes to the calendar, distinguished from the identities above by carrying its own bearer-style secret rather than relying purely on route isolation. |

### 6.3 Scoping the App Identity (mandatory, not optional)

Unchanged: sheet + Club Events mailboxes live in a dedicated mail-enabled security group; the app registration is constrained to that group via Application Access Policy or RBAC for Applications; negatively tested (verify the app identity is denied access to a mailbox outside the group).

### 6.4 Other Security Requirements

- No secrets in code or plaintext config — user-secrets locally, Azure App Service Application Settings (or equivalent secret-injection mechanism for another host) in production. See `docs/deployment-guide.md`.
- The three original public endpoints remain read-only, minimized (JSON API) or hand-encoded (public calendar, search), rate-limited, CORS-scoped only to those routes. As of Phase 10 they are no longer the app's *only* internet-anonymous surface: the Breely booking webhook (§4.8/§5.5) is a fourth anonymous endpoint, deliberately write-capable, gated by its own static shared secret rather than by being read-only — a bounded, explicit exception, not a broadening of the general rule.
- **The webhook's static-secret auth is the weakest credential in this app's security posture, accepted deliberately.** Unlike an HMAC-signed webhook, a leaked `BreelySharedSecret` is reusable indefinitely rather than scoped to one request or time window. Accepted because Breely's own webhook configuration has no capability to compute a per-request signature (confirmed empirically); mitigated by the endpoint's own rate limiter (§5.5), the "dumb webhook" design never trusting the payload's shape blindly, and the fact that a forged request can at most create a spurious booking or release a real one — both staff-visible and correctable, not a data-exfiltration or privilege-escalation risk.
- **A specific, live-verified gotcha:** never apply `.AllowAnonymous()` to `MapRazorComponents<App>()` itself — it disables authorization for every page in the app, not just an intended public one (§5.4). Any future anonymous page must be a plain Minimal API endpoint outside the Blazor component tree.
- Static assets (CSS/JS/images) are explicitly `.AllowAnonymous()`'d (`app.MapStaticAssets().AllowAnonymous()`) — safe, since they carry nothing sensitive, and necessary since the global `FallbackPolicy` otherwise gates every routed endpoint including these.
- Mailbox audit logging enabled on every resource mailbox.
- **The activity/debug log (§4.9) is its own security surface, not covered by the points above.** `AppLog:LogDirectory` should be a path only the app's own process account can read/write — the same "keep secrets out of anything world-readable" concern as everything else in this section, since a Debug-tier Breely log line still carries booking times/sheets/admin URLs even with customer PII redacted. `/settings/logs/download` is gated by the default authenticated fallback policy (§5.6), not `.AllowAnonymous()`, so it requires the same staff sign-in as every other page.
- **Phase 10 security review completed (2026-07-16).** Confirmed sound: the auth model above, XSS encoding discipline on both public surfaces, input clamping on `?days=`, CORS scoping, secrets posture, and the uncached conflict-check invariant (D16). Found and fixed: missing rate limiting on `/public/calendar`, the unbounded `?month=` quota-exhaustion vector (§5.4.2), and an `innerHTML` interpolation in the embed widget (admin-config data only, hardened to `textContent` as defense in depth since that script executes on the club's own website). Accepted with eyes open: the rate limiter is a single global 60/min bucket, not per-IP — stronger Graph-quota protection, but one abusive client can starve the widget for legitimate visitors; revisit with per-IP partitioning (plus forwarded-headers config) if real traffic warrants. `/diagnostics` remains reachable by any signed-in staff member — remove or admin-gate before members get accounts.

---

## 7. Tenant Provisioning Checklist (one-time + per-new-sheet)

1. Create resource mailboxes, one per configured sheet, plus one Club Events mailbox.
2. Create the mail-enabled security group for all facility mailboxes; add every mailbox.
3. `Set-CalendarProcessing` per mailbox — calendar hygiene (booking-policy enforcement is app-owned, not Exchange's).
4. Provision the master category list on every sheet mailbox (Group Event/League/Practice Ice/Other — see `docs/provision-categories.ps1` for the exact current list) and the Club Events mailbox (Bonspiel/Activities/Closure/Other) via `docs/provision-categories.ps1`, parameterized by `-TenantId`, `-ClientId`, `-TenantDomain`, `-SheetCount`. If a category was ever renamed after mailboxes were already provisioned (e.g. the Phase 10 Rental → Group Event rename), existing events keep the old literal category string until migrated — see `docs/migrate-rental-category.ps1`.
5. Entra ID app registration: delegated scope for staff SSO (identity/audit only), application scope for the service identity that does all Graph work.
6. Scope the application permissions to the security group (Application Access Policy or RBAC for Applications) and negatively test it.
7. Grant staff Reviewer (read-only Outlook fallback) permission per §6.2.
8. Enable mailbox audit logging.
9. Set the app's `Facility`/`Graph` configuration (§4.6) to this tenant's real values.
10. Repeat steps 1, 2 (membership), 4, 7 for every sheet added later.

See `docs/deployment-guide.md` for the full deployment process this checklist feeds into.

---

## 8. Risks, Limitations, and Resolved Verification Spikes

| Item | Status |
|---|---|
| Direct-write bypasses booking attendant | **CONFIRMED** via spike — validates D3/§6.1. |
| Category filter behavior in `calendarView` | **RESOLVED** — server-side `$filter` on categories works, better than the original design assumed. |
| Extended property size limits | **RESOLVED** — 4000-character values accepted without error. |
| `getSchedule` 62-day window cap | **CONFIRMED EXACTLY** at 62 days (not used by `/public/search`'s consolidated-availability view, §5.3/§5.4.3 - that view reads through the existing `calendarView`-based booking fetch, already paginated, rather than `getSchedule`; its own 60-day search-span cap is a separate, deliberate design limit, not related to this Graph-side cap). |
| `calendarView` pagination | **Found live, not a pre-build spike** — a wide Month-view window with several expanded recurring series can exceed one page; every read path now follows `@odata.nextLink`. |
| `BookingGroupId` propagation to recurring occurrences | **Found false via live testing** (§4.5) — only persists once an occurrence is individually edited; fixed via a dedup-key fallback. |
| Blazor circuit + anonymous pages | **Serious incident, resolved** (§5.4) — public pages are now plain Minimal API endpoints, never Blazor components sharing the staff circuit. |
| Category color consistency in Outlook shared-calendar views | Still open, low stakes, client-dependent; not automated. |
| Accidental deletion of active bookings | Recoverable-items window (~weeks) is the only net; accepted at this scale. |
| Resource mailbox licensing | Confirmed against the tenant's actual SKU during Phase 1. |
| Graph throttling | Non-issue at this scale; cache absorbs bursts. |
| Schema drift (categories, property names) | Mitigated by the sole-writer invariant + read-only staff Outlook access. |
| iframe embedding of the public calendar | No `frame-ancestors` CSP restriction — deliberate for now (simplicity over locking to a specific domain), documented as a future hardening candidate. |
| Missing end-after-start validation on booking/series/Club Event forms | **Found live, fixed (2026-07-23)** — none of the three forms checked that the end time came after the start; an inverted range reached Graph's calendar API unvalidated and the resulting unhandled error took down the Blazor circuit. Fixed by adding the check to each form's existing `CanSave`/validation-message pattern (§4.2). |
| Week/Day hourly grid label drift | **Found live, fixed (2026-07-23)** — the hour-label gutter's cells didn't set `box-sizing: border-box`, so padding was added on top of the declared height rather than included in it, drifting the labels out of alignment with the grid by a full row over the course of a day (§4.7). |
| Category rename leaves old data behind | Renaming a `BookingCategory` (Rental → Group Event) only changes what the app writes going forward — the literal string already stored in `categories` on existing events doesn't change on its own, since it isn't a foreign key into the master category list. A category rename after real data exists needs an explicit one-time migration (`docs/migrate-rental-category.ps1`), not a code change alone. |
| Public open-slot computation trusted a hold's advertised window blindly | **Found live, fixed (2026-07-28)** — `GetOpenSlotsAsync` reported a Group Event hold's full Start/End as open without checking for another overlapping booking on the same sheet. Fixed by subtracting every other same-sheet overlapping booking out of the hold's window before reporting it (§5.4.3). Affected both `/api/public/availability` and the new `/public/search`, since both share this method. |
| Day view could silently hide a booking | **Found live, fixed (2026-07-28)** — a sheet column rendered every booking on that sheet at full column width with no lane-splitting; two bookings genuinely overlapping on the same sheet meant the wider one completely painted over the narrower one. Fixed by lane-splitting within each sheet's column using the same `CalendarStyles.LayoutLanes` algorithm Week view already used across days (D26). |
| Breely webhook auth is a static shared secret, not HMAC | **Accepted, not a defect** — Breely's own webhook configuration has no capability to compute a per-request signature (§6.4). A leaked secret is reusable indefinitely; mitigated by the endpoint's own rate limiter and the bounded blast radius of a forged request (spurious/released booking, staff-visible and correctable). Revisit if Breely ever adds signed-webhook support. |
| `FindByExternalIdAsync`'s Graph `$filter` query is unverified against real production traffic | The query shape (`singleValueExtendedProperties/Any(...)` filtering on `ExternalBookingId`) was written against Graph's documented filter syntax but not live-tested at the time of this writing, since the operator can't test the webhook locally and testing against production risked writing spurious bookings. Verify on the first real Breely notification after deployment; if the filter doesn't match as expected, every booking would still get written (never silently dropped, per §4.8) but would land as a duplicate/force-booked `NeedsTriage` case rather than a clean upsert. |
| Breely cancellation path is untested against real traffic | The operator has not yet cancelled a real booking through Breely as of this writing, so `ProcessAsync`'s cancel branch (§4.8) has only been exercised against synthetic data. Verify on the first real cancellation. |
| This calendar's copy of a Breely booking can diverge from Breely's own record | By design (§2.3, §4.8) — Breely is the source of truth; this app's copy is best-effort. A missed/failed webhook, a manual Breely-side edit, or the untested paths above could all cause drift. Accepted because the operator confirmed one-off manual reconciliation is acceptable as long as the vast majority of bookings sync correctly; not a defect to "fix" so much as a standing operational reality until real bidirectional sync exists. |
| Cancelling a booking could crash the Blazor circuit on a 404 from Graph | **Found live, fixed (2026-08-03)** — `CancelAsync`/`CancelGroupAsync` let an unhandled `ODataError` ("specified object was not found in the store") propagate out of a Blazor event handler, taking down the whole circuit. Most likely cause: the Breely webhook (§4.8) claiming/trimming the exact hold a staff browser tab had loaded moments earlier, leaving that tab's view stale. Fixed by tolerating a 404 on cancel/reopen as "already gone" (D37) — the same pattern `CancelSeriesAsync` already used for a missing series master, extended to the plainer single/group cancel paths that didn't have it. |
| `AppLog:LogDirectory` unset falls back to a path inside the deployed app folder | Adequate for local dev; on Azure App Service that folder is replaced on every redeploy, silently losing log history with no error (§4.9). Deployments must set this explicitly to a persistent path — added to the deployment guide's config table and post-deploy checklist. |
| Debug-tier "network traffic" logging is scoped to the Breely webhook flow, not literal Graph HTTP tracing | **Accepted, not a defect** — hooking the Graph SDK's own HTTP pipeline was judged riskier to get right without local Graph testing, and would have buried webhook-specific detail under routine calendar-page reads. Revisit if Debug mode proves insufficient once exercised against real production traffic (§4.9). |

---

## 9. Design Decision Record

| # | Decision | Rationale |
|---|---|---|
| D1 | EXO resource mailboxes as system of record | Zero-infrastructure hosted store; native recurrence, free/busy, permissions/audit; Outlook as emergency fallback UI. |
| D2 | Custom web UI; Outlook read-only fallback | Custom views + contextual metadata Outlook can't serve; read-only staff access protects the sole-writer invariant. |
| D3 | Direct event writes; app-owned conflict enforcement | Resource Booking Attendant doesn't run on direct writes; invite-based flow is asynchronous and clunky for a staff UI. |
| D4 | `showAs` tentative/busy encodes hold vs. confirmed | Keeps free/busy and Outlook fallback semantically honest. |
| D5 | `categories` for booking type | Free-form strings validated by app discipline; own color mapping independent of Exchange's. |
| D6 | Metadata on the event itself: named extended properties + one JSON blob | Explicit constraint against adjacent datastores. |
| D7 | No companion database | Avoid fragility/complexity of a second authoritative store. |
| D8 | Ephemeral short-TTL cache; no webhooks | Sole-writer + read-only Outlook access means nothing out-of-band to catch. |
| D9 | Hard delete on cancellation | Audit/reporting explicitly out of scope. |
| D10 | Single Blazor Server deployment + thin CMS embed | Decouples booking operations from the website's failure domain. |
| D11 | Hand-built minimized public payload | Prevents accidental PII leakage; a deliberately separate mapping, never a reuse of the internal API with anonymous access bolted on. |
| D12 | Microsoft Bookings and Power Apps rejected | Bookings targets customer self-service; Power Apps trades away custom-view flexibility. |
| D13 | Club Events as a dedicated resource mailbox, no cross-conflict-check with sheet bookings | Atomic single-write vs. non-transactional multi-write. **Narrowed after build** (§4.4): closure (`MarksSheetsUnavailable`) events now are cross-checked against new sheet bookings/series; everything else about D13 stands. |
| D14 | .NET / C#, Blazor Server for the app | Explicitly specified by the operator for this project, independent of any other project's stack. |
| D15 | Public/anonymous pages are always plain Minimal API endpoints, never Blazor components | Established after a live-verified incident (§5.4) where sharing the authenticated Blazor circuit's endpoint registration either exposed every staff page or left an unfixable client-runtime error banner for anonymous visitors. Not a style preference — a hard rule for any future public page. |
| D16 | The ephemeral cache is scoped to view-rendering reads only, never conflict-check reads | A cached snapshot on the conflict-check path could mask a just-created booking and allow a double-booking within the TTL window — unacceptable given D3's whole premise. The two read paths are kept structurally separate in code specifically so this can't happen by accident. |
| D17 | Tenant/mailbox/timezone are configuration, never hardcoded | Added during Phase 10 once a real production tenant existed — the same deployed app must be repointable at a different tenant, or stood up fresh for a different facility, without a recompile (§4.6). |
| D18 | Week view rebuilt as an hourly grid (one column per day), matching Day view's layout, instead of a condensed chip list | Staff feedback: Week view needed the same hour-level detail Day view already had, not just a list of same-day items. Concurrent bookings on different sheets are laid out in side-by-side lanes rather than one-per-sheet columns, since Week's columns are per-day, not per-sheet (§4.7). |
| D19 | Club Events render inside the same hourly timeline as sheet bookings in Week/Day (full-width bands for timed events, a pinned row for all-day ones), not a separate banner; distinguished by a dotted border rather than a fourth color scheme | Keeps club events visually part of the calendar staff are already looking at rather than a detached list above it, while still being unmistakably a different kind of item from a sheet booking at a glance (§4.4, §4.7). |
| D20 | Booking/series/Club Event time pickers and the Week/Day grids cover the full 24-hour day, not a fixed daytime window (previously 6 AM–11 PM) | Staff feedback: some legitimate ice time falls outside the originally-assumed daytime window. |
| D21 | Category display label kept separate from the category's Graph wire value (`CalendarStyles.CategoryLabel`) | Renaming `BookingCategory.Rental` to `GroupEvent` and adding `PracticeIce` needed human-readable multi-word labels ("Group Event", "Practice Ice") without touching the literal string round-tripped through `Enum.TryParse` against Graph's `categories` property — decoupling the two means a future label rename is presentation-only. |
| D22 | Public calendar gained Week and Day views, matching the staff calendar's three-view structure | Member feedback: the public calendar only offered Month, giving no hour-level detail at all. Reused the staff Week/Day grids' hour-axis math and lane-layout algorithm exactly (rather than a second implementation) via the shared `CalendarStyles.LayoutLanes<T>` (§4.7). |
| D23 | A JS "Loading…" overlay was added to the public calendar's nav links | The public calendar is deliberately plain server-rendered HTML with full-page navigation, not a client-routed app (§5.4) — but that left date/view changes with no visible feedback while the server computed the next view. The overlay is shown on click and is simply replaced along with the rest of the DOM once the new page arrives; it doesn't change the underlying full-page-reload architecture. |
| D24 | The "Open ice only" staff calendar filter was removed | User feedback: it was unclear and confusing (it silently overrode the category chips rather than combining with them). Filtering by category chips alone remains; anyone wanting "open Group Event holds only" can already get that by toggling every other category chip off. |
| D25 | Month/Week/Day staff grids no longer cap their own width | User feedback: a `max-width` (1100px Month/Week, 1000px Day) on each grid left a large empty gap on wide windows even though the toolbar above already spanned full width. Replaced with a `min-width` floor instead (900px Month/Week, 800px Day) so the grids fill available width but don't get crushed on a narrow one; cell/text sizing was bumped up slightly to match. |
| D26 | Day view's per-sheet columns lane-split overlapping bookings, reusing `CalendarStyles.LayoutLanes` | Live-found: every booking on a sheet was rendered at that column's full width with no lane-splitting, so two bookings genuinely overlapping on the same sheet (the app's own conflict check should prevent this via the app, but doesn't protect data written outside it) meant the wider one completely painted over the narrower one - a real booking silently disappeared from the view. Week view already had this via the lane algorithm (D18); Day view never needed it under the "sheets can't self-overlap" assumption, which turned out not to hold for all data. |
| D27 | A diagnostic webhook capture listener (`/api/webhook-capture/{token}`) was built and shipped before the real Breely integration, and kept afterward | Breely's own webhook documentation was too sparse to build against directly; the real payload shape needed to be observed empirically first. Kept in place post-integration (superseded for real traffic, not deleted) in case a future Breely change requires re-inspecting the payload shape again (§5.5). |
| D28 | The Breely integration is an inbound webhook that upserts by `event.id`, not the originally-scoped bidirectional-sync design | A real two-way sync wasn't going to be ready in time; the operator explicitly deprioritized it once webhook-based one-way sync was confirmed sufficient, given Breely (not this calendar) is the true source of truth for customer-facing availability and occasional manual reconciliation is acceptable (§2.3, §4.8). |
| D29 | Booking claims a hold rather than blocking on it (`ClaimHoldAsync`), and trims the hold's remainder instead of deleting it outright | Every other write path treats a hold as something new bookings must avoid; a Breely booking is *of* the hold it was sold against. Trimming (delete/patch/split, §4.8) preserves the operator's requirement that a partial claim (e.g. 2 of 4 hours, or 1 of several sheets) still leaves the rest visibly open rather than clearing the whole hold. |
| D30 | Sheets are claimed in `Facility.SheetMailboxes` configured order, with no additional sorting | Explicit operator requirement: "when assigning sheets for rentals, always go in numerical order (first is sheet 1, second is sheet 2, etc.)" — already satisfied by the existing configuration order (§4.6), so no new sorting logic was needed. |
| D31 | An unmatched Breely booking is force-written (bypassing the conflict check) rather than rejected or queued | Consistent with the "dumb webhook, never drop a real booking" philosophy (§4.8) — the booking already happened in the real world regardless of what this calendar's holds show. Paired with a non-blocking `NeedsTriage` Club Event marker so staff can find and reassign it, rather than silently accepting a possibly-wrong sheet assignment. |
| D32 | Breely webhook auth uses a static shared secret compared with `CryptographicOperations.FixedTimeEquals`, not an HMAC signature | Breely's webhook configuration only supports a fixed URL, static custom headers, and a body — no per-request signing capability (confirmed empirically). Constant-time comparison avoids a timing side-channel on the one credential available; accepted as weaker than HMAC given the sending platform's real constraints (§6.4). |
| D33 | A second, app-level activity/debug log (`AppLogService`) was added, separate from `ILogger` | The operator found the Breely webhook's production behavior opaque once it went live — `ILogger`'s console/Azure Log Stream output isn't retained anywhere staff can see without portal access. A flat rotating file (§4.9), not a database, matching D7's spirit; exposed via a new staff-only Settings page rather than requiring Azure portal access for something this operational. |
| D34 | The activity log's "actor" for staff actions reuses the signed-in Entra display name already shown in the header, not a new claim-resolution path | Consistency with the existing trust boundary (§6.2) already used for the "Booked By" default — a typed, editable field isn't a reliable audit identity, but the actual signed-in identity is. |
| D35 | Debug-tier Breely webhook payload logging redacts customer name/email/phone, keeping every other field | Decided explicitly with the operator (§4.9) — logging raw customer contact info would create a second at-rest PII store outside Exchange for the whole retention window, undermining the same privacy discipline already applied to the public surfaces (D11). |
| D36 | Debug-mode "network traffic" logging covers the Breely webhook's own processing steps and staff sign-ins, not literal Graph HTTP-level tracing | The literal reading would mean hooking the Graph SDK's HTTP pipeline - riskier to get right without local Graph testing access, and would flood the log with routine calendar-page reads unrelated to what an operator is actually debugging. Explicitly flagged to the operator as a scoping call, open to revisiting (§8). |
| D37 | `CancelAsync`/`CancelGroupAsync` tolerate a 404 from Graph on cancel/reopen as "already gone," instead of letting it crash the Blazor circuit | Live-hit 2026-08-03 while testing the new logging feature: cancelling a booking whose event had likely already been claimed/trimmed by the Breely webhook (§4.8) out from under a stale staff view threw an unhandled `ODataError` that took down the whole circuit. Extends the same 404-tolerance pattern `CancelSeriesAsync` already had for a missing series master to the plainer cancel paths, which didn't have it. |

---

## 10. Generalization Note

To reuse this for bowling lanes, tennis courts, or other facilities: the resource-mailbox-per-unit model, state/category mechanism, conflict enforcement, and public-endpoint pattern are all facility-agnostic, and — as of Phase 10 — the sheet count, mailbox naming, tenant, and time zone are genuinely configuration, not code. What still changes per facility is vocabulary (categories, states) and slot-granularity rules, which live in the application's domain layer (`Domain/BookingCategory.cs`, `Domain/ClubEventCategory.cs`), not its architecture.
