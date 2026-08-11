# Member-Hosted Practice Ice — Design Proposal

**Status: implemented (2026-08-11).** This document is kept as the historical design record - the
rationale, rejected alternatives, and every open question worked through with the operator before
and during the build. The architecture doc's own account of what shipped, including six new Design
Decision Record entries (D68-D73) covering points where implementation reversed or refined what's
written below, lives at `docs/curling-facility-scheduling-architecture.md` §5.4.4. That's the
authoritative current-state reference; this document is not maintained going forward.

One load-bearing thing decided during implementation, not below: this app has **no role/group
distinction between staff and any other signed-in user** (§3.3 assumed member sign-in without
examining that consequence). A member invited as a guest to submit a practice ice request can reach
every staff page, including approvals for other members' requests. Flagged as an open, unresolved
risk in the architecture doc §8 - read that before inviting members at any real volume.

Where this proposal contradicted an existing recorded decision at design time, that was called out
explicitly in §8 below — largely superseded now by the architecture doc's own DDR entries, but left
in place as the original reasoning trail.

---

## 1. The request

Any member who is properly trained may host practice ice. To do so they need to (a) find an
available time slot and (b) volunteer to host during that time. The club wants a way to surface
those slots and collect the volunteer's offer.

Delivered in priority order:

1. A view of potential practice ice slots.
2. A form to request to host one.
3. A way to notify the approver group that a request came in.

## 2. Constraints as given

| Constraint | Value | Notes |
|---|---|---|
| Eligible hours | 06:00–22:00, every day | Tentative; belongs in configuration, not a constant |
| Eligibility rule | No other activity on **any** sheet | Includes unsold group-event holds — see §3.1 |
| Minimum lead time | 48 hours | Tentative; configuration |
| Maximum horizon | 30 days | Tentative; configuration |
| Slot granularity | 30 minutes | Sessions typically 1–2 hours, i.e. 2–4 blocks |
| Sheets consumed | All 5 | The volunteer hosts on behalf of all members |

**Sizing.** ~600 member club. Expected volume is 2–5 practice ice slots per week in season, each
1–2 hours. Several "don't build it" recommendations below rest on this number; if real volume is
materially higher, §7 should be revisited.

**Supply context.** Group-event slots are pre-determined at the start of each season (typically
09:00–14:00 weekdays, plus occasional weekends) and rarely added mid-season. Practice ice will
therefore naturally fall in evenings and weekends. The two audiences barely compete for the same
hours in practice.

## 3. Proposed design

### 3.1 Availability — computed gaps

A slot is a potential practice ice window when, within eligible hours, **no sheet has any event of
any kind** and no ice-blocking club event overlaps. Every booking category and both states
(`Hold` and `Confirmed`) block, including unsold `GroupEvent` holds — group events take priority
over practice ice even when nobody has bought them yet.

This is deliberately the simplest possible rule: absence of a calendar event *is* availability,
with no additional blackout signal, because the things that would justify one are already modelled
as calendar objects. Ice maintenance is an existing `Maintenance` category, and building/facility
closures are already club events carrying `MarksSheetsUnavailable`, which the public availability
path honours today.

**Parameterisation.** The computation should be written as "windows where **≥N sheets** are free,"
not "where all sheets are free," with N=5 supplied by the caller. `PublicAvailabilityService`
already contains a boundary-sweep of exactly this shape (`FindConcurrentAvailability`, used by
`/public/search` to find windows with ≥N sheets *held for group rental*). The practice ice version
is the same sweep run over *free* intervals instead of over holds. Writing it parameterised costs
nothing now and is the difference between a config change and a rewrite if §6 proceeds.

### 3.2 Booking model

A submitted request is written to the calendar **immediately**, as `PracticeIce` + `Hold`, across
all five sheets in one operation via the existing `CreateAcrossSheetsAsync` — one `BookingGroupId`,
full metadata blob, under the existing per-sheet locks and conflict check. All-or-nothing by
construction.

Immediate write is not merely convenient; it is forced. Under D7 (no companion database) there is
nowhere else a pending request can live. It is a calendar event or it does not exist.

No new states are introduced. Approval flips the group `Hold` → `Confirmed` using the existing
lifecycle. Because the booking spans five sheets under one group id, the public calendar renders it
as a single "Practice Ice · 5 sheets" chip with no additional work.

### 3.3 Identity and submission

Submission requires authentication; the volunteer's name and email are taken **from the Entra token,
not typed into the form**. A typed address can be anyone's, and deriving it removes a field from the
form. The form therefore collects only the slot and the qualification certification (which the app
records but does not validate).

> **Dependency.** This app currently authenticates staff only. Member-facing authentication is
> being designed separately and is not yet built. **This feature cannot ship before that work
> lands.** Whether the *browse* view is anonymous or also gated is still open — see §7.

### 3.4 Approval

An in-app approval queue is the authoritative view of pending requests. Approval is one click.
Decline hard-deletes the events (per D9) and requires the approver to supply a free-text **reason**,
which is sent to the volunteer — without it, a declined member can simply re-request the same slot
and be declined again, since D9 leaves no record that the decline ever happened.

