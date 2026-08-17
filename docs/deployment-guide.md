# GCC Ice & Event Calendar — Deployment Guide

**What this is:** an ordered, do-this-then-this walkthrough for standing up a working instance of this
app from nothing — Microsoft 365 tenant side and Azure side both. Follow the steps in order; later
steps depend on values produced by earlier ones.

**What this is not:** an explanation of *why* the system is built this way. Where a step exists for a
non-obvious reason, it links to `curling-facility-scheduling-architecture.md` (referred to below as
"the architecture doc") rather than repeating the reasoning here.

**Time:** roughly half a day, most of it waiting on Microsoft 365 provisioning and DNS.

---

## Before you start

### Roles you (or someone on call) must hold

| Role | Needed for |
|---|---|
| **Exchange Administrator** | Steps 1–3, 6, 7 (mailboxes, calendar processing, both security groups, Application Access Policy) |
| **Application Administrator** | Step 4 (app registration) |
| **Global Administrator** or **Privileged Role Administrator** | Step 5 only — granting admin consent to Microsoft Graph *application* permissions cannot be delegated below this |
| **Contributor** on an Azure subscription | Steps 9–12 |

The Global Admin involvement is a single click in Step 5. Everything ongoing after deployment
(adding staff, adding a sheet) needs none of these — see "Day-two operations" at the end.

### Tools

```powershell
Install-Module ExchangeOnlineManagement -Scope CurrentUser
```

Plus the **.NET 10 SDK** on whatever machine will build and publish the app, and the repo cloned
locally.

### Fill these in once and paste into each PowerShell session

Every command below uses these variables.

```powershell
$TenantDomain  = 'yourclub.onmicrosoft.com'   # or the tenant's primary mail domain
$SheetParts    = @('sheet1','sheet2','sheet3','sheet4','sheet5')
$ClubEventPart = 'clubevents'
$MailerMailbox = 'scheduler-mailer@yourclub.org'  # licensed account that sends practice ice mail
$MailboxGroup  = 'Facility Mailboxes'         # created in Step 2
$StaffGroup    = 'Facility Scheduler Staff'   # created in Step 2, populated in Step 7

$SheetMailboxes = $SheetParts | ForEach-Object { "$_@$TenantDomain" }
$ClubEvents     = "$ClubEventPart@$TenantDomain"
$AllMailboxes   = @($SheetMailboxes) + $ClubEvents
```

Sheet mailbox local-parts do **not** have to be `sheet1..sheetN` — the app takes an explicit list
(Appendix A). One exception: `provision-categories.ps1` (Step 8) assumes that naming and takes a
`-SheetCount`; a different scheme means editing that one line in the script.

### Run the tests first

They run entirely against in-memory fakes — no tenant, no configuration:

```bash
dotnet test FacilityScheduler.Tests/FacilityScheduler.Tests.csproj
```

CI runs this on every push to `master`, but a local publish can carry uncommitted changes CI never saw.

---

## Step 1 — Create the resource mailboxes

One room mailbox per ice sheet, plus one for Club Events (the whole-club calendar, architecture doc
§4.4). Room mailboxes need no license.

```powershell
Connect-ExchangeOnline

foreach ($part in $SheetParts) {
    New-Mailbox -Room -Name $part -PrimarySmtpAddress "$part@$TenantDomain"
}
New-Mailbox -Room -Name $ClubEventPart -PrimarySmtpAddress $ClubEvents
```

Provisioning is not instant — new mailboxes can take several minutes to become addressable.

### 1a. Calendar processing — turn the Resource Booking Attendant into an auto-decline

**This app is the sole writer to these calendars.** It writes events directly via Graph, so the
Resource Booking Attendant never runs on anything the app does (architecture doc §6.1, D3). But
nothing stops a member or staffer from opening Outlook and adding `sheet1` as a room on a meeting
invite, and what Exchange does with that invite is a real decision:

