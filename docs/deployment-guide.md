# GCC Ice & Event Calendar — Deployment / Installation Guide

**Audience:** whoever is standing up a new instance of this app (a new tenant, a new environment, or a fresh facility entirely). Written assuming no prior Azure experience.
**Companion to:** `curling-facility-scheduling-architecture.md` (design), `provision-categories.ps1` (tenant provisioning script).

---

## 1. Prerequisites

Before deploying the app itself, the tenant side needs to exist.

### 1.1 Entra ID app registration

In the target M365 tenant's Entra admin center (entra.microsoft.com → **Applications → App registrations → New registration**):

- **Delegated** `Calendars.Read` (or similar) scope, for staff sign-in (identity/audit only — see the architecture doc §6.2, Graph itself always runs on the app-only credential below).
- **Application** Graph calendar scopes (e.g. `Calendars.ReadWrite`), admin-consented, for the service identity that does all actual Graph work.
- A client secret (or certificate) generated for the application credential (**Certificates & secrets** tab → **New client secret** → copy the value immediately, it's shown only once).

### 1.2 Leave "Assignment required?" off

On the **Enterprise application** (Entra admin center → Identity → Applications → Enterprise
applications — *not* App registrations; these are two separate linked objects and this toggle only
exists on the Enterprise Application's Properties tab), leave **Assignment required?** set to **No**.

Access control happens in the app, not at the sign-in gate: the staff claim (§2.5, architecture
doc §6.5) checks per page, so a signed-in non-staff member reaches only `/practice-ice/request` and
nothing else. Turning this on would mean **every member wanting to host practice ice needs an
individual Entra admin action just to sign in** — because on Entra ID Free, assignment only accepts
individual users, not security groups (live-confirmed 2026-08-12; group assignment to an Enterprise
Application is a P1+ feature, on this screen and on App Roles alike).

**Accepted exposure:** anyone already in the tenant for an unrelated reason could also reach
`/practice-ice/request` and submit a request. That request is reversible, staff-visible, and requires
approval before it means anything (architecture doc §5.4.4) — not worth the per-member admin
bottleneck the alternative creates.

### 1.3 Resource mailboxes provisioned

One per sheet, plus one Club Events mailbox — scoped to a dedicated security group, with the app registration's application permission constrained to that group via an Application Access Policy or RBAC for Applications (architecture doc §6.3). Run `docs/provision-categories.ps1` against the tenant once the mailboxes exist, to set up the master category lists:
```powershell
$env:CURLING_APP_CLIENT_SECRET = '<client secret>'
.\provision-categories.ps1 -TenantId '<tenant id>' -ClientId '<app registration client id>' -TenantDomain '<tenant>.onmicrosoft.com' -SheetCount 5
```

### 1.4 Runtime

**.NET 10 ASP.NET Core runtime** available wherever the app will run — the exact install mechanism depends on the host; see §3 (Azure) or §4 (IIS) below.

### 1.5 Run the test suite before publishing

`FacilityScheduler.Tests` (architecture doc §11) runs against an in-memory fake, not a real tenant — no configuration needed:

```
dotnet test FacilityScheduler.Tests/FacilityScheduler.Tests.csproj
```

CI (`.github/workflows/tests.yml`) runs this on every push/PR to `master`; run it locally too before a manual publish (§3.3/§4) since CI passing on `master` doesn't guarantee an uncommitted local change still passes.

---

## 2. Configuration Reference

Every value the app needs, and where it belongs. None of this is baked into source — see the architecture doc §4.6.

Two notations appear below for each key: the **JSON form** (`Graph:TenantId`, as it'd appear nested in an `appsettings.json` file or user-secrets) and the **environment-variable form** (`Graph__TenantId`, double underscore). **Any host that sets configuration via environment variables — Azure App Service's Environment variables blade included — requires the double-underscore form.** Colons are not valid in an environment variable name and Azure will reject them; this corrects an earlier version of this doc that claimed Azure accepted both.

| Key (JSON form) | Environment-variable form | What it is | Secret? | Local dev | Production |
|---|---|---|---|---|---|
| `Graph:TenantId` | `Graph__TenantId` | Entra tenant (directory) ID for the app registration | No | user-secrets or `appsettings.Development.json` | App Service Environment variables (or host equivalent) |
| `Graph:ClientId` | `Graph__ClientId` | App registration client ID (application credential) | No | same | same |
| `Graph:ClientSecret` | `Graph__ClientSecret` | App registration client secret | **Yes** | user-secrets only, never committed | App Service Environment variables, marked as a "slot setting"/secret if the host supports it |
| `AzureAd:Instance` | `AzureAd__Instance` | Always `https://login.microsoftonline.com/` | No | `appsettings.json` (already set) | same |
| `AzureAd:TenantId` | `AzureAd__TenantId` | Entra tenant ID for staff SSO (delegated sign-in) | No | user-secrets | App Service Environment variables |
| `AzureAd:ClientId` | `AzureAd__ClientId` | App registration client ID for staff SSO (delegated scope) | No | user-secrets | App Service Environment variables |
| `AzureAd:ClientSecret` | `AzureAd__ClientSecret` | Client secret for the delegated sign-in flow | **Yes** | user-secrets only | App Service Environment variables |
| `Facility:TenantDomain` | `Facility__TenantDomain` | The tenant's mailbox domain, e.g. `contoso.onmicrosoft.com` | No | `appsettings.Development.json` | App Service Environment variables |
| `Facility:SheetMailboxLocalParts` | `Facility__SheetMailboxLocalParts__0`, `__1`, `__2`, … | Array of sheet mailbox local-parts — not a count, an explicit list | No | same | same — see §2.1 for the exact keys, and a worked example |
| `Facility:ClubEventsMailboxLocalPart` | `Facility__ClubEventsMailboxLocalPart` | Local-part of the Club Events mailbox (default `clubevents`) | No | same | same |
| `Facility:TimeZone` | `Facility__TimeZone` | Windows time zone ID the facility operates in (e.g. `Pacific Standard Time`) | No | same | same |
| `Facility:Name` | `Facility__Name` | Facility display name | No | optional, currently inert (no UI wiring yet) | optional |
| `Facility:LogoPath` | `Facility__LogoPath` | Relative path under `wwwroot` to a logo image (e.g. `/branding/logo.png`) | No | optional, currently inert | optional — the actual image file must be placed under `wwwroot` in the deployed app if set |
| `Webhook:BreelySharedSecret` | `Webhook__BreelySharedSecret` | Shared secret Breely must send in the `X-Webhook-Secret` header on every call to `/api/webhooks/breely` (architecture doc §4.8/§5.5) | **Yes** — this is the only thing gating a write-capable anonymous endpoint | user-secrets only, never committed | App Service Environment variables, marked as a "slot setting"/secret if the host supports it |
| `AppLog:LogDirectory` | `AppLog__LogDirectory` | Absolute path where the app's rotating activity/debug log files live (architecture doc §4.9) | No | `App_Data/logs` (relative, under the project folder) is fine locally | **Must** be set to a path outside the deployed app folder — see §2.3 below |
| `AppLog:RetentionDays` | `AppLog__RetentionDays` | How many days of rotated log files to keep before automatic deletion | No | optional, defaults to `30` | optional |
| `PracticeIce:EligibleStartHour` / `EligibleEndHour` | `PracticeIce__EligibleStartHour` / `__EligibleEndHour` | Hours (0-24, Start < End) practice ice may be requested within, each day | No | optional, defaults to `6`/`22` | optional |
| `PracticeIce:MinLeadHours` | `PracticeIce__MinLeadHours` | Minimum hours in advance a slot must be requested | No | optional, defaults to `48` | optional |
| `PracticeIce:MaxHorizonDays` | `PracticeIce__MaxHorizonDays` | How many days out slots are offered | No | optional, defaults to `30` | optional |
| `PracticeIce:ApproverDistributionEmail` | `PracticeIce__ApproverDistributionEmail` | Mail-enabled group notified when a member submits a request | No, but see below | not a secret, but keep out of the tracked `appsettings.json` regardless — see the note below the table | App Service Environment variables |
| `PracticeIce:MailerMailbox` | `PracticeIce__MailerMailbox` | Mailbox the app sends practice ice notifications as, via Graph `Mail.Send` (application permission) | No, but see below | same | App Service Environment variables |
| `StaffAccess:StaffGroupId` | `StaffAccess__StaffGroupId` | Entra object id of the security group whose members get the staff claim (architecture doc §6.5) | No, but **load-bearing** — the app refuses to start without it, same tier as `Facility:TenantDomain` | `appsettings.Development.json` is fine (not a secret — an object id, not a credential) | App Service Environment variables |

**`Facility:TenantDomain`, `Facility:SheetMailboxLocalParts`, `Facility:TimeZone`, and `StaffAccess:StaffGroupId` are load-bearing** — the app fails fast at startup with a clear error if any is missing, rather than starting in a silently-broken state. For `StaffAccess:StaffGroupId` (§2.5) the reason is specific: unlike a feature-level setting, leaving it blank would lock everyone, including real staff, out of every staff page. `Webhook:BreelySharedSecret` and `AppLog:LogDirectory` are not load-bearing in that sense (the app starts fine without either) but each has a consequence if left unset: `/api/webhooks/breely` rejects every request with `401` (see §2.2 below), and the activity log falls back to a path that's lost on every redeploy (see §2.3 below). `PracticeIce:ApproverDistributionEmail`/`MailerMailbox` are the same shape again — the app boots fine blank, but practice ice submission is blocked outright (rather than silently accepting a hold nobody gets notified about) until both are set; see §2.4.

**A note on the two `PracticeIce` mail addresses specifically:** neither is a secret the way a client secret is, but real values still don't belong committed into the tracked `appsettings.json` — that file is meant to carry only the blank placeholders shipped in the repo, same as every other section above. It's easy to forget this while iterating locally (live-hit 2026-08-11); double-check `git diff appsettings.json` before committing if you've been testing with real addresses filled in.

### 2.1 Representing the `SheetMailboxLocalParts` array as environment variables

.NET configuration flattens JSON arrays into indexed keys, joined with `__` (double underscore — .NET's section-separator convention for environment variables, and the only form Azure App Service's Environment variables blade accepts; a colon in the name will be rejected). **Worked example for this project's actual 5-sheet setup** — these are the exact name/value pairs to add, one row each, in Azure's Environment variables blade (or any other host's env-var mechanism):

| Name | Value |
|---|---|
| `Facility__SheetMailboxLocalParts__0` | `sheet1` |
| `Facility__SheetMailboxLocalParts__1` | `sheet2` |
| `Facility__SheetMailboxLocalParts__2` | `sheet3` |
| `Facility__SheetMailboxLocalParts__3` | `sheet4` |
| `Facility__SheetMailboxLocalParts__4` | `sheet5` |
| `Facility__ClubEventsMailboxLocalPart` | `clubevents` |
| `Facility__TenantDomain` | *(your tenant's `.onmicrosoft.com` domain)* |
| `Facility__TimeZone` | `Pacific Standard Time` |

The index (`__0`, `__1`, …) must be contiguous starting from `0` with no gaps, or .NET's configuration binder will stop reading the array at the first missing index. If a sheet is ever added or removed, add/remove exactly one indexed row here — nothing else in configuration changes.

### 2.2 Setting up the Breely booking webhook

`/api/webhooks/breely` (architecture doc §4.8/§5.5) is how bookings taken through the Breely booking platform get reflected onto this app's calendar. It's optional in the sense the app runs fine without it configured — but until it is, Breely bookings simply won't appear here at all, since nothing else feeds them in.

1. Generate a strong random secret (e.g. `openssl rand -base64 32`, or any password generator producing 32+ random characters — this doesn't need to be memorable, only unguessable) and set it as `Webhook:BreelySharedSecret` per the table above.
2. In Breely's own admin/webhook configuration for this club's account, configure a webhook pointing at `https://<your-app-domain>/api/webhooks/breely`, with a custom header `X-Webhook-Secret` set to the same value generated in step 1. (Breely's webhook configuration only supports a fixed URL, static custom headers, and a body — there's no per-request signature to configure, hence the static-secret approach rather than HMAC; see architecture doc §6.4 for why that's an accepted trade-off.)
3. Trigger a real test booking through Breely and confirm it appears on the correct sheet's calendar in this app. If it lands on the fallback sheet with a `⚠ Web booking needs review` Club Event marker instead, that means no open Group Event hold matched the booking's window — check that a hold actually exists on some sheet covering that time.
4. If the secret is ever rotated, update it in both places (this app's configuration, and Breely's webhook header) at the same time — a mismatch fails closed (`401`, request dropped), it doesn't fall back to unauthenticated.

### 2.3 Setting up the activity/debug log (Settings page)

`AppLog:LogDirectory` (architecture doc §4.9) controls where the app's rotating log files and the persisted logging-level marker live. **Left unset, it falls back to `App_Data/logs` under the deployed app folder** — fine for local dev, but wrong for Azure App Service: that folder is part of the deployed content and gets replaced on every redeploy/zip-deploy, silently losing log history with no error. As of 2026-08-03 this same directory also holds a small `booking-policy.txt` marker for the Settings page's "Minimum group event booking interval" field — nothing to configure separately, just don't delete unrecognized small text files from this folder.

1. Pick a path outside the app's own deployment folder:
   - **Azure App Service:** use the persistent storage share every instance already has, e.g. `%HOME%\LogFiles\facility-scheduler` (on Windows App Service; adjust for Linux App Service's equivalent persistent path under `/home`). This survives redeploys because it isn't part of the deployed content — only `wwwroot`/the app folder is replaced.
   - **IIS / other hosts:** any folder outside the deployed app directory that the app pool identity (§4.7) has write access to, e.g. `C:\FacilityScheduler-Logs`.
2. Set `AppLog:LogDirectory` to that path per §2's table (environment variable form: `AppLog__LogDirectory`).
3. Optionally set `AppLog:RetentionDays` if 30 days isn't the right window for how long you want to keep rotated files around.
4. After deploying, sign in and open **Settings** (`/settings`) — confirm a log entry appears after taking any action (create/edit/cancel a booking), and that the level toggle saves and takes effect immediately.

No action is needed for the app to function without this configured — the fallback just means log history won't survive a redeploy, which defeats the point of having it in production.

### 2.4 Setting up practice ice hosting's notification email

Practice ice hosting (architecture doc §5.4.4) sends email via Graph `Mail.Send` from the mailbox
set as `PracticeIce:MailerMailbox`. This needs two things beyond the config table above, and the
second one is the part actually worth reading carefully — it's what took the longest to diagnose the
first time through.

1. **Grant `Mail.Send`** as an **application** permission (not delegated) on the same Entra app
   registration used for `Graph:ClientId`/`Calendars.ReadWrite` (§1.1), with admin consent. This app
   only ever uses app-only Graph calls (§6.2 of the architecture doc), so there's no separate
   delegated flow to configure.
2. **Add the mailer mailbox to the same Application Access Policy scope group** that already
   restricts `Calendars.ReadWrite` to the sheet + Club Events mailboxes (§7 step 2/6 of the
   provisioning checklist). Application Access Policies scope by **app + group membership, not by
   permission** — there's no way to grant `Mail.Send` on a narrower set of mailboxes than
   `Calendars.ReadWrite` already covers without creating an entirely separate policy and group, and
   for a single low-volume mailer that's not worth the extra moving part. The one real trade-off:
   the app also technically gains `Calendars.ReadWrite` on the mailer mailbox itself once it joins
   the group — harmless (the app has no reason to ever call it) but worth knowing rather than
   discovering later.

**Diagnosing it, if mail sends fail with an error like:**

```
Access to OData is disabled: [RAOP] : Blocked by tenant configured AppOnly AccessPolicy settings.
```

That's Exchange's Application Access Policy layer, not a `Mail.Send` consent problem (a missing
consent grant fails earlier, before reaching Exchange, with a different error). First, confirm which
group is actually in scope — the exact property name returned varies by Exchange Online Management
module version, so ask for everything rather than guessing a specific one:

```powershell
Connect-ExchangeOnline
Get-ApplicationAccessPolicy | Format-List *
```

Look for `ScopeName`/`ScopeIdentity` (or `PolicyScopeGroupId` on older module versions) matching the
`AppId` equal to `Graph:ClientId`. Add the mailer mailbox as a member of that group:

```powershell
Add-DistributionGroupMember -Identity "<the group name>" -Member "<mailer mailbox address>"
```

(If that errors because the group is a Microsoft 365 Group rather than a distribution/mail-enabled
security group, use `Add-UnifiedGroupLinks -Identity "<group>" -LinkType Members -Links "<mailer mailbox address>"` instead.)

Then verify directly, rather than just retrying and guessing whether it worked:

```powershell
Test-ApplicationAccessPolicy -AppId <Graph:ClientId> -Identity "<mailer mailbox address>"
```

`AccessCheckResult: Granted` confirms the *current* directory/policy state is correct — but this is
where the real gotcha is: **the live Graph/Exchange enforcement path that actually throws `[RAOP]`
does not necessarily share a cache with this diagnostic cmdlet.** A real case (2026-08-11) showed
`Granted` here well within Microsoft's quoted ~30-minute propagation window, while the actual
`sendMail` call kept failing for some time afterward — group-*membership* changes to an existing
policy appear to propagate on a slower, separate cache than the diagnostic check reflects. There is
no customer-facing way to force that cache to flush. If `Test-ApplicationAccessPolicy` says
`Granted` and it's still failing, the fix is already correct — just retry again later rather than
re-diagnosing from scratch.

No action is needed for the app to function without any of this configured — practice ice submission
is blocked with an explicit "not accepted yet" message (`PracticeIceMailConfigured`, §2 above) rather
than silently creating an unnotified hold.

### 2.5 Setting up staff vs. member authorization

**This one is not optional the way §2.4 was.** Per architecture doc §6.5, every page
except `/practice-ice/request` requires the staff claim, decided by live Entra group membership
at sign-in — and `StaffAccess:StaffGroupId` is load-bearing (§2 above): the app will not start until
it's set to a real group.

1. **Create a security group for staff** — a *separate* group from the mailbox-scoping one in the
   provisioning checklist §7 step 2 (that one governs mailbox access; this one governs app
   authorization — don't reuse it, or every mailbox in the scoping group would need to double as a
   staff account). Add every current staff/committee member.
2. **Delegate Ownership** of that group to at least one person who isn't one of the tenant's Entra
   admins — Owners can add/remove Members with no Entra admin role at all, which is the entire point:
   this tenant is Entra ID Free, so the native alternative (assigning a group to an Entra App Role)
   isn't available, and the fallback (assigning individual users to a role directly) would put every
   staff change back on the tenant's two Entra admins.
3. **Grant `GroupMember.Read.All`** as an application permission on the same app registration used
   for `Graph:ClientId` (§1.1), with admin consent. This is a directory (Entra ID) permission, not an
   Exchange Online one — it is **not** subject to Application Access Policy scoping the way
   `Calendars.ReadWrite`/`Mail.Send` are (§2.4), so there's no group-membership step to add it to.
4. **Set `StaffAccess:StaffGroupId`** to the group's object id from step 1 (`Get-MgGroup` /
   Entra admin center → Groups → the group → Object Id). Not a secret — safe to set directly in
   `appsettings.Development.json` locally, same as `Facility:TenantDomain`.

**Ongoing staff changes are just group membership from here on** — add or remove someone from the
group (Exchange/Entra admin center, or PowerShell), and their access changes on their next sign-in.
No further app configuration, redeploy, or Entra admin action needed for a routine staff change.

**Verify before relying on this in production**, with a real non-staff test account (a guest who is
signed in but deliberately *not* in the staff group): confirm `/practice-ice/request` works, and that
`/calendar`, `/settings`, `/club-events`, and `/practice-ice/approvals` all correctly deny access. The
mechanism that makes `/practice-ice/request` reachable while everything else isn't (a per-page
authorization policy overriding the app's stricter default) has not been confirmed against a real
sign-in as of this writing — architecture doc §6.5/§8 has the full explanation of why this specific
check matters more than most.

**Known, deliberately deferred:** the top staff nav currently shows links to Calendar/Settings/
Practice Ice Requests to every signed-in user, including non-staff members — clicking correctly
denies them, but a visibly dead link is poor UX. Not yet built.

---

## 3. Path A — Azure App Service

A step-by-step walkthrough assuming you haven't used Azure before. If you already have an Azure subscription, skip to §3.2.

### 3.1 Get an Azure subscription

If the club doesn't already have one, go to [azure.microsoft.com/free](https://azure.microsoft.com) and create an account (a credit card is required for verification even on free-tier usage, but this app's actual resource needs are small — see §3.3 on pricing tier). If the club already uses Microsoft 365, you can usually create an Azure subscription under the same tenant from the Azure portal directly.

### 3.2 Create the Web App

1. Go to **[portal.azure.com](https://portal.azure.com)** and sign in.
2. In the search bar at the top, type **"App Services"** and select it.
3. Click **+ Create → Web App**.
4. Fill out the **Basics** tab:
   - **Subscription**: your subscription.
   - **Resource Group**: click **Create new**, give it a name like `facility-scheduler-rg`. (A resource group is just a folder that groups related Azure resources together for billing/management — nothing to configure inside it yet.)
   - **Name**: a globally unique name (becomes `https://<name>.azurewebsites.net` unless you bind a custom domain later — see §3.5).
   - **Publish**: **Code**.
   - **Runtime stack**: **.NET 10** (or the closest available version at deploy time — pick the latest .NET LTS/STS offered).
   - **Operating System**: either Linux or Windows work; Linux is typically cheaper at the same tier.
   - **Region**: pick whichever is geographically closest to the facility/staff.
5. Under **App Service Plan**, click **Create new**. This is the compute tier the app actually runs on:
   - **Pricing plan**: select **Basic B1**. This app's actual concurrency (1–2 staff, plus light anonymous public traffic against the rate-limited public endpoints) is comfortably served by the smallest paid tier — B1 is enough. Avoid the **Free (F1)** tier for anything beyond a quick test: it has no custom domain/SSL support and the app "sleeps" after inactivity, which breaks Blazor Server's live SignalR connections.
6. Click **Review + create**, then **Create**. Wait for the deployment notification (usually 1–2 minutes), then click **Go to resource**.
7. **Note the app's URL** (shown on the Overview blade, e.g. `https://<name>.azurewebsites.net`) and immediately register it as a redirect URI — see §3.2.1. Skipping this until after you try to sign in produces `AADSTS50011: The redirect URI ... does not match the redirect URIs configured for the application`, live-hit during this project's own first deployment.

### 3.2.1 Register the redirect URIs

The app registration from §1.1 needs to know exactly which URL(s) it's allowed to send the Microsoft sign-in response back to — this is a security control (it stops a login response from being redirected somewhere an attacker controls), not paperwork, so Entra rejects anything not on the list rather than warning about it.

1. In the Entra admin center, go to **Identity → Applications → App registrations**, open the registration from §1.1.
2. Open **Authentication** in the left menu. Add a **Web** platform if none exists yet (**+ Add a platform → Web**).
3. Under **Redirect URIs**, add both, using the app's actual URL from step 7 above:
   - `https://<name>.azurewebsites.net/signin-oidc`
   - `https://<name>.azurewebsites.net/signout-callback-oidc`
4. **Save**. No app restart needed — this takes effect immediately.

**Revisit this step whenever the app's externally-visible URL changes** — binding a custom domain (§3.5) means adding that domain's `/signin-oidc` and `/signout-callback-oidc` here too; the `azurewebsites.net` ones can stay registered alongside it if you still want the default URL to work.

### 3.3 Publish the app

Pick whichever fits your comfort level:

- **Visual Studio (easiest if you have it open already)**: right-click the project → **Publish** → **Azure** → **Azure App Service (Windows/Linux)** → sign in and pick the Web App you just created → **Publish**. Visual Studio handles the build and upload for you.
- **Command line**:
  ```
  dotnet publish -c Release -o ./publish
  ```
  then either `az webapp deploy --resource-group facility-scheduler-rg --name <your-app-name> --src-path ./publish.zip --type zip` (after zipping the `./publish` folder), or drag-and-drop ZIP deploy through **Deployment Center** in the portal.
- **GitHub Actions**: if the code lives in GitHub, the App Service **Deployment Center** blade can generate a ready-made workflow file that redeploys automatically on every push — worth setting up once the app is stable, not required for a first deployment.

### 3.4 Set Application Settings

1. In the App Service resource, find **Settings → Environment variables** (newer portal) or **Configuration → Application settings** (classic portal — both are the same underlying feature).
2. Click **+ Add** for each key from the §2 table's **environment-variable form** (`Graph__TenantId`, double underscore) as the **name**. Azure's Environment variables blade rejects colons in the name outright — the double-underscore form is the only one that works here, regardless of which portal view you're using.
3. For `Facility:SheetMailboxLocalParts`, add the indexed rows from §2.1's worked example (`Facility__SheetMailboxLocalParts__0` = `sheet1`, `__1` = `sheet2`, etc.).
4. Click **Apply**, then **Save** at the top. App Service will prompt to restart the app to pick up the change — confirm.
5. **Secret values** (`Graph:ClientSecret`, `AzureAd:ClientSecret`): these are stored as plain application settings by default, visible to anyone with Contributor access to the resource (masked behind a "click to show" toggle in the UI, not encrypted from an authorized viewer). For stronger protection, consider an **Azure Key Vault reference** instead (`@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<name>)` as the setting's value) — a worthwhile upgrade later, not required to get running.

### 3.5 Custom domain + HTTPS

1. **Custom Domains** blade → **+ Add custom domain** → enter the domain (e.g. `booking.yourclub.org`) → follow the on-screen instructions to add the TXT/CNAME record Azure shows you at your domain registrar → **Validate**.
2. **Certificates** blade → **+ Create App Service Managed Certificate** (free, auto-renewing) → bind it to the custom domain from step 1.
3. **TLS/SSL settings** blade → toggle **HTTPS Only** to **On**, so plain HTTP requests are always redirected.
4. **Go back to §3.2.1** and add this new domain's `/signin-oidc` and `/signout-callback-oidc` redirect URIs — sign-in will fail with `AADSTS50011` on the custom domain until you do.

### 3.6 Restart and verify

**Overview** blade → **Restart**, then work through §6 (Post-Deploy Verification Checklist) below. If something doesn't come up correctly, the **Log stream** blade (under **Monitoring**) shows live console output from the app — the most useful first place to look.

---

## 4. Path B — IIS on Windows Server (on-premises)

An alternative to Azure if the facility already has (or wants) an on-premises Windows server. This section also covers reducing IIS/Windows Server's configuration footprint to the minimum this app actually needs — fewer installed roles and features means less attack surface and less running overhead, without giving anything up functionally.

### 4.1 Prerequisites

- **Windows Server 2019 or 2022** (Server Core, without the Desktop Experience GUI, is the lower-footprint choice if you're comfortable managing IIS remotely via IIS Manager from another machine or via PowerShell — Server with Desktop Experience is easier if this will be managed locally at the console).
- A TLS certificate for the server's hostname — an internal CA certificate, a purchased certificate, or a free one via [win-acme](https://www.win-acme.com/) (an ACME/Let's Encrypt client for Windows) if the server has a real public hostname.

### 4.2 Install IIS with a minimal role-service set

Rather than the full IIS role (which includes FTP, extra authentication providers, and legacy modules this app doesn't use), install only what's needed. Via **Server Manager → Add Roles and Features → Web Server (IIS)**, select just:

- **Web Server → Common HTTP Features**: Static Content, Default Document, HTTP Errors.
- **Web Server → Health and Diagnostics**: HTTP Logging (for basic access logs; skip the rest).
- **Web Server → Performance**: Static Content Compression (optional, reduces bandwidth for the static assets under `wwwroot`).
- **Web Server → Security**: Request Filtering only.
- **Management Tools**: IIS Management Console (and IIS Management Scripts/Tools if you'll manage it via PowerShell).

**Deliberately skip**: FTP Server, WebDAV Publishing, CGI, ISAPI Filters/Extensions (the ASP.NET Core Module handles all request routing — none of the legacy modules apply), Basic/Windows/Digest/Client Certificate Authentication (this app handles its own sign-in via Entra ID/OIDC — IIS-level authentication providers are unused and unnecessary attack surface), URL Authorization, IP and Domain Restrictions (add later only if you specifically want network-level allowlisting on top of the app's own auth).

Equivalent PowerShell, for a repeatable/scripted install:
```powershell
Install-WindowsFeature -Name Web-Server, Web-Common-Http, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, Web-Http-Logging, Web-Stat-Compression, Web-Filtering, Web-Mgmt-Console -IncludeManagementTools
```

### 4.3 Install the .NET 10 Hosting Bundle

Download and run the **ASP.NET Core Hosting Bundle** installer for .NET 10 from Microsoft's .NET download page (this installs the runtime plus the IIS integration module, `AspNetCoreModuleV2`). **Restart IIS** afterward (`iisreset`) so it picks up the newly-registered module — this step is easy to miss and the most common cause of a fresh IIS deployment failing to start.

### 4.4 Certificate and site binding

1. **IIS Manager → Server Certificates** → import the certificate from §4.1 if it isn't already in the server's certificate store.
2. **Sites → Add Website**:
   - **Site name**: e.g. `FacilityScheduler`.
   - **Physical path**: wherever you'll deploy the published app (e.g. `C:\inetpub\FacilityScheduler`).
   - **Binding**: type **https**, port **443**, select the certificate from step 1. Add a second binding for **http** on port **80** only if you want it to auto-redirect to HTTPS (the app's own `UseHttpsRedirection()` handles that redirect once a request reaches it).

### 4.5 Application pool configuration

IIS creates an app pool automatically when you create the site above (named after the site). Open **Application Pools**, select it, and set:

- **.NET CLR version**: **No Managed Code** — ASP.NET Core doesn't run under the classic CLR pipeline; this setting name is a holdover from earlier .NET Framework-hosted IIS apps.
- **Identity**: leave as **ApplicationPoolIdentity** (the default) — a least-privilege virtual account scoped to just this app pool, rather than a shared or administrative account.
- **Start Mode**: **AlwaysRunning**, and on the *site's* own **Advanced Settings**, set **Preload Enabled**: **True** — together these keep the app warm instead of cold-starting on the first request after a period of inactivity, which matters here since Blazor Server's SignalR circuit shouldn't be dropped mid-session by an idle recycle.
- **Idle Time-out (minutes)**: set to **0** (disabled) for the same reason — the default 20-minute idle shutdown would otherwise kill active staff sessions during a quiet stretch.

### 4.6 Application configuration (environment variables)

`dotnet publish` generates a `web.config` in the publish output with an `<aspNetCore>` element. Add an `<environmentVariables>` block inside it for every key from §2, using the double-underscore form from §2.1:
```xml
<aspNetCore processPath="dotnet" arguments=".\FacilityScheduler.dll" stdoutLogEnabled="false" hostingModel="InProcess">
  <environmentVariables>
    <environmentVariable name="Graph__TenantId" value="..." />
    <environmentVariable name="Graph__ClientId" value="..." />
    <environmentVariable name="Graph__ClientSecret" value="..." />
    <environmentVariable name="AzureAd__TenantId" value="..." />
    <environmentVariable name="AzureAd__ClientId" value="..." />
    <environmentVariable name="AzureAd__ClientSecret" value="..." />
    <environmentVariable name="Facility__TenantDomain" value="..." />
    <environmentVariable name="Facility__SheetMailboxLocalParts__0" value="sheet1" />
    <environmentVariable name="Facility__SheetMailboxLocalParts__1" value="sheet2" />
    <environmentVariable name="Facility__SheetMailboxLocalParts__2" value="sheet3" />
    <environmentVariable name="Facility__SheetMailboxLocalParts__3" value="sheet4" />
    <environmentVariable name="Facility__SheetMailboxLocalParts__4" value="sheet5" />
    <environmentVariable name="Facility__ClubEventsMailboxLocalPart" value="clubevents" />
    <environmentVariable name="Facility__TimeZone" value="Pacific Standard Time" />
    <environmentVariable name="Webhook__BreelySharedSecret" value="..." />
    <environmentVariable name="AppLog__LogDirectory" value="C:\FacilityScheduler-Logs" />
  </environmentVariables>
</aspNetCore>
```
Since `web.config` then contains secrets, restrict its NTFS permissions the same way as the rest of the app folder (§4.7) and keep it out of source control (it's a deploy-time artifact, not something to commit).

### 4.7 File permissions

Grant the app pool identity **Read & Execute only** on the deployed folder itself — nothing there needs to be written to at runtime. In an elevated PowerShell prompt:
```powershell
$poolName = "FacilityScheduler"
icacls "C:\inetpub\FacilityScheduler" /grant ("IIS AppPool\$poolName`:(OI)(CI)RX") /T
```
The activity/debug log's directory (§2.3) is deliberately **outside** this folder and needs the opposite: grant the app pool identity **write** access there specifically (`RX` above is not enough for it to create/append log files), e.g.:
```powershell
icacls "C:\FacilityScheduler-Logs" /grant ("IIS AppPool\$poolName`:(OI)(CI)M") /T
```

### 4.8 Firewall

Only two things need to cross the network boundary: inbound HTTPS from staff/visitors, and outbound HTTPS from the server to Microsoft's cloud endpoints.

```powershell
# Inbound: only 443 (and 80, if you're using it purely for the HTTPS redirect)
New-NetFirewallRule -DisplayName "FacilityScheduler HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
New-NetFirewallRule -DisplayName "FacilityScheduler HTTP Redirect" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
```
Outbound HTTPS (443) to `graph.microsoft.com` and `login.microsoftonline.com` is usually already permitted by Windows Firewall's default allow-outbound policy — only add explicit outbound rules if this server's firewall policy has been changed to default-deny outbound. No static IP allowlisting is needed on the Microsoft side; these are standard public Microsoft 365 endpoints resolved by DNS.

### 4.9 Minimal-footprint checklist (attack surface + resource use)

- [ ] Only the **Web Server (IIS)** role is installed on this machine — no other server roles (AD DS, DNS, DHCP, File Server, etc.) unless this box genuinely serves other purposes.
- [ ] Role services match §4.2's minimal list — FTP, WebDAV, CGI, ISAPI, and all non-Entra IIS authentication providers are **not** installed.
- [ ] **Directory browsing** is disabled (IIS default is already off — confirm it wasn't turned on).
- [ ] The **Server** response header is suppressed: in `web.config` or via `applicationHost.config`, set `<security><requestFiltering removeServerHeader="true" /></security>` (IIS 10+) so responses don't advertise the exact IIS version to anyone probing the site.
- [ ] **Detailed error pages** are not shown to remote requests in production — the app's own `UseExceptionHandler("/Error")` (active outside `Development`) already covers this; don't override it with IIS's own detailed remote errors setting.
- [ ] App pool **Identity** is the default `ApplicationPoolIdentity`, not a shared/administrative account.
- [ ] NTFS permissions on the app folder are **Read & Execute** only for the app pool identity (§4.7) — no broader access than that.
- [ ] Windows Firewall allows only 443 (and optionally 80) inbound; nothing else is open on this host.
- [ ] Consider **Server Core** (no Desktop Experience) if this server is managed remotely — it has a materially smaller patch/attack surface than a full GUI install, at the cost of needing IIS Manager or PowerShell from another machine for day-to-day administration.
- [ ] Windows Update stays current — this is a small, low-traffic app; a stale, unpatched server is a far larger risk than anything in the app itself.

---

## 5. Generic Requirements — Any Other Host

If deploying somewhere other than Azure App Service or IIS (a Linux VM, a container, etc.), the app has no cloud-specific dependency — it's a standard ASP.NET Core Blazor Server app. Any host needs:

- **.NET 10 ASP.NET Core runtime** (or the SDK, if building on the host itself).
- **Outbound HTTPS access** to `graph.microsoft.com` and `login.microsoftonline.com` — nothing else external is called.
- **HTTPS termination** — either the app's own Kestrel server with a bound certificate, or a reverse proxy (nginx, a cloud load balancer) terminating TLS in front of it. Blazor Server's SignalR circuit needs WebSocket support to pass through cleanly if a reverse proxy is in the path.
- **A secret-injection mechanism** — environment variables (using the `__` convention from §2.1) or a mounted configuration file are both sufficient; nothing here requires a specific secret manager.
- **A process supervisor** to keep the app running and restart it on failure — a systemd unit, a container orchestrator's own restart policy, or equivalent.
- The same §2 configuration reference table applies regardless of host — only *where* each value is set changes.

---

## 6. Post-Deploy Verification Checklist

- [ ] Staff sign-in (Entra SSO) succeeds and the app's root URL (`/`, which routes directly to the staff Calendar) loads real data for every configured mailbox. A blank calendar or a Graph error here usually means either the Application Access Policy/RBAC scoping (architecture doc §6.3) or the `Facility` configuration is wrong.
- [ ] A non-assigned account is correctly blocked from signing in, if §1.2's Enterprise Application assignment restriction was configured.
- [ ] `/public/calendar` loads without signing in, in a private/incognito browser window.
- [ ] `/api/public/availability` returns JSON without signing in.
- [ ] If `Webhook:BreelySharedSecret` is configured, a test call to `/api/webhooks/breely` without the `X-Webhook-Secret` header returns `401`, and a real test booking through Breely (§2.2) shows up on the correct sheet's calendar. If the test booking spans multiple sheets, confirm every sheet gets claimed (not just one) and that they show up grouped together when clicked (architecture doc §4.8).
- [ ] `AppLog:LogDirectory` is set to a path outside the deployed app folder (§2.3) — not left at the `App_Data/logs` fallback. Sign in, open `/settings`, take any booking action, and confirm a new line appears in the log viewer after clicking Refresh.
- [ ] Mailbox audit logging is confirmed enabled on the resource mailboxes (per the provisioning checklist, architecture doc §7) — this is a tenant-side setting, not something the app itself can verify.
- [ ] `Facility:TimeZone` is genuinely the facility's own zone, not left at a placeholder — every "today" in the app (the staff/public calendar's Today button, new-booking defaults, the public availability window) is computed from it (`FacilityConfiguration.Today`, architecture doc §4.6). A wrong zone here silently shifts what "today" means, most noticeably in the evening.
- [ ] `curl -I https://<your-app>/calendar` (or any staff page) shows `X-Frame-Options: DENY` and a `Content-Security-Policy` header; the same check against `/public/calendar` should show neither — that page is the one deliberate exception (architecture doc §6.4).
- [ ] If `Facility:TenantDomain` (or any other load-bearing `Facility` value) is deliberately left unset as a smoke test, the app should fail to start with a clear `InvalidOperationException` — confirms the fail-fast validation is actually wired up in this environment, not silently bypassed by a stale cached config.
- [ ] `/public/practice-ice` loads without signing in; clicking an open slot and submitting a real request (§2.4) creates a `Practice Ice` hold visible on `/calendar` and sends the approver notification email — if it doesn't, see §2.4's diagnosis steps before assuming the code is wrong. Approve or decline it from `/practice-ice/approvals` and confirm the volunteer's confirmation/decline email arrives.
- [ ] **Before inviting any member as a guest for practice ice hosting:** complete §2.5's setup (staff security group, delegated Ownership, `GroupMember.Read.All`, `StaffAccess:StaffGroupId`), then **sign in as a real non-staff test account** and confirm `/practice-ice/request` works while `/calendar`, `/settings`, `/club-events`, and `/practice-ice/approvals` all correctly deny access. This specific check has not been performed against a real tenant as of this writing (architecture doc §6.5/§8) — don't skip it on the assumption the code is obviously right.
- [ ] (IIS only) The site is bound to 443 with a valid, non-expired certificate, and Windows Firewall shows only the expected inbound ports open.
