# Facility Scheduler — Staff User Guide

**Audience:** staff who create, edit, and cancel bookings through the app.
**Scope:** everything on the staff-facing `/calendar`, `/club-events`, and `/settings` pages. The
public calendar members see is read-only and needs no instructions.

---

## 1. Signing In

Navigate to the app's URL and sign in with your normal club Microsoft account when prompted. Sign-in
is used for identity/audit purposes — it doesn't change what you can do in the app, just who shows
up as "Booked by" on a booking.

---

## 2. The Calendar Page

The main working page (`/calendar`) has three views, selected top-right: **Month**, **Week**, and
**Day**. Use the **‹**, **Today**, and **›** controls to navigate; clicking a day in Month view, or
an empty area of a day column in Week view, jumps to that day's Day view.

**Week and Day are both hour-by-hour grids** covering the full 24-hour day, sharing the same time
axis down the left edge — Day view has one column per sheet, Week view has one column per day. If a
booking spans multiple sheets at once, Week view shows it as a single item (e.g. "League Practice ·
5 sheets") rather than repeating it in every sheet's row. Every cell's title is prefixed with its
start time (e.g. "7PM - League Practice"), including in Month view.

Club Events (§5) render inline in every view — a pinned row at the top for all-day events, or a
full-width band at the right hour for timed ones — with a **dotted border** distinguishing them from
sheet bookings (dashed = Hold, solid = Confirmed).

### The SHOW row

Below the toolbar, a row of controls filters what's currently displayed — this only affects what you
see, not the underlying data:

- **Category chips** (Group Event, League, Bonspiel, Maintenance, Practice Ice, Other) — click to
  toggle a category on or off.
- **Show club events** — toggles whether Club Events (§5) appear on the calendar at all.

---

## 3. Creating a Booking

Click the **+ New Booking ▾** button. A small menu opens with two options:

- **New Booking** — a single one-off booking (§3.1).
- **New Series** — a recurring weekly booking (§4).

You can also open the New Booking form directly by clicking an empty slot in Day view.

### 3.1 The booking form

- **Category** — required, no default. Pick one of the category chips before you can save.
- **Sheets** — tap sheet chips to toggle which sheet(s) this booking covers, or click **All Sheets**
  to select every sheet at once. A booking can span multiple sheets as one conceptual unit.
- **Date**, **From**, **To** — time fields are in 30-minute increments, covering the full 24-hour
  day. The end time must be after the start time.
- **Booking status** (Group Event category only) — **Hold for future group event** or **Confirmed
  booking**. No default; you must pick one. Every other category is always a confirmed (hard)
  booking — this toggle doesn't appear for them.
- **Event Title** — required for every category.
- **Phone / Email** (Group Event only, optional) and **Notes** (optional, any category).

Click **Save**. If the requested sheets/time conflict with an existing booking, or with a Club Event
flagged as closing all sheets, nothing is saved — a red banner lists exactly what conflicts, and you
adjust the sheets or time and try again.

---

## 4. Creating a Recurring Series

Click **+ New Booking ▾ → New Series**. This is a two-step wizard:

**Step 1** — same fields as a single booking, plus:
- **First date** / **Last date** — the series repeats weekly on the first date's day of the week,
  from the first date through the last date.

Click **Review N dates →** to move to Step 2.

**Step 2** — every date the series will create is listed. Conflicts (against existing sheet bookings,
or a closure Club Event) are flagged per date but **never automatically skipped** — you decide,
per date, using the **Skip**/**Include** toggle next to each one. Click **Create series** once you're
satisfied with the selection.

**Editing a series** is per-occurrence only — click a single occurrence on the calendar and edit it
like a normal booking; there's no "edit the whole series" action. **Canceling an entire series** is a
separate, deliberately de-emphasized action available from that occurrence's detail view (§3.2) —
meant for correcting a data-entry mistake at the start of a season, not routine cancellations, since
it deletes every occurrence, past and future, on every sheet involved.

### 3.2 Editing or canceling a booking

Click any booking chip to open its detail view, which offers **Edit booking…**, **Cancel Booking**,
and (only if the booking is part of a recurring series, shown as a smaller link below the main
buttons) the option to cancel the entire series instead.

- **Edit booking…** reopens the same form, pre-filled. If you deselect a sheet that was previously part of a
  multi-sheet booking, that sheet is left untouched (not deleted) and split off as its own booking —
  it won't keep showing the old renter's details.
- **Cancel Booking** offers up to three choices:
  - **Cancel & reopen for group event** (Group Event bookings only) — the slot goes back to an open,
    unclaimed Hold, publicly bookable again.
  - **Cancel booking** — removed entirely, no longer offered.
  - **Keep the booking** — closes the dialog, no change.
- **Cancel Series** deletes every occurrence of the recurring series on every sheet involved. You'll
  be asked to confirm; this can't be undone.

---

## 5. Club Events

Club Events are whole-club events (bonspiels, closures, club activities) that aren't tied to a
specific sheet. Reach them either via the **Club Events** button in the calendar toolbar (goes to the
dedicated `/club-events` list page) or by clicking a Club Event chip directly on the calendar, which
opens the same edit form inline without leaving the calendar page.

- **Category** — Bonspiel, Activities, Closure, or Other. Required, no default.
- **Event Title** — required.
- **All day** — toggle on/off. When off, set **From**/**To** times (same 30-minute increments as
  bookings).
- **Start date** / **End date**.
- **Marks all sheets unavailable** — off by default. Turn this on if the event actually closes the
  ice (not every club event does — e.g. a promotional tournament listing might not).
- **Notes** (optional).

When **Marks all sheets unavailable** is on, that event is cross-checked against new sheet bookings
and series: attempting to book a sheet during that window is blocked (for a single booking) or
flagged per-date (for a series preview) the same way a real sheet conflict is.

To delete a Club Event, open it for editing and use the **Delete Club Event** link at the bottom of
the form.

**A Club Event titled "⚠ Web booking needs review"** is created automatically, not by staff, when a
booking notification from the Breely booking website doesn't match any open group-event slot on any
sheet — the booking is still made (onto a fallback sheet) so it's never lost, but it needs a human to
check it landed on the right sheet and reassign it if not. Its notes include a link back to that
booking's page in Breely. It doesn't close any ice itself ("Marks all sheets unavailable" is off) —
delete it once you've verified or corrected the booking it refers to.

---

## 6. Understanding Conflict Warnings

Whenever a red conflict banner appears, it lists one of two kinds of conflict:

- **A sheet conflict** — another booking already occupies that sheet/time. Shown as
  `Sheet N: <time range> (<category>)`.
- **A closure conflict** — a Club Event marked "closes all sheets" overlaps the requested time.
  Shown as `Club event "<title>": <time range> — closes all sheets`.

For a single booking or edit, either kind blocks the save entirely — nothing is written until you
change the sheets or time. For the series wizard's review step, conflicts are informational: you
choose per date whether to skip it or include it anyway.

---

## 7. Settings — Activity Log

The **Settings** page (top nav bar) shows a record of what's actually happened in the app, and lets
you control how much detail it captures.

**Logging Level** — two options:

- **Standard** (the default) — records only definitive actions: a booking, series, or Club Event
  created, edited, or canceled, who did it, and which sheet/event it affects. This is what you'll
  normally leave it on.
- **Debug** — adds staff sign-in events and full detail on every notification received from the
  Breely booking website. Turn this on only while actively troubleshooting something (e.g. "did a
  Breely booking come through correctly?") — it's noisier, and there's no reason to leave it on day
  to day. Switch it back to Standard once you're done.

Pick a level and click **Save** — it takes effect immediately, no restart needed.

**Recent Activity** shows the most recent 500 lines from the log. Click **Refresh** to pull in
anything logged since you opened the page — it doesn't update on its own. Each line has a
timestamp, what happened, and who (or what — Breely-originated bookings show as "Breely webhook")
did it.

**Download full log archive (.zip)** downloads every log file the app currently has on hand, not
just what's shown in the 500-line viewer — useful if you need to look further back or hand the file
to whoever's helping troubleshoot something.