### 3.5 Notification

Approver notification is a single email sent via Graph `sendMail`, from the existing mailer service
account (a licensed Exchange Online user in the same tenant, already used for outbound mail by a
separate club system), addressed to a **pre-defined distribution group configured in Azure App
Settings** — not on the in-app Settings page, and not hard-coded, so membership and address changes
need no redeploy.

`replyTo` is set explicitly rather than defaulting to the sender: approver-facing mail replies to
the volunteer, volunteer-facing mail replies to the approver group. Without this, replies land in a
mailbox nobody treats as an inbox.

This requires the `Mail.Send` **application** permission on the app registration. That permission is
tenant-wide send-as by default; it is constrained by the same Application Access Policy mechanism
already used to scope `Calendars.ReadWrite` to the sheet mailboxes. See §8 for the security note.

### 3.6 Privacy

**The host's name is included in the booking title**, deliberately, so members can see who is
hosting. The club accepts this as a legitimate use of personal information: hosting practice ice is
a voluntary, outward-facing club role, and the name is the point of the listing.

Contact details and the qualification certification are **not** published — email, phone, and the
certification record remain visible only to authenticated staff.

This is a deliberate departure from the `PublicTitle` redaction added on 2026-08-04, and the
distinction is worth keeping straight. That redaction exists because Breely populates a *paying
customer's* real name automatically, with no staff opportunity to redact it and no expectation by
the customer that they would be listed publicly. A practice ice host is a member choosing to put
their name forward in a club role. Different consent, different answer.

**Implementation note.** This makes the feature *less* work, not more. `PublicTitle` already falls
back to `RenterName` when `ExternalBookingId` is absent, so storing the host's name in the existing
renter-name field renders it in the title with no code change — the redaction would have been the
extra work. The multi-sheet suffix composes normally, giving titles of the form
"Jane Smith · 5 sheets".

**Decided:** the slot view is anonymous and the host's **full name** is shown publicly, for both
pending and confirmed requests. The audience is therefore the open internet, not members only, and
this was accepted explicitly rather than inherited by default. No abbreviation, sign-in gate, or
pending-state suppression is applied.

Pending and confirmed are distinguished only by the hold-versus-confirmed visual treatment the
public calendar already applies — `PublicMonthBooking` carries the confirmed flag today, so no
special handling is needed for either case.

### 3.7 Stale requests

A hold written on submission blocks five sheets until someone actions it. **No automated expiry is
proposed.** The queue shows hold age, oldest first; the notification email makes "nobody looked"
unlikely; and stale holds are visible on the staff calendar during normal use.

The accepted residual risk is an approver being away while a request sits. The mitigating argument
is social rather than technical: a volunteer who does not see an approval will follow up. Auto-expiry
remains available later (see §7) and adding it would not invalidate anything built now.

### 3.8 Surfaces — anonymous browse, authenticated submit

Because the slot view is anonymous (§3.6), **D15 applies**: it must be a plain Minimal API endpoint
with hand-built HTML, GET forms, and full page reloads — the same pattern as `/public/calendar` and
`/public/search`, never a Blazor component. D15 is a hard rule established after a live incident, not
a style preference.

Submission is authenticated, so that surface is *not* constrained by D15 and may be a Blazor page.
The natural split is therefore an anonymous browse endpoint whose "request this slot" control links
to an authenticated route, carrying the chosen slot in the query string and triggering sign-in there
if needed.

**Consequence worth designing for:** the slot parameters then arrive from an untrusted, editable, and
possibly stale query string. Every eligibility rule must be re-validated server-side at submission —
the 48-hour floor, the 30-day horizon, eligible hours, 30-minute boundaries, and all five sheets
being free — not trusted from the link. `CreateAcrossSheetsAsync` already re-checks conflicts under
lock, which covers the "someone booked it in the meantime" case, but it knows nothing about the
practice ice eligibility rules and will happily create a booking that satisfies none of them.

## 4. What this reuses

Almost all of it. `BookingCategory.PracticeIce` already exists; so do the `Hold`/`Confirmed` states,
`CreateAcrossSheetsAsync` with its per-sheet locking and all-or-nothing guarantee, the interval
subtraction and boundary-sweep helpers, the multi-sheet chip consolidation, and the D15 full-page-
reload GET-form pattern used by the public calendar's category filters.

Genuinely new: the gap computation, the request form, the approval queue, and outbound mail.

## 5. Rejected alternatives

Recorded because they were seriously considered and the reasoning is not obvious in hindsight.

**Exchange Resource Booking Attendant / meeting requests.** Superficially very attractive: delegates
approve from Outlook with no app involvement, tentative→busy maps exactly onto the existing
`showAs` convention, and Exchange notifies the requester for free. Rejected on four grounds. A
meeting addressed to five room resources is delivered as **five independent requests**, each
processed and accepted separately — approvers would click accept five times per request (~25/week at
expected volume) and could miss one, producing a partially-booked session. Attendant-created events
carry none of the app's extended-property metadata, so the app would read foreign objects it cannot
classify. It reintroduces a second writer, which D8 relies on not existing. And Entra guest accounts
have no Exchange mailbox, so a member has nothing to originate an invite from.

