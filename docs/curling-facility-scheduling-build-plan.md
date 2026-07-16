# Facility Scheduling System — Phased Build Plan

**Companion to:** `curling-facility-scheduling-architecture.md`
**Date:** 2026-07-11
**Stack:** .NET / C# (see architecture doc §9, D14)
**Target environment:** Trial M365 tenant (initial dev/test), production tenant TBD

This plan sequences the work so nothing gets built against unverified assumptions. The architecture document made several claims (Graph filter behavior, extended-property size limits, licensing) that are marked as **spikes** rather than settled facts — those come before anything durable is built on top of them.

---

## Phase 0 — Environment & credential setup (no code)

**Goal:** everything needed to talk to the trial tenant exists, safely.

- [ ] Confirm the trial tenant's licensing SKU; verify resource mailbox behavior against it (risk item, §8 of the architecture doc).
- [ ] Entra ID app registration created in the trial tenant: delegated calendar scopes (staff sign-in) + application calendar scopes (service identity).
- [ ] Client secret/certificate generated and placed directly into a local secret store (`.env`, gitignored) — **never shared in chat or committed**.
- [ ] Local dev environment: .NET SDK (current LTS), C#, NuGet package management. Microsoft Graph SDK for .NET (`Microsoft.Graph`) as the Graph client library.
- [ ] Decide hosting target for later phases (Azure Functions / App Service / container) — doesn't block early phases, but affects Phase 6 cache and Phase 8 public endpoint design.

**What you do vs. what I produce:** I'll write the exact PowerShell/Graph commands and the app-registration steps as instructions; you (or your tenant admin) run them and hand back non-secret identifiers (tenant ID, client ID, mailbox names) I can reference in code.

**Exit criteria:** a test script can authenticate to the trial tenant and list mailboxes. No booking logic yet.

---

## Phase 1 — Tenant provisioning (scripted, per §7 of the architecture doc)

**Goal:** the 5 sheet mailboxes + 1 Club Events mailbox exist, correctly scoped and permissioned.

- [ ] Create 6 resource mailboxes (5 sheets + Club Events).
- [ ] Create the mail-enabled security group; add all 6 mailboxes.
- [ ] Scope the app registration's application permissions to that group (Application Access Policy or RBAC for Applications — evaluate both against the trial tenant, pick one).
- [ ] **Negative test:** confirm the app identity is denied access to a mailbox outside the group.
- [ ] Provision the identical master category list (Rental / League / Event / Other) on each sheet mailbox; separate taxonomy on Club Events.
- [ ] Grant staff Editor (delegated write) and/or Reviewer (read-only fallback) permissions per §6.2.
- [ ] Enable mailbox audit logging.

This step should be scripted (PowerShell + Graph, or Graph-only where possible) rather than done by hand in the admin center, since it repeats every time a sheet is added.

**Exit criteria:** all 6 mailboxes exist, are correctly scoped, and a manual event created via Outlook/OWA is visible in both OWA and via a Graph read.

---

## Phase 2 — Verification spikes (small standalone scripts, not the app) — COMPLETE (2026-07-11)

Run as an in-app `/diagnostics` page (`Components/Pages/Diagnostics.razor`) against the live trial tenant rather than standalone scripts, since the app scaffold already existed with working Graph auth by this point. Results folded into the architecture doc's risk register (§8):
- Direct-write bypasses booking attendant: **confirmed**
- `calendarView` category `$filter`: **works server-side** (better than assumed)
- Extended property size (4000 chars): **fine**
- `getSchedule` window cap: **exactly 62 days**, confirmed via rejection message

Only unresolved item: category color rendering consistency across OWA/desktop Outlook — deferred as low-stakes/manual-only.


**Goal:** resolve the "uncertain" rows in the architecture doc's risk register (§8) before designing around them.

- [ ] Confirm current extended-property / open-extension size limits empirically (create an event, attach properties, push toward the documented limit).
- [ ] Test `calendarView` `$filter` behavior against `categories` — does server-side filtering work at all, partially, or not (design currently assumes "not," fetch-and-filter in app).
- [ ] Confirm `getSchedule`'s ~62-day window cap in practice.
- [ ] Create/update/delete a test event via direct Graph write; confirm the Resource Booking Attendant does *not* run (validates D3/§6.1) and that overlapping events are accepted without rejection.
- [ ] Spot-check category color behavior when the same mailbox's calendar is viewed via OWA vs. desktop Outlook.

**Exit criteria:** each risk-register row is either confirmed, refuted, or has a concrete workaround — written back into the architecture doc.

---

## Phase 3 — Core Graph client + booking domain logic

**Goal:** the backend can create, read, update, and cancel a booking correctly, with no UI yet.

- [ ] Graph client wrapper (delegated + application auth flows).
- [ ] Booking domain model: the fixed vocabulary (categories, states) as a single source of truth in code (per the "sole-writer discipline" invariant, D6/D7).
- [ ] Create booking: validate → per-sheet lock → conflict check (`calendarView` on target sheet) → write (subject, `showAs`, categories, extended properties, JSON blob) → capture `iCalUId`.
- [ ] Confirm hold → `showAs` tentative→busy update.
- [ ] Cancel → hard delete.
- [ ] Read: single-sheet grid for a time window.