- **Leave it at the room default (`AutoAccept`)** and Exchange books the sheet itself — bypassing the
  app's conflict check, and producing an event with none of the app's metadata (architecture doc §4.1).
- **Set `None`** and the invite is silently ignored. Safe, but the sender gets no reply at all and
  assumes they have the ice.

Configure it to **decline everything, with an explanatory reply** — safe *and* it tells the sender
where to actually go:

```powershell
foreach ($mb in $AllMailboxes) {
    Set-CalendarProcessing -Identity $mb `
        -AutomateProcessing AutoAccept `
        -AllBookInPolicy $false `
        -AllRequestInPolicy $false `
        -AllRequestOutOfPolicy $false `
        -AddAdditionalResponse $true `
        -AdditionalResponse 'Ice time is not booked by Outlook invite. Please use the club scheduling app, or contact the ice scheduler.'
}
```

With no booking policy permitting anyone, every request is out of policy, and with no delegate to
forward to, the attendant declines it and sends the `AdditionalResponse` text back. Verify on one
mailbox before moving on:

```powershell
Get-CalendarProcessing -Identity $SheetMailboxes[0] |
    Format-List AutomateProcessing, AllBookInPolicy, AllRequestOutOfPolicy, AdditionalResponse
```

### 1b. Enable mailbox audit logging

```powershell
foreach ($mb in $AllMailboxes) { Set-Mailbox -Identity $mb -AuditEnabled $true }
```

The app cannot verify this itself, and it's the only record of a change made outside the app.

---

## Step 2 — Create the two security groups

Two groups, doing unrelated jobs. Keeping them separate matters: **one contains mailboxes, the other
contains people.**

| Group | Contains | Used for |
|---|---|---|
| `$MailboxGroup` | The sheet mailboxes, Club Events, and the mailer mailbox — **no human accounts** | Scoping the app's Graph credential to only these mailboxes (Step 6) |
| `$StaffGroup` | Staff and committee **people** — no mailboxes | Who counts as staff in the app (Step 7), and read-only Outlook access (Step 3) |

Both are created as **mail-enabled security groups**. For `$MailboxGroup` that's mandatory — an
Application Access Policy cannot be scoped by a plain Entra security group. For `$StaffGroup` it's
so `Add-MailboxFolderPermission` in Step 3 will accept it as a principal; a non-mail-enabled Entra
security group is rejected there. Mail-enabled security groups can only be created from Exchange
PowerShell, not the Entra portal.

The **mailer mailbox** goes in `$MailboxGroup` too. Application Access Policies scope by app + group
membership, not by permission, so `Mail.Send` cannot be scoped separately from `Calendars.ReadWrite`
(architecture doc D73). Create the mailer as a normal licensed user account first if it doesn't
exist — it needs an Exchange Online license to send.

```powershell
# Mailbox scope group - mailboxes only
New-DistributionGroup -Name $MailboxGroup -Type Security -PrimarySmtpAddress "facility-mailboxes@$TenantDomain"

foreach ($mb in $AllMailboxes) { Add-DistributionGroupMember -Identity $MailboxGroup -Member $mb }
Add-DistributionGroupMember -Identity $MailboxGroup -Member $MailerMailbox

# Staff group - people only. Populated and delegated in Step 7.
New-DistributionGroup -Name $StaffGroup -Type Security -PrimarySmtpAddress "scheduler-staff@$TenantDomain"
```

`$StaffGroup` gets an email address as a side effect of being mail-enabled. Harmless, and often
useful — but it lands in the GAL, so name it something that makes sense to the whole club.

---

## Step 3 — Grant staff read-only Outlook access

Staff view sheet calendars in Outlook only as an emergency fallback; the app is the operational
interface (architecture doc D2). Reviewer is read-only, which is what protects the sole-writer
invariant.

The permission goes to **`$StaffGroup`** — the group holding people. Granting it to `$MailboxGroup`
would give the resource mailboxes Reviewer on each other and no human anything.

