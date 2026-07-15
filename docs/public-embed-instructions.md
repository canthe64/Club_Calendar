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

- Every sheet's currently open-for-rental time slots (the same "AVAILABLE FOR RENTAL" holds staff
  create in the calendar) for the lookahead window.
- Any club-wide events (bonspiels, tournaments, closures) in that window, with a distinct call-out
  and "all sheets reserved" wording specifically when a closure/event has been marked as closing
  every sheet.

## What this never shows

No renter names, phone numbers, emails, notes, or the underlying resource mailbox addresses - the
public endpoint uses its own deliberately separate, hand-built response shape rather than exposing
anything from the internal booking data directly (architecture doc §5.4). League, Bonspiel,
Maintenance, and Other category bookings are not shown either - only explicit Rental holds mean
"open for the public to book."

## If something looks wrong

- If the widget shows "Availability is temporarily unavailable," check that the app is running and
  that `https://YOUR-APP-HOSTNAME/api/public/availability` loads directly in a browser.
- If nothing appears at all, confirm the `<div id="curling-availability">` is actually present on
  the page and its ID matches what the script expects (or matches a `data-target` override).
