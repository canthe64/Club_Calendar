# Embedding public availability on the club website

Paste this into a Drupal HTML/custom block (or the equivalent block/widget area on a future
WordPress site - it's plain HTML/JS, portable to either):

```html
<div id="curling-availability"></div>
<script src="https://YOUR-APP-HOSTNAME/embed/availability-widget.js"></script>
```

Replace `YOUR-APP-HOSTNAME` with wherever this app is actually hosted (e.g.
`facilityscheduler-a1b2c3d4.westus-01.azurewebsites.net` — Azure's default domain carries a
generated suffix — or a custom domain if one gets set up later). That's the
only configuration needed - the widget figures out the API host itself from the script tag it was
loaded from.

## Optional settings

Add these as attributes on the `<script>` tag if needed:

- `data-days="14"` - show fewer/more days ahead than the default (30, capped at 60 by the server
  regardless of what's requested).
- `data-target="some-other-id"` - if the container div needs a different ID than
  `curling-availability` (e.g. to avoid a clash with something else already on the page).

## What this shows

- Every sheet's currently open-for-group-event time slots (the same open Group Event holds staff
  create in the calendar) for the lookahead window.
- Any club-wide events (bonspiels, tournaments, closures) in that window, with a distinct call-out
  and "all sheets reserved" wording specifically when a closure/event has been marked as closing
  every sheet.

## What this never shows

No renter names, phone numbers, emails, notes, or the underlying resource mailbox addresses - the
public endpoint uses its own deliberately separate, hand-built response shape rather than exposing
anything from the internal booking data directly (architecture doc §5.4). League, Bonspiel,
Maintenance, Practice Ice, and Other category bookings are not shown either - only explicit Group
Event holds mean "open for the public to book."

## Full calendar page (embeddable via iframe)

There's also a complete, anonymous calendar page at `https://YOUR-APP-HOSTNAME/public/calendar` -
Month, Week, and Day views (a toggle in the header switches between them, same as the staff
calendar), Prev/Next/Today navigation, every category shown (League, Bonspiel, Maintenance,
Practice Ice, Other, Group Event - not just open slots), plus off-ice events, each shown in its own
category color and filterable separately (D99). This is the *primary* public
view; the list-style widget above is a subordinate, availability-focused feature.

- `?view=month` (default), `?view=week`, or `?view=day` selects the view.
- `?month=yyyy-MM` sets the displayed month (Month view); `?date=yyyy-MM-dd` sets the anchor date
  (Week/Day views - Week shows the 7-day week containing that date).

To embed it directly in a page instead of linking to it, use a plain iframe:

```html
<iframe src="https://YOUR-APP-HOSTNAME/public/calendar" style="width:100%;height:800px;border:0"></iframe>
```

Add `?view=week` or `?view=day` to the `src` if you'd rather the embed open directly into one of
those instead of Month.

**What it shows:** each entry's start time and title (the same title staff see internally - e.g. a
league's name, or a renter's name if one happens to be in the title field), e.g. "7PM - Monday Night
League", and its exact date/time when tapped. The one deliberate exception: a *confirmed booking's*
renter name is not stripped out programmatically - keeping that private is handled by staff practice
(i.e., what staff choose to type in that field), not by this page. Changing the date range or
switching views reloads the page (there's no client-side app here) - a brief "Loading…" overlay
appears immediately so that reload isn't silent.

The header's "Host practice ice" and "Find available times for a group event" links open in the top-level page/tab
(`target="_top"`), not inside your iframe - so clicking either one navigates the whole browser tab
away from your page, the same as any other outbound link would. This is deliberate: both destinations
send `X-Frame-Options: DENY` (this app's own security headers, applied to every route except this one),
so without `target="_top"` the browser would silently refuse to load them inside your iframe at all.

**Security note - possible future hardening:** this page currently has no restriction on who can
embed it in an iframe (no `Content-Security-Policy: frame-ancestors` set). In practice this means
any website, not just the club's own, could iframe it. This was a deliberate choice for now (simplicity
over restricting to a specific domain), but worth revisiting - especially once other club members are
using the app and its public surface gets more scrutiny - by adding a `frame-ancestors` directive
scoped to the club's actual website domain.

## Availability search page

`https://YOUR-APP-HOSTNAME/public/search` lets a visitor search for a date/time window with at
least N sheets open for a group event, instead of scanning the calendar by hand - a date range
(capped at 60 days) plus a minimum-sheets dropdown, returning a list of matching windows that each
link to that day on the calendar. Linked from the calendar page's header, and vice versa. Same
"never promise ice that isn't actually open" guarantee as the widget above - a window is only
reported if nothing else is booked on that sheet during it. Between the search form and the results,
the page points visitors at `curlingseattle.org/group-events` to actually confirm availability and
inquire - this search only ever shows what *looks* open, not a confirmed booking.

## If something looks wrong

- If the widget shows "Availability is temporarily unavailable," check that the app is running and
  that `https://YOUR-APP-HOSTNAME/api/public/availability` loads directly in a browser.
- If nothing appears at all, confirm the `<div id="curling-availability">` is actually present on
  the page and its ID matches what the script expects (or matches a `data-target` override).