```powershell
foreach ($mb in $AllMailboxes) {
    Add-MailboxFolderPermission -Identity "${mb}:\Calendar" -User $StaffGroup -AccessRights Reviewer
}
```

The group is still empty at this point; that's fine. Permissions attach to the group, so anyone added
in Step 7 inherits this automatically, and anyone removed loses it — no per-person mailbox work ever.

---

## Step 4 — Create the Entra app registration

Portal: **entra.microsoft.com → Applications → App registrations → + New registration**.

- **Name:** e.g. `Facility Scheduler`
- **Supported account types:** *Accounts in this organizational directory only (single tenant)*
- **Redirect URI:** leave blank for now — the app's URL doesn't exist until Step 9.

From the **Overview** blade, record these two; you'll need them repeatedly:

- **Application (client) ID** → becomes both `Graph:ClientId` and `AzureAd:ClientId`
- **Directory (tenant) ID** → becomes both `Graph:TenantId` and `AzureAd:TenantId`

### 4a. Create a client secret

**Certificates & secrets → Client secrets → + New client secret.** Copy the **Value** immediately —
it is never shown again. This becomes both `Graph:ClientSecret` and `AzureAd:ClientSecret`.

Note the expiry date somewhere you'll see it. An expired secret takes the whole app down with a
Graph authentication failure, with no advance warning from the app itself.

### 4b. Leave "Assignment required?" off

On the **Enterprise application** (Entra → Applications → Enterprise applications — a separate linked
object from the App registration; this toggle exists only there), leave **Properties → Assignment
required?** set to **No**.

Access control is enforced inside the app by the staff claim (Step 7), not at the sign-in gate. On
Entra ID Free, assignment accepts only individual users — turning this on would put every member who
wants to host practice ice behind an individual Entra admin action. The accepted exposure is that
anyone already in the tenant can reach `/practice-ice/request` and submit a request, which is
reversible and requires staff approval (architecture doc §6.5).

---

## Step 5 — Grant and consent the Graph permissions

**API permissions → + Add a permission → Microsoft Graph.**

Under **Application permissions** add all five:

| Permission | Used by |
|---|---|
| `Calendars.ReadWrite` | Every booking read and write — the core of the app |
| `MailboxSettings.ReadWrite` | `provision-categories.ps1` (Step 8) writing the master category list |
| `Mail.Send` | Practice ice notification email (skip only if that feature stays off) |
| `GroupMember.Read.All` | Staff group check at sign-in |
| `User.Read.All` | Also the staff group check — `checkMemberGroups` resolves the user object as well as its memberships, and `GroupMember.Read.All` alone returns 403 (live-verified 2026-08-15) |

Under **Delegated permissions**, `User.Read` is added by default and is all that's needed — staff
sign-in is for identity only, and every Graph call runs on the application credential above
(architecture doc §6.2). Do not add delegated calendar scopes.

Then click **Grant admin consent for &lt;tenant&gt;** and confirm every row reads *Granted*. **A
permission that's been added but not consented fails identically to one that was never added** —
same 403, same message.

### 5a. Enable ID tokens

**Authentication → Implicit grant and hybrid flows → check "ID tokens (used for implicit and hybrid
flows)".**

