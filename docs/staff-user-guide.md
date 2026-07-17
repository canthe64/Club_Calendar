# Facility Scheduler — Staff User Guide

**Audience:** staff who create, edit, and cancel bookings through the app.
**Scope:** everything on the staff-facing `/calendar` and `/club-events` pages. The public calendar
members see is read-only and needs no instructions.

---

## 1. Signing In

Navigate to the app's URL and sign in with your normal club Microsoft account when prompted. Sign-in
is used for identity/audit purposes — it doesn't change what you can do in the app, just who shows
up as "Booked by" on a booking.

---

## 2. The Calendar Page

The main working page (`/calendar`) has three views, selected top-right: **Month**, **Week**, and
**Day**. Use the **‹**, **Today**, and **›** controls to navigate; clicking a day in Month or Week
view jumps to that day's Day view.

### The SHOW row

Below the toolbar, a row of controls filters what's currently displayed — this only affects what you
see, not the underlying data:

- **Category chips** (Rental, League, Bonspiel, Maintenance, Other) — click to toggle a category on
  or off.
- **Show club events** — toggles whether Club Events (§5) appear on the calendar at all.
- **Open ice only** — when on, shows only open (not-yet-rented) Rental holds, hiding everything else
  regardless of the category chips above.

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
- **Date**, **From**, **To** — time fields are in 30-minute increments, extending through midnight.
- **Booking status** (Rental category only) — **Hold for future rental** or **Confirmed booking**.
  No default; you must pick one. Every other category is always a confirmed (hard) booking — this
  toggle doesn't appear for them.
- **Event Title** — required for every category.
- **Phone / Email** (Rental only, optional) and **Notes** (optional, any category).

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
  - **Cancel & reopen for rental** (Rental bookings only) — the slot goes back to an open, unclaimed
    Hold, publicly bookable again.
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