**Attendees on the app-written events, using Exchange as the mail transport.** Would have avoided the
`Mail.Send` permission entirely. Rejected on discovering it has the same defect used to reject the
attendant: the app writes five separate events on five separate mailboxes, so five invitations go
out, each organised by a different room mailbox. Putting attendees on only one of the five works but
misrepresents the booking and clutters approver calendars with a block that means nothing.

**Power Automate HTTP-triggered flow.** Would also have avoided the permission. Declined by the club
for independent reasons; additionally, the HTTP request trigger is a premium connector, and it would
place a live piece of the system outside source control, CI, and test coverage.

**Club Events mailbox as sender.** Requires no scope expansion, since it is already inside the
policy group. Rejected because the sender identity would have to vary once §6 proceeds (per-sheet
reservations sending as the sheet), giving approvers mail from six different senders, breaking
recognisability and any inbox rule built on it. It also conflates a system-of-record mailbox with an
outbound mailer, so replies land somewhere unmonitored.

**Staff pre-painting practice ice windows** (the way group-event holds are painted). Near-zero new
logic, but imposes recurring staff work, and any unpainted time reads as unavailable.

**Blackout/eligible-hours intersection beyond the plain rule.** Proposed early, then discounted:
maintenance and closures are already calendar objects, so a second mechanism would duplicate them.

**Auto-expiry background service, and a staging calendar for pending requests.** Both solve stale
holds properly. Both judged disproportionate at 2–5 requests per week against a notification path
that already makes the failure unlikely. Revisit if §7 item 3 materialises.

## 6. Adjacent scenario — private team reservations

Under discussion separately: allowing a competitive team to reserve ice for its own practice, rather
than hosting openly for all members. **This requires policy review and approval and is not part of
this proposal.**

It affects exactly one design choice, and cheaply. Private team ice would need only one or two
sheets, and its eligibility would require only those sheets to be free — hence the ≥N
parameterisation in §3.1, which costs nothing now and avoids a rewrite later.

Two things are deliberately *not* pre-solved, because guessing wrong costs more than deciding later:
whether private team ice is a distinct `BookingCategory` (it likely needs to be — the public
calendar should distinguish "open practice, all welcome" from "reserved", and the approval policy
differs), and whether it carries a fee.

## 7. Open items

1. **Member authentication must land first** (§3.3). This is a hard dependency, not a sequencing
   preference.
2. **Private team reservations** (§6) — pending policy review.
3. **Stale-hold expiry** (§3.7) — deliberately deferred, not rejected.

## 8. Consequences to accept, and decisions that would need recording

**The availability model contradicts an existing documented position.** `PublicAvailabilityService`'s
own docstring states that "available" deliberately means an explicit group-event hold and *not* raw
free/busy, reasoning that "unbooked League/Bonspiel/practice time isn't necessarily something staff
want the public renting." This proposal computes exactly that complementary free time. The
contradiction is defensible — that reasoning was about *renting* ice to the public, and the blockers
it worried about are themselves calendar objects — but it is a reversal and would need its own
Design Decision Record entry rather than a silent change.

**`Mail.Send` expands the app's Graph permissions.** Application Access Policies scope by *mailbox,
not by permission*: adding the mailer to the app's allowed scope also grants this app
`Calendars.ReadWrite` on the mailer's calendar. Harmless in practice — the app will never call it —
but it is a real if small least-privilege regression and should be a recorded decision, not a side
effect. The policy constraint itself should be verified live both before and after the grant.

**Cross-system coupling.** Reusing the mailer creates a dependency on an account owned by a
different club system. If that account is renamed, rotated, or decommissioned, practice ice
notifications break silently and nobody debugging the other system would know. Mitigation is
documentation in both directions.

**Group-event priority is policy, not enforcement.** Once practice ice is confirmed, the app's
conflict check will *block* staff from painting a new group-event hold over it — the write path has
no concept of category precedence. Confirmed as acceptable: conflicts are handled individually, and
no bump feature is wanted. Note that the Breely integration cannot hit this, since it only ever
claims pre-existing group-event holds and practice ice can only exist where no hold was.

**Foreign meeting invites already render oddly, unrelated to this feature.** If anyone puts a sheet
resource on a meeting request today, the attendant may accept it and the app reads the result as
`Other` + `Confirmed` with no group id — showing on the public calendar as an unattributable "Other"
chip, and as five separate chips for a five-room invite. Conflict enforcement is unaffected (the
write path reads all overlapping events regardless of category), and the meeting's subject line does
not leak, because `FromGraphEvent` never reads `Subject`. Reviewed and accepted as rare; noted here
because it was discovered during this discussion and is worth knowing about.