Microsoft.Identity.Web's default response type for a sign-in-only app that calls no downstream API
is `id_token`. If sign-in later fails with `AADSTS700054` ("response_type 'id_token' is not
enabled"), this is the checkbox.

---

## Step 6 — Restrict the app to the facility mailboxes

Without this, the app's credential can read and write **every** mailbox in the tenant. This step is
mandatory (architecture doc §6.3).

```powershell
New-ApplicationAccessPolicy `
    -AppId '<Application (client) ID from Step 4>' `
    -PolicyScopeGroupId "facility-mailboxes@$TenantDomain" `
    -AccessRight RestrictAccess `
    -Description 'Facility Scheduler - facility mailboxes only'
```

Verify positively **and negatively** — the negative test is the one that proves the restriction works:

```powershell
# Expect: AccessCheckResult = Granted
Test-ApplicationAccessPolicy -AppId '<client id>' -Identity $SheetMailboxes[0]
Test-ApplicationAccessPolicy -AppId '<client id>' -Identity $MailerMailbox

# Expect: AccessCheckResult = Denied
Test-ApplicationAccessPolicy -AppId '<client id>' -Identity 'someone.else@yourclub.org'
```

Allow up to ~30 minutes for propagation. See Appendix C if mail later fails with `[RAOP]` despite
`Granted` here.

---

## Step 7 — Populate and delegate the staff group

`$StaffGroup` was created in Step 2 and already carries the Outlook Reviewer permission from Step 3.
This step fills it and hands over its management. Membership in it is what every page except
`/practice-ice/request` requires (architecture doc §6.5).

```powershell
# 1. Add the people
Add-DistributionGroupMember -Identity $StaffGroup -Member 'jane@yourclub.org'
# ...repeat per staff/committee member

# 2. Delegate management to someone who is not an Entra admin
Set-DistributionGroup -Identity $StaffGroup -ManagedBy 'jane@yourclub.org' `
    -MemberJoinRestriction Closed -MemberDepartRestriction Closed

# 3. Get the object id for StaffAccess:StaffGroupId
(Get-DistributionGroup -Identity $StaffGroup).ExternalDirectoryObjectId
```

`ExternalDirectoryObjectId` on an Exchange recipient *is* its Entra object ID — the same GUID the
Entra portal shows — so this needs no separate Graph connection.

Two things to get right:

- **The value must be the object ID (a GUID)**, not the display name and not the SMTP address. A
  display name here produces no error at all — just a silent, permanent "nobody is staff."
- **The `ManagedBy` owner needs no Entra admin role** to add and remove members, which is the entire
  point of doing it this way. The tenant is Entra ID Free, where every native alternative routes each
  staff change back through the two Entra admins.

`MemberJoinRestriction Closed` stops anyone adding themselves to a group that grants staff access.
The default for a new distribution group is `Closed`, but it is worth setting explicitly here rather
than inheriting it.

> **Verify this one before trusting it.** `$StaffGroup` is mail-enabled so Step 3 can use it, and the
> app's staff check (`checkMemberGroups`) resolves groups by directory object ID, which a
> mail-enabled security group has like any other. That should work, but it has not been confirmed
> against a real tenant — and if it doesn't, the failure mode is the staff lockout in Appendix C.
> Step 13's first item is the check. If a mail-enabled group turns out not to resolve, create a plain
> Entra security group for `StaffAccess:StaffGroupId` and leave the mail-enabled one holding only the
> Step 3 calendar permission.


---

## Step 8 — Provision the calendar category palettes

Now that permissions are consented (Step 5) and scoped (Step 6), the category script can run. It
mirrors the app's own colors into Exchange's master category lists so the Outlook fallback view
doesn't show a different scheme. Idempotent — safe to re-run.

```powershell
$env:CURLING_APP_CLIENT_SECRET = '<client secret from Step 4a>'

.\docs\provision-categories.ps1 `
    -TenantId '<directory (tenant) id>' `
    -ClientId '<application (client) id>' `
    -TenantDomain $TenantDomain `
    -SheetCount 5
```

It prints a per-mailbox, per-category result table. Anything marked `FAILED` here almost always means
Step 5 or Step 6 isn't right yet.

**The tenant side is now complete.** Everything below is Azure.

---

## Step 9 — Create the Azure Web App

1. **[portal.azure.com](https://portal.azure.com) → App Services → + Create → Web App.**
2. **Basics:**
   - **Resource Group:** create one, e.g. `facility-scheduler-rg`
   - **Name:** globally unique; becomes `https://<name>.azurewebsites.net`
   - **Publish:** Code · **Runtime stack:** .NET 10 (or latest offered)
   - **Operating System:** Linux or Windows both work; Linux is cheaper at the same tier
   - **Region:** nearest the facility
3. **App Service Plan → Create new → Basic B1.** Do not use Free (F1): it sleeps on inactivity, which
   breaks Blazor Server's persistent SignalR circuit, and has no custom domain support.
4. **Create**, then **Go to resource** and note the URL from the Overview blade.

### 9a. Turn on the settings Blazor Server needs

**Settings → Configuration → General settings:**

- **Web sockets: On** — off by default on Windows App Service; without it Blazor Server silently
  degrades to long polling.
- **Always On: On** — prevents idle unload dropping active circuits.
- **Session affinity (ARR): On** — the default; leave it, or scaling out will break circuits.

---

## Step 10 — Register the redirect URIs

Back in the app registration (Step 4): **Authentication → + Add a platform → Web**, and add both,
using the real URL from Step 9:

- `https://<name>.azurewebsites.net/signin-oidc`
- `https://<name>.azurewebsites.net/signout-callback-oidc`

**Save.** Effective immediately, no restart. Missing this produces `AADSTS50011` on first sign-in.

Repeat this for any custom domain you bind in Step 12.

---

## Step 11 — Set the application configuration

**Settings → Environment variables → + Add**, one row per key. Use the **double-underscore** names
from Appendix A — Azure rejects the colon form outright.

The minimum set for a working instance:

| Name | Value |
|---|---|
| `Graph__TenantId` | directory (tenant) ID |
| `Graph__ClientId` | application (client) ID |
| `Graph__ClientSecret` | client secret from Step 4a |
| `AzureAd__TenantId` | same tenant ID |
| `AzureAd__ClientId` | same client ID |
| `AzureAd__ClientSecret` | same secret |
| `Facility__TenantDomain` | e.g. `yourclub.onmicrosoft.com` |
| `Facility__SheetMailboxLocalParts__0` … `__4` | `sheet1` … `sheet5` — one row each |
| `Facility__ClubEventsMailboxLocalPart` | `clubevents` |
| `Facility__TimeZone` | e.g. `Pacific Standard Time` |
| `StaffAccess__StaffGroupId` | group object ID from Step 7 |
| `AppLog__LogDirectory` | `%HOME%\LogFiles\facility-scheduler` (Windows) or `/home/LogFiles/facility-scheduler` (Linux) |
| `PracticeIce__MailerMailbox` | the mailer address, if practice ice is enabled |
| `PracticeIce__ApproverDistributionEmail` | mail-enabled group notified of new requests |
| `Webhook__BreelySharedSecret` | only if integrating Breely — see Step 14 |

Four of these are **load-bearing**: `Facility__TenantDomain`, `Facility__SheetMailboxLocalParts`,
`Facility__TimeZone`, and `StaffAccess__StaffGroupId`. The app refuses to start without them rather
than running in a silently wrong state.

Three details that bite:

- **Array indices must be contiguous from `0`.** A gap makes .NET's binder stop reading at the hole.
- **`AppLog__LogDirectory` must be outside the deployed app folder.** Left unset it falls back to
  `App_Data/logs` *inside* the deployment, which is replaced on every redeploy — log history vanishes
  with no error (architecture doc §4.9).
- **`Facility__TimeZone` must genuinely be the facility's zone.** Every "today" in the app derives
  from it, and a wrong value shifts the whole app a day forward during the facility's own evening.

Click **Apply → Save** and confirm the restart prompt.

Secrets here are stored as plain settings, visible to anyone with Contributor on the resource. An
**Azure Key Vault reference** (`@Microsoft.KeyVault(SecretUri=…)` as the value) is a worthwhile
later upgrade, not required to get running.

---

## Step 12 — Publish, and bind a domain

Publish, by whichever route suits:

```bash
dotnet publish -c Release -o ./publish
```

then zip `./publish` and either drag it onto **Deployment Center → ZIP Deploy** in the portal, or:

```bash
az webapp deploy --resource-group facility-scheduler-rg --name <your-app-name> --src-path ./publish.zip --type zip
```

Visual Studio's right-click **Publish → Azure App Service** does the same thing interactively. Once
the app is stable, Deployment Center can generate a GitHub Actions workflow that redeploys on push.

**Custom domain (optional but expected for production):**

1. **Custom domains → + Add** → enter the domain → add the TXT/CNAME record Azure shows you at your
   registrar → **Validate**.
2. **Certificates → + Create App Service Managed Certificate** (free, auto-renewing) → bind it.
3. **TLS/SSL settings → HTTPS Only: On.**
4. **Go back to Step 10** and add the new domain's two redirect URIs, or sign-in fails with
   `AADSTS50011` on the custom domain.

---

## Step 13 — Verify

Work through this in order; each item has caught a real failure at least once.

- [ ] **A staff account signs in and `/calendar` loads real data for every sheet.** Signs in but
      every page denies → Appendix C.
- [ ] **A non-staff test account** (signed in, deliberately not in `$StaffGroup`) can reach
      `/practice-ice/request`, and is denied on `/calendar`, `/settings`, `/club-events`, and
      `/practice-ice/approvals`. *This has never been verified against a real tenant* (architecture
      doc §8) — do not skip it before inviting members as guests.
- [ ] `/public/calendar` and `/api/public/availability` both load in a private window, signed out.
- [ ] **Practice ice end to end:** submit a request from `/public/practice-ice`, confirm the hold
      appears on `/calendar` and the approver email arrives; then approve it from
      `/practice-ice/approvals` and confirm the volunteer's confirmation email arrives. Mail failure
      here → Appendix C.
- [ ] **Logging:** take any booking action, open `/settings`, confirm the entry appears; confirm the
      log path is the one from Step 11, not `App_Data/logs`.
- [ ] **Time zone:** confirm the calendar's "Today" is correct *in the facility's evening*, not just
      during the day — that's when a wrong zone shows itself.
- [ ] **Headers:** `curl -I https://<app>/calendar` shows `X-Frame-Options: DENY` and a
      `Content-Security-Policy`; the same against `/public/calendar` shows neither — that page is the
      one deliberate exception, so it can be iframed on the club site.
- [ ] **Outlook invite is declined:** send a meeting invite from Outlook with a sheet as a room and
      confirm the decline reply arrives (Step 1a).
- [ ] **Negative access test still passes:** re-run `Test-ApplicationAccessPolicy` against a mailbox
      outside the group and confirm `Denied`.

---

## Step 14 — Optional: the Breely booking webhook

Bookings taken through Breely, the club's separate customer-facing platform, reach this app only via
this webhook (architecture doc §4.8). Without it configured, they simply never appear here.

1. Generate a strong random secret (32+ random characters) and set it as `Webhook__BreelySharedSecret`.
2. In Breely's webhook configuration, point a webhook at
   `https://<your-app-domain>/api/webhooks/breely` with a custom header `X-Webhook-Secret` set to the
   same value. Breely supports no per-request signature, hence a static secret (architecture doc §6.4).
3. Confirm an unauthenticated `POST` to that URL returns `401`.
4. Take a real test booking through Breely and confirm it lands on the correct sheet. If it lands on
   the fallback sheet with a `⚠ Web booking needs review` marker, no open Group Event hold covered
   that window.
5. **Rotating the secret means changing it in both places at once** — a mismatch fails closed.

---

## Day-two operations

| Task | What's involved | Needs an Entra admin? |
|---|---|---|
| Add or remove a staff member | Add/remove them in `$StaffGroup`. Takes effect **on their next sign-in** — tell them to sign out and back in. | No — a group Owner can do it |
| Add a new ice sheet | New mailbox (Step 1 + 1a + 1b), add to `$MailboxGroup` (Step 2), Reviewer permission (Step 3), re-run the category script (Step 8), add one `Facility__SheetMailboxLocalParts__N` row (Step 11) | No |
| Rotate the client secret | New secret in Step 4a, update `Graph__ClientSecret` and `AzureAd__ClientSecret` (Step 11) | Application Administrator |
| Renew before expiry | Watch the Step 4a expiry date — an expired secret is a full outage | Application Administrator |

---

## Appendix A — Configuration reference

Two notations: the **JSON form** (`Graph:TenantId`, for `appsettings.json` and user-secrets) and the
**environment-variable form** (`Graph__TenantId`, double underscore) required by Azure and any other
env-var host. Nothing here is baked into source (architecture doc §4.6).

| JSON key | Env-var name | What it is | Required |
|---|---|---|---|
| `Graph:TenantId` | `Graph__TenantId` | Directory (tenant) ID | Yes |
| `Graph:ClientId` | `Graph__ClientId` | Application (client) ID | Yes |
| `Graph:ClientSecret` | `Graph__ClientSecret` | Client secret — **secret** | Yes |
| `AzureAd:Instance` | `AzureAd__Instance` | `https://login.microsoftonline.com/` — already set in `appsettings.json` | Preset |
| `AzureAd:TenantId` | `AzureAd__TenantId` | Same tenant ID, for staff sign-in | Yes |
| `AzureAd:ClientId` | `AzureAd__ClientId` | Same client ID, for staff sign-in | Yes |
| `AzureAd:ClientSecret` | `AzureAd__ClientSecret` | Same secret — **secret** | Yes |
| `Facility:TenantDomain` | `Facility__TenantDomain` | Mailbox domain, e.g. `yourclub.onmicrosoft.com` | **Load-bearing** |
| `Facility:SheetMailboxLocalParts` | `Facility__SheetMailboxLocalParts__0`, `__1`, … | Explicit list of sheet mailbox local-parts, not a count. Indices contiguous from `0`. | **Load-bearing** |
| `Facility:ClubEventsMailboxLocalPart` | `Facility__ClubEventsMailboxLocalPart` | Defaults to `clubevents` | No |
| `Facility:TimeZone` | `Facility__TimeZone` | Windows time zone ID, e.g. `Pacific Standard Time` | **Load-bearing** |
| `Facility:Name` | `Facility__Name` | Display name — accepted but not yet wired to any UI | No |
| `Facility:LogoPath` | `Facility__LogoPath` | Path under `wwwroot` — accepted but not yet wired to any UI | No |
| `StaffAccess:StaffGroupId` | `StaffAccess__StaffGroupId` | Staff group **object ID** (GUID). Not a secret. | **Load-bearing** |
| `PracticeIce:MailerMailbox` | `PracticeIce__MailerMailbox` | Mailbox that sends notifications | Practice ice only |
| `PracticeIce:ApproverDistributionEmail` | `PracticeIce__ApproverDistributionEmail` | Group notified of new requests | Practice ice only |
| `PracticeIce:EligibleStartHour` / `EligibleEndHour` | `PracticeIce__EligibleStartHour` / `__EligibleEndHour` | Bookable hours, 0–24, Start < End. Default `6`/`22` | No |
| `PracticeIce:MinLeadHours` | `PracticeIce__MinLeadHours` | Minimum notice. Default `48` | No |
| `PracticeIce:MaxHorizonDays` | `PracticeIce__MaxHorizonDays` | How far out slots appear. Default `30` | No |
| `Webhook:BreelySharedSecret` | `Webhook__BreelySharedSecret` | Breely's `X-Webhook-Secret` value — **secret** | Breely only |
| `AppLog:LogDirectory` | `AppLog__LogDirectory` | Absolute path, **outside** the deployed app folder | Strongly recommended |
| `AppLog:RetentionDays` | `AppLog__RetentionDays` | Rotated files kept. Default `30` | No |

**Load-bearing** = the app throws at startup rather than running misconfigured. The two `PracticeIce`
mail addresses are softer: the app boots without them, but request submission is blocked with an
explicit message rather than creating a hold nobody is notified about.

**Keep real values out of the tracked `appsettings.json`** — it ships blank placeholders only. That
includes the two `PracticeIce` addresses, which aren't secrets but still don't belong committed. Use
user-secrets locally; `dotnet user-secrets set "Graph:ClientSecret" "…"` from the project folder.

---

## Appendix B — Hosting somewhere other than Azure

This is a standard ASP.NET Core Blazor Server app with no cloud-specific dependency. Any host needs:

- **.NET 10 ASP.NET Core runtime**
- **Outbound HTTPS** to `graph.microsoft.com` and `login.microsoftonline.com` — nothing else external
  is called
- **HTTPS termination**, with **WebSocket support passed through** if a reverse proxy is in front —
  Blazor Server's circuit needs it
- **A secret-injection mechanism** — environment variables (double-underscore form) or a mounted
  config file
- **A process supervisor** — systemd, a container restart policy, or equivalent
- **A writable log directory** outside the deployed app folder

Steps 1–8 (the entire tenant side) and Appendix A apply unchanged; only Steps 9–12 are Azure-specific.

---

## Appendix C — Troubleshooting

### Signed in successfully, but every page is denied

The staff group check fails **closed** by design — a Graph error means "not staff," never "staff" —
so sign-in completes normally and this looks like an authentication problem when it isn't.

**The Azure log stream will not tell you why.** Nothing on this path writes to `ILogger`. The answer
is in the app's own log file, and the Settings page that would show it is itself behind the policy
you're locked out of. Read the file directly over SSH or Kudu (`https://<app>.scm.azurewebsites.net`):

```bash
tail -50 <AppLog:LogDirectory>/app-*.log
```

| What you find | Cause |
|---|---|
| `StaffGroupCheckFailed` + `Insufficient privileges to complete the operation` | Step 5 — a permission missing, or added without admin consent. By far the most common cause. |
| `StaffGroupCheckFailed` + anything else | The `details=` value is the raw Graph error. |
| No failure, but a `StaffSignIn` entry (Debug tier) | The check worked and said "not a member." Either the account really isn't in the group, or `StaffAccess:StaffGroupId` holds a display name instead of the object ID GUID — which produces no error, just a permanent silent "not staff." |
| **No new log entry at all** | No sign-in is happening — see below. |

### "Works in a private window but not my normal browser"

The staff claim is written into the auth cookie at sign-in. A cookie issued before the group existed
(or before any authorization change) keeps its old claims and is happily accepted as authenticated
while every page denies. Restarting the app changes nothing — none of this is server-side. Restarting
the browser often doesn't either, because "continue where you left off" preserves session cookies.

Recovery: visit `https://<app>/MicrosoftIdentity/Account/SignOut` and sign back in; failing that,
**F12 → Application → Cookies →** delete the `.AspNetCore.*` cookies.

The same property is why **adding someone to the staff group takes effect on their next sign-in**.

### Mail fails with `[RAOP] : Blocked by tenant configured AppOnly AccessPolicy settings`

That's the Application Access Policy (Step 6), not a `Mail.Send` consent problem — a missing consent
fails earlier with a different error. Confirm which group is actually in scope; the exact property
names vary by module version, so ask for all of them:

```powershell
Get-ApplicationAccessPolicy | Format-List *
Test-ApplicationAccessPolicy -AppId '<client id>' -Identity $MailerMailbox
```

**If `Test-ApplicationAccessPolicy` says `Granted` and sends still fail, the configuration is already
correct — just wait and retry.** Group-*membership* changes to an existing policy propagate on a
slower cache than the diagnostic cmdlet reads, well past Microsoft's quoted ~30 minutes in a real
observed case (2026-08-11). There is no way to force that flush; re-diagnosing from scratch wastes
the time instead.

### `AADSTS50011: The redirect URI … does not match`

Step 10, or Step 12 if you've just bound a custom domain.

### The app won't start at all

Check the log stream for an `InvalidOperationException` naming a specific setting — that's the
fail-fast validation on the four load-bearing keys in Appendix A working as intended.