No consolidated views, no cache, no Club Events integration yet — this phase is "one sheet, one booking, correctly."

**Exit criteria:** automated tests (or at minimum a manual test script) create/read/update/cancel bookings against the trial tenant and the results are correct in both the app's own read-back and in Outlook.

---

## Phase 4 — Staff web UI (basic)

**Goal:** a human can do everything Phase 3 proved out, through a browser.

- [ ] Auth: Entra ID SSO for staff sign-in.
- [ ] Per-sheet grid view for a given day/week.
- [ ] Booking creation/edit/cancel forms, including the metadata fields (renter contact, price, notes) and category selection.
- [ ] Visual encoding of state (hold vs. confirmed vs. category) — the UI's own color mapping, independent of Exchange's category colors (per §4.1 design rule).

**Exit criteria:** a staff member can fully manage bookings for one sheet without touching Outlook or this document.

---

## Phase 5 — Multi-sheet + consolidated views — BACKLOGGED (2026-07-16)

**Struck from the active plan per explicit user decision (2026-07-16).** Not rejected outright - deferred pending real usage/feedback once other club members start using the app (see the "project trajectory" note below and in project memory). Revisit only if raised again.

**Goal:** the actual differentiator — views that don't map 1:1 to a single Exchange calendar.

- [ ] All-sheets aligned timeline view.
- [ ] Interval-merge engine (sweep-line over per-sheet state boundaries).
- [ ] First derived view: "times when ≥N sheets are available for rental."
- [ ] `getSchedule` batching wired in for any view that only needs coarse free/busy, reserving `calendarView` + property reads for views needing category/state detail.

**Exit criteria:** the "≥2 sheets available for rental" example from the design discussion works correctly against real trial-tenant data across all 5 sheets.

---

## Phase 6 — Club Events (§4.4)

**Goal:** the 6th resource, integrated into views without a cross-conflict-check (per D13).

- [ ] Club Events mailbox CRUD (simpler than sheet bookings — no per-sheet lock needed, single mailbox).
- [ ] Fold Club Events into the interval-merge view: any covered window forces all sheets "unavailable."
- [ ] Dedicated Club Events list/calendar view for staff (the "simpler viewing" goal).

**Exit criteria:** creating a bonspiel spanning 3 days correctly shows all 5 sheets as unavailable in every consolidated view for that window, and the Club Events list shows just the big events with no per-sheet noise.

---

## Phase 7 — Ephemeral cache

**Goal:** introduce caching once real read patterns are known — not before, since premature caching risks caching the wrong things.

- [ ] In-memory cache keyed by sheet/view + time window, short TTL (30–60s).
- [ ] Invalidation on every write the app performs.
- [ ] Cache-miss fallback path already exists by construction (every view already computes live in Phases 5–6); this phase just adds the read-through/invalidate wrapper.

**Exit criteria:** repeated loads of the same view within the TTL window don't trigger repeated Graph calls (verify via request logging), and a cold cache still produces correct results.

---

## Phase 8 — Public endpoint

**Goal:** anonymous, minimized, cache-fed availability data.

- [ ] Read-only endpoint using the app service identity (never delegated/staff tokens).
- [ ] Hand-built minimization mapping — explicitly enumerate the public-safe fields; never pass through an internal payload.
- [ ] Distinct Club Events labeling (per the 2026-07-11 decision).
- [ ] Basic rate limiting.

**Exit criteria:** the endpoint returns correct summarized data and a manual review confirms no PII/pricing/notes fields are reachable from it.

---

## Phase 9 — CMS embed

**Goal:** the public view is visible on the actual club website.

- [ ] Thin embed (Drupal block today; portable to a WordPress shortcode/block later per the earlier discussion) that calls Phase 8's endpoint and renders the response — no Graph logic, no credentials, in the CMS.

**Exit criteria:** availability is visible on the live (or staging) club website.

---

## Phase 10 — Production hardening

**Goal:** ready to move off the trial tenant.

- [ ] Secrets moved to production secret store.
- [ ] Security review pass (OWASP hygiene, §6.4).
- [ ] Mailbox audit logging confirmed active in production tenant.
- [ ] Re-run Phase 1 provisioning checklist against the production tenant.
- [ ] Staff onboarding doc (how to get Editor/Reviewer permissions, how the states/categories work).

**Exit criteria:** production tenant mirrors trial tenant setup; staff can be onboarded without a developer in the room.

---

## Notes on sequencing

- Phases 0–3 have no UI and are the highest-uncertainty work (they're where the architecture doc's open risk items get resolved) — don't parallelize UI work against them.
- Phase 5 (consolidated views) is the actual value proposition of building this instead of using Outlook directly — worth protecting its scope rather than letting Phases 3–4 creep.
- Phase 7 (cache) is deliberately late — caching decisions made before real usage patterns exist tend to cache the wrong things or invalidate incorrectly.
- Club Events (Phase 6) was slotted after multi-sheet views because it depends on the interval-merge engine existing first; it doesn't depend on the cache or public endpoint.
