# GCC Ice & Event Calendar — Staff User Guide

**Audience:** staff who create, edit, and cancel bookings through the app.
**Scope:** everything on the staff-facing `/calendar`, `/club-events`, `/search`, `/settings`, and
`/practice-ice/approvals` pages. The public calendar members see is read-only and needs no
instructions.

---

## 1. Signing In

Navigate to the app's URL and sign in with your normal club Microsoft account when prompted. Once
you're in, the header greets you by name — that's how you can tell which account you're signed in as.

Sign-in identifies you for audit purposes (you're recorded as "Booked by" on bookings you create),
and it determines whether you get staff access at all: that comes from membership in the club's staff
security group, checked each time you sign in. If you can reach the Calendar page, you have it.

### Getting around

Everything is behind the **menu button** (the three-line icon at the top left):

| | |
|---|---|
| **Staff Calendar** | The main working page (§2) |
| **Public Calendar** | What members see — opens in a new tab |
| **Off-Ice Events** | Whole-club events and closures (§5) |
| **Search** | Find any event by day, category, or title (§6) |
| **Practice Ice** | The member-facing page showing open practice ice times — opens in a new tab |
| **Practice Ice Approvals** | Pending member requests to host (§10) |
| **Settings** | Booking rules and the activity log (§8) |
| **Sign out** | |

The two "opens in a new tab" items are the member-facing views, so you can check what members are
seeing without losing your place in the app.

---

## 2. The Calendar Page

The main working page (`/calendar`) has three views, selected top-right: **Month**, **Week**, and
**Day**. Use the **‹**, **Today**, and **›** controls to navigate; clicking a day in Month view, or
a day's name/date header in Week view, jumps to that day's Day view.

**Week and Day are both hour-by-hour grids** covering the full 24-hour day, sharing the same time
axis down the left edge — Day view has one column per sheet, Week view has one column per day. If a
booking spans multiple sheets at once, Week view shows it as a single item (e.g. "League Practice ·
5 sheets") rather than repeating it in every sheet's row. Every cell's title is prefixed with its
start time (e.g. "7PM - League Practice") **on its actual starting day only** — an event that spans
multiple days doesn't repeat that time on later days, since it didn't start again
at that hour each day. A multi-day item shows a **→** on days before its last and a **←** on days
after its first instead, so its chips read as one continuous event rather than unrelated repeats.

Off-ice events (§5) carry a **dotted border** distinguishing them from sheet bookings (dashed = Hold,
solid = Confirmed). Where they sit depends on the view:

- **Month and Week** — inline with everything else. All-day events pin to a row at the top of their
  column; timed ones sit at their actual hour, sharing the width with any booking running at the
  same time.
- **Day** — all of them, all-day and timed alike, list in a band just above the hourly grid. Day
  view's columns are sheets, and an off-ice event isn't on a sheet, so it gets no column of its own.
  For a timed one, a thin coloured rail in the narrow strip left of Sheet 1 shows when it runs; the
  rail is its band row's colour, and two off-ice events happening at once get side-by-side rails.

### The SHOW rows

Below the toolbar, two rows of chips filter what's currently displayed — this only affects what you
see, not the underlying data:

- **ON ICE** (Group Event, League, Bonspiel, Maintenance, Practice Ice, Learn To Curl, Other) — click
  to toggle a category on or off.
- **OFF ICE** (Out of Town Bonspiels, Competitions, Activities, Meetings, Closure, Other) — the same,
  for off-ice events (§5).

Each row has an **All** / **None** link on the right to switch that whole row at once — so hiding
everything off-ice is still one click.

Note both rows contain an **Other** chip, and that on-ice **Bonspiel** (one held on our ice) is a
different thing from off-ice **Out of Town Bonspiels** (members travelling to play elsewhere). Which
row a chip sits in is what tells them apart.

---

## 3. Creating an Event

Everything on the calendar is an **event**. The only question the form asks is whether it happens
**on the ice** (it takes up sheets) or **off the ice** (it doesn't).

Click the **+ New Event ▾** button. A small menu opens with three options:

- **New Event** — a single one-off event, opening on the ice (§3.1).
- **New Off-Ice Event** — the same form, opening off the ice (§5).
- **New Series** — a recurring weekly booking (§4). On-ice only.

You can also open the form directly by clicking an empty slot in Day or Week view; those always open
on the ice. In Day view this also fills in the sheet you clicked; Week view doesn't show sheets
separately, so pick one in the form.

### On the ice vs off the ice

The toggle sits at the top right of the form, next to the sheet buttons it governs.

- **On the ice** — pick one or more sheets. The event is checked against existing bookings and
  against the booking season (§8), and can't be saved if it collides.
- **Off the ice** — the sheet buttons grey out. Off-ice events aren't tied to a sheet, and are
  deliberately **not** checked against the booking season or against existing ice bookings, so
  closures and next-season planning can always be recorded. The form says so while you're in it.

Switching the toggle mid-entry keeps the date, time, title and notes you've already typed. Anything
that only applies to one side — sheets, the category, Hold/Confirmed, contact details, "closes all
sheets" — waits where you left it, so flipping over and back loses nothing.

**You can't move an event between on-ice and off-ice after it's saved.** The toggle shows as a greyed
badge when you open an existing event. If something was filed on the wrong side, delete it and create
it again.

### 3.1 The event form

- **Category** — required, no default. Pick one of the category chips before you can save.
- **Sheets** — tap sheet chips to toggle which sheet(s) this booking covers, or click **All Sheets**
  to select every sheet at once. A booking can span multiple sheets as one conceptual unit.
- **Start date** / **End date**, **From**, **To** — each time is two dropdowns: the hour, then the
  minutes past it (`:00`, `:15`, `:30`, `:45`), covering the full 24-hour day. Picking **Midnight**
  as the hour greys out the minutes, since it means the end of that day. Leave End date equal to Start date for an ordinary same-day
  booking; set it later to span multiple days (e.g. a weekend bonspiel) — From/To then apply to the
  start and end date respectively, and a caption confirms the resolved span. The end date/time must
  be after the start date/time, and a span can't exceed 14 days. Moving Start date past the current
  End date pulls End date forward to match, so the range never goes invalid; it never pulls End date
  backward, so a span you've already set up is preserved unless the new start actually outruns it.
- **Booking status** (Group Event category only) — **Hold for future group event** or **Confirmed
  booking**. No default; you must pick one. Every other category is always a confirmed (hard)
  booking — this toggle doesn't appear for them.
- **Event Title** — required for every category.
- **Phone / Email** (Group Event only, optional) and **Notes** (optional, any category).

Click **Save**. If the requested sheets/time conflict with an existing booking, or with an off-ice event
flagged as closing all sheets, nothing is saved — a red banner lists exactly what conflicts, and you
adjust the sheets or time and try again.

### 3.2 Editing or canceling a booking

Click any booking chip to open its detail view, which offers **Edit event…** and **Cancel Booking**.
If the booking is part of a recurring series, a second row appears below those with **Edit whole
series…** and **Cancel entire series** (§4), labeled with the series' own start and end date — read
live from the series each time you open the dialog, so it briefly says "loading series dates…" before
filling in.

- **Edit event…** reopens the same form, pre-filled. If you deselect a sheet that was previously part of a
  multi-sheet booking, that sheet is left untouched (not deleted) and split off as its own booking —
  it won't keep showing the old renter's details. You can also *add* a sheet that wasn't part of the
  original booking; it's created fresh under the same booking.
- **Cancel Booking** offers up to three choices (the reopen choice only appears for a **Confirmed**
  booking — cancelling something that's already an open Hold just offers to remove it, since there's
  nothing to "reopen"):
  - **Cancel & reopen for group event** (Confirmed Group Event bookings only) — the slot goes back to
    an open, unclaimed Hold, publicly bookable again. If this reopens right next to another open
    Group Event slot on the same sheet, they merge into one continuous open block rather than showing
    as separate chips.
  - **Cancel booking** — removed entirely, no longer offered.
  - **Keep the booking** — closes the dialog, no change.
- **Cancel Series** deletes every occurrence of the recurring series on every sheet involved. You'll
  be asked to confirm; this can't be undone.

**A chip titled "Available for Group Events"** is an open Hold the app created automatically, not
something staff typed in — either the leftover of a slot after a Breely booking claimed part of a
larger hold, or a hold reopened after cancelling a booking. It behaves exactly like any other open
Group Event Hold; there's nothing special to do with it beyond booking or cancelling it as normal.

---


## 4. Creating a Recurring Series

Click **+ New Event ▾ → New Series**. This is a two-step wizard:

**Step 1** — same fields as a single booking, plus:
- **First date** / **Last date** — the series repeats weekly on the first date's day of the week,
  from the first date through the last date.

Click **Review N dates →** to move to Step 2.

**Step 2** — every date the series will create is listed. Conflicts (against existing sheet bookings,
or a closure off-ice event) are flagged per date but **never automatically skipped** — you decide,
per date, using the **Skip**/**Include** toggle next to each one. Click **Create series** once you're
satisfied with the selection.

If a **Booking Season** is configured (Settings, §8), any of your requested dates that fall outside
it are shown separately, marked *"outside the booking season"* with no Skip/Include toggle — these
are never created, and it isn't a choice you can override here. Your First date/Last date on Step 1
are never changed by this; only which dates actually get created is affected. If every date ends up
skipped or outside the season, **Create series** is disabled until you adjust the range.

**Editing a series** has two paths, both from an occurrence's detail view (§3.2):

- **Edit event…** — the ordinary single-occurrence edit. Changes only the date you clicked,
  including its time.
- **Edit whole series…** — applies to every date, past and future; its header names the series' own
  start and end date, so you can confirm you've got the right series before changing it. You can
  change the title, notes, category, and which sheets the series occupies, but **not the time** — a
  series can span months, so a time change would mean re-checking every date against everything else
  on the calendar, which was judged too easy to get wrong. To move a series, edit each occurrence you
  need to move individually, or cancel and re-create it.
  - Removing a sheet drops every date of the series from it — this can never conflict.
  - Adding a sheet is checked against every date first; if any date collides with something already
    on that sheet, the whole change is refused and nothing is saved. Fix or skip that date's conflict
    first (by editing that one occurrence, or removing it), then try adding the sheet again.

**Canceling an entire series** is a separate, deliberately de-emphasized action, also from the
occurrence's detail view — meant for correcting a data-entry mistake at the start of a season, not
routine cancellations, since it deletes every occurrence, past and future, on every sheet involved.

---

## 5. Off-Ice Events

Off-ice events are whole-club events (bonspiels away, meetings, closures, social events) that aren't
tied to a specific sheet. Reach them either via the **Off-Ice Events** button in the calendar toolbar
(goes to the dedicated list page) or by clicking an off-ice chip directly on the calendar, which opens
the same edit form inline without leaving the calendar page.

They use the same form as on-ice events, opened with the toggle set to off-ice (§3). The fields below
are the ones that only appear on that side.

- **Category** — Out of Town Bonspiels, Competitions, Activities, Meetings, Closure, or Other.
  Required, no default. "Out of Town Bonspiels" is members travelling to play elsewhere — not to be
  confused with the on-ice Bonspiel category, which is one held on this club's own ice.
- **Event Title** — required.
- **All day** — off by default (most off-ice events — meetings, closures — have a real start and end
  time), so **From**/**To** times (same hour + minutes dropdowns as bookings) are visible immediately;
  toggle it on for something that genuinely spans whole days instead. A timed event's End can equal
  its Start — a zero-length marker for something that happens at a moment rather than over a span
  (e.g. a ribbon cutting) — but not fall before it.
- **Start date** / **End date** — moving Start date past the current End date pulls End date forward
  to match, so the range never goes invalid; it never pulls End date backward, so a span you've
  already set up is preserved unless the new start actually outruns it.
- **Closes all sheets for this time** — off by default. Turn this on if the event actually closes the
  ice (not every off-ice event does — e.g. a promotional tournament listing might not).
- **Notes** (optional).

When **Closes all sheets for this time** is on, that event is cross-checked against new sheet bookings
and series: attempting to book a sheet during that window is blocked (for a single booking) or
flagged per-date (for a series preview) the same way a real sheet conflict is.

To delete an off-ice event, open it for editing and use the **Delete Event** link at the bottom of
the form.

**An off-ice event titled "⚠ Web booking needs review"** is created automatically, not by staff, when a
booking notification from the Breely booking website doesn't match any open group-event slot on any
sheet — the booking is still made (onto a fallback sheet) so it's never lost, but it needs a human to
check it landed on the right sheet and reassign it if not. Its notes include a link back to that
booking's page in Breely. It doesn't close any ice itself ("Marks all sheets unavailable" is off) —
delete it once you've verified or corrected the booking it refers to.

---

## 6. Searching for an Event

Reach **Search** from the menu. Unlike the calendar, which only ever shows the day/week/month
currently in view, this searches a date range up to 60 days wide at once (14 days back through 46
ahead by default) so you can find something without already knowing exactly when it happened. Move
Start/End forward or back to search further out — the 60-day limit keeps a search fast; a much wider
one would take Exchange a long time to compute against recurring bookings.

Type a search and press **Enter** or click **Search**. Nothing is fetched until you do — opening the
page or adjusting the date range alone doesn't search on its own.

**Search terms:**

| Term | Matches |
|---|---|
| `category:bonspiel` | A category — on-ice and off-ice events both use this prefix, e.g. `league`, `bonspiel`, `practiceice`, `closure` |
| `day:saturday` | A day of the week — full name or 3-letter abbreviation (`sat`) |
| `type:on-ice` / `type:off-ice` | Restrict results to just on-ice or just off-ice events (the older `type:booking` / `type:clubevent` still work) |
| any other word | Matches the title — a renter's name for an on-ice booking, the event name for an off-ice event |

Terms combine — `category:league day:tuesday junior` finds Tuesday league bookings with "junior" in
the name. Multiple `day:` or `category:` terms combine as *either*: `day:saturday day:sunday` finds
anything on the weekend.

`category:bonspiel` deliberately matches **both** an on-ice Bonspiel and an off-ice event under "Out
of Town Bonspiels" — a note explains this and suggests `category:outoftownbonspiels` or
`type:on-ice` if you meant just one, since both were included in the search.

Search only looks at **titles and categories** — not phone numbers, email addresses, or notes. Open a
result to see that information; it was never meant to be searchable text.

**Start date** / **End date** narrow or widen the window searched, always up to 60 days at a time. A
reminder of that limit sits under the Search button at all times. If you pick a wider range (or an
end date before the start date), **Search is disabled** and a specific message replaces the reminder
explaining exactly what to fix — you won't be able to click Search and only find out afterward that it
didn't cover what you asked for.

Click a result to see its detail. From there, **Open on calendar** takes you to that day on the
Calendar page, where you can edit or cancel it the normal way — Search itself is read-only, so you
always jump to the same working edit screen you'd use from the calendar directly.

**Export CSV** appears once your search has found at least one result. It downloads *every* match —
not just the ones on screen, even if there are more than fit — as a spreadsheet file you can open in
Excel or Google Sheets, with one row per result: date, start and end time, title, on-ice/off-ice,
category, sheet(s), and Hold/Confirmed status. It does **not** include phone numbers or email
addresses, the same as the search results on screen.

---

## 7. Understanding Conflict Warnings

Whenever a red conflict banner appears, it lists one of two kinds of conflict:

- **A sheet conflict** — another booking already occupies that sheet/time. Shown as
  `Sheet N: <time range> (<category>)`.
- **A closure conflict** — an off-ice event marked "closes all sheets" overlaps the requested time.
  Shown as `Off-ice event "<title>": <time range> — closes all sheets`.

For a single booking or edit, either kind blocks the save entirely — nothing is written until you
change the sheets or time. For the series wizard's review step, conflicts are informational: you
choose per date whether to skip it or include it anyway.

---

## 8. Settings

The **Settings** page (menu button, top left) controls booking behavior and shows a record of what's actually
happened in the app.

### Minimum group event booking interval

When a Breely booking claims part of an open Group Event hold, whatever time is left over before
and/or after it is normally offered as its own bookable slot (titled "Available for Group Events,"
§3.2) — unless it's shorter than this many minutes, in which case that leftover time is dropped
instead of offered as an unusably short sliver. Choose 30, 60, 90, or 120 minutes and click **Save**
— it takes effect immediately, no restart needed.

### Public Calendar Visibility

Hides anything starting after a chosen date from the **public** calendar and the club-website widget
only — you still see and edit everything on the staff calendar regardless. Useful for building out
next season's schedule ahead of time without members seeing a half-finished calendar. Pick a date and
click **Apply**; click **Clear** to go back to showing everything publicly. Leave it blank (the
default) and nothing is hidden.

### Booking Season

Rejects new bookings — the staff form, series, and member practice ice requests — outside a
start/end window, and stops the public search tool, the widget, and practice ice from advertising
off-season slots in the first place. **Off-ice events are exempt** — closures, off-season committee
meetings, and next season's planning still go on the calendar regardless of this setting. Either date
can be left blank to leave that side unrestricted (e.g. a start date with no end lets bookings begin
once the season opens, with no cutoff on how far out they can go). Set one or both dates and click
**Save**; **Clear** removes the restriction entirely.

Once you're within 30 days of a configured public-calendar cutoff, a reminder banner appears at the
top of every staff page until you update or clear the date — it stays up even after the date itself
passes, since a season quietly staying hidden with nobody having noticed is worth flagging for longer
than a one-time heads-up. The season start/end window has no equivalent banner.

### Activity Log

The rest of the page shows a record of what's actually happened in the app, and lets you control how
much detail it captures.

**Logging Level** — two options:

- **Standard** (the default) — records only definitive actions: a booking, series, or off-ice event
  created, edited, or canceled, who did it, and which sheet/event it affects. This is what you'll
  normally leave it on.
- **Debug** — adds staff sign-in events, full detail on every notification received from the
  Breely booking website, and a line whenever the app itself starts up or shuts down. Turn this on
  only while actively troubleshooting something (e.g. "did a Breely booking come through
  correctly?") — it's noisier, and there's no reason to leave it on day to day. Switch it back to
  Standard once you're done.

**While Debug is selected or active, a warning banner appears on the page:** the Breely detail this
level captures can include a customer's name, email, or phone number. There's no automatic timeout —
switching back to Standard is on you, not the app.

Pick a level and click **Save** — it takes effect immediately, no restart needed.

**Recent Activity** shows the most recent 500 lines from the log. Click **Refresh** to pull in
anything logged since you opened the page — it doesn't update on its own. Each line has a
timestamp, what happened, and who (or what — Breely-originated bookings show as "Breely webhook")
did it.

**Download full log archive (.zip)** downloads every log file the app currently has on hand, not
just what's shown in the 500-line viewer — useful if you need to look further back or hand the file
to whoever's helping troubleshoot something.

---

## 9. "Something went wrong" message

If a page shows **"Something went wrong loading this page"** instead of its usual content, click
**Try again**. This is usually a one-off (a transient Graph hiccup, a stale view) and clicking Try
again reloads just that page in place - no need to sign in again or lose anything else you had open
elsewhere. If it keeps happening on the same action, that's worth reporting.

---

## 10. Practice Ice Requests

Any properly-trained member can volunteer to host a practice ice session open to the whole club, at
a time when nothing else is on the calendar. Members find open times and submit a request at
`/public/practice-ice` (no sign-in needed to browse, sign-in required to actually submit); every
request lands here for staff review before it's real.

**Practice Ice Approvals** (menu button, top left; also linked from Settings) lists every pending request,
soonest first - a request whose slot is coming up soon is the one most likely to need a decision
first, since (unlike a booking) there's no record of *when* a member actually submitted it. Each
entry shows the requested time, the volunteer's name and email, and any notes they added.

- **Approve** confirms the slot immediately - it becomes a real "Practice Ice" booking across every
  sheet, and the volunteer gets an email letting them know.
- **Decline** requires a short reason first (shown to the volunteer in their email, so they're not
  left guessing) - type it, then **Confirm decline**. This removes the request entirely; there's no
  record kept of a declined request once it's gone.

**If the notification email couldn't be sent** (shown as a banner after either action, in amber
rather than green), the approve or decline itself still went through - only the automatic email
failed. Contact the volunteer directly in that case, since they won't have heard anything from the
app.

You can also get to the approval queue from an individual booking: opening any Practice Ice booking
on the Calendar page that's still awaiting approval shows a link straight to this page.
