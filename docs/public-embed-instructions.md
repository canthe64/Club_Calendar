# Embedding public availability on the club website

Paste this into a Drupal HTML/custom block (or the equivalent block/widget area on a future
WordPress site - it's plain HTML/JS, portable to either):

```html
<div id="curling-availability"></div>
<script src="https://YOUR-APP-HOSTNAME/embed/availability-widget.js"></script>
```

Replace `YOUR-APP-HOSTNAME` with wherever this app is actually hosted (e.g.
`facilityscheduler.azurewebsites.net`, or a custom domain if one gets set up later). That's the
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

There's also a complete, anonymous month-calendar page at `https://YOUR-APP-HOSTNAME/public/calendar`
- Prev/Next/Today navigation, every category shown (League, Bonspiel, Maintenance, Practice Ice,
  Other, Group Event - not just open slots), plus club events (with a dotted border distinguishing
  them from sheet bookings, same as the staff calendar). This is the *primary* public view; the
  list-style widget above is a subordinate, availability-focused feature.

To embed it directly in a page instead of linking to it, use a plain iframe:

```html
<iframe src="https://YOUR-APP-HOSTNAME/public/calendar" style="width:100%;height:800px;border:0"></iframe>
```

**What it shows:** each entry's start time and title (the same title staff see internally - e.g. a
league's name, or a renter's name if one happens to be in the title field), e.g. "7PM - Monday Night
League", and its exact date/time when tapped. The one deliberate exception: a *confirmed booking's*
renter name is not stripped out programmatically - keeping that private is handled by staff practice
(i.e., what staff choose to type in that field), not by this page.

**Security note - possible future hardening:** this page currently has no restriction on who can
embed it in an iframe (no `Content-Security-Policy: frame-ancestors` set). In practice this means
any website, not just the club's own, could iframe it. This was a deliberate choice for now (simplicity
over restricting to a specific domain), but worth revisiting - especially once other club members are
using the app and its public surface gets more scrutiny - by adding a `frame-ancestors` directive
scoped to the club's actual website domain.

## If something looks wrong

- If the widget shows "Availability is temporarily unavailable," check that the app is running and
  that `https://YOUR-APP-HOSTNAME/api/public/availability` loads directly in a browser.
- If nothing appears at all, confirm the `<div id="curling-availability">` is actually present on
  the page and its ID matches what the script expects (or matches a `data-target` override).
