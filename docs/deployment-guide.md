# Facility Scheduler — Deployment / Installation Guide

**Audience:** whoever is standing up a new instance of this app (a new tenant, a new environment, or a fresh facility entirely).
**Companion to:** `curling-facility-scheduling-architecture.md` (design), `provision-categories.ps1` (tenant provisioning script).

---

## 1. Prerequisites

Before deploying the app itself, the tenant side needs to exist:

1. **An Entra ID app registration** in the target M365 tenant, with:
   - **Delegated** `Calendars.Read` (or similar) scope, for staff sign-in (identity/audit only — see the architecture doc §6.2, Graph itself always runs on the app-only credential below).
   - **Application** Graph calendar scopes (e.g. `Calendars.ReadWrite`), admin-consented, for the service identity that does all actual Graph work.
   - A client secret (or certificate) generated for the application credential.
2. **Resource mailboxes provisioned** — one per sheet, plus one Club Events mailbox — scoped to a dedicated security group, with the app registration's application permission constrained to that group via an Application Access Policy or RBAC for Applications (architecture doc §6.3). Run `docs/provision-categories.ps1` against the tenant once the mailboxes exist, to set up the master category lists:
   ```powershell
   $env:CURLING_APP_CLIENT_SECRET = '<client secret>'
   .\provision-categories.ps1 -TenantId '<tenant id>' -ClientId '<app registration client id>' -TenantDomain '<tenant>.onmicrosoft.com' -SheetCount 5
   ```
3. **.NET 10 ASP.NET Core runtime** available wherever the app will run (the exact mechanism depends on the host — see §4).

---

## 2. Configuration Reference

Every value the app needs, and where it belongs. None of this is baked into source — see the architecture doc §4.6.

| Key | What it is | Secret? | Local dev | Production |
|---|---|---|---|---|
| `Graph:TenantId` | Entra tenant (directory) ID for the app registration | No | user-secrets or `appsettings.Development.json` | App Service Application Settings (or host equivalent) |
| `Graph:ClientId` | App registration client ID (application credential) | No | same | same |
| `Graph:ClientSecret` | App registration client secret | **Yes** | user-secrets only, never committed | App Service Application Settings, marked as a "slot setting"/secret if the host supports it |
| `AzureAd:Instance` | Always `https://login.microsoftonline.com/` | No | `appsettings.json` (already set) | same |
| `AzureAd:TenantId` | Entra tenant ID for staff SSO (delegated sign-in) | No | user-secrets | App Service Application Settings |
| `AzureAd:ClientId` | App registration client ID for staff SSO (delegated scope) | No | user-secrets | App Service Application Settings |
| `AzureAd:ClientSecret` | Client secret for the delegated sign-in flow | **Yes** | user-secrets only | App Service Application Settings |
| `Facility:TenantDomain` | The tenant's mailbox domain, e.g. `contoso.onmicrosoft.com` | No | `appsettings.Development.json` | App Service Application Settings |
| `Facility:SheetMailboxLocalParts` | Array of sheet mailbox local-parts (e.g. `["sheet1","sheet2",...]`) — not a count, an explicit list | No | same | same (as a JSON array; see §2.1 below for how App Service represents arrays) |
| `Facility:ClubEventsMailboxLocalPart` | Local-part of the Club Events mailbox (default `clubevents`) | No | same | same |
| `Facility:TimeZone` | Windows time zone ID the facility operates in (e.g. `Pacific Standard Time`) | No | same | same |
| `Facility:Name` | Facility display name | No | optional, currently inert (no UI wiring yet) | optional |
| `Facility:LogoPath` | Relative path under `wwwroot` to a logo image (e.g. `/branding/logo.png`) | No | optional, currently inert | optional — the actual image file must be placed under `wwwroot` in the deployed app if set |

**`Facility:TenantDomain`, `Facility:SheetMailboxLocalParts`, and `Facility:TimeZone` are load-bearing** — the app fails fast at startup with a clear error if any is missing, rather than starting in a silently-broken state.

### 2.1 Representing the `SheetMailboxLocalParts` array outside a JSON file

.NET configuration flattens JSON arrays into indexed keys. If your host only accepts flat key/value pairs (rather than a JSON file or a structured secrets store), set:
```
Facility__SheetMailboxLocalParts__0 = sheet1
Facility__SheetMailboxLocalParts__1 = sheet2
Facility__SheetMailboxLocalParts__2 = sheet3
Facility__SheetMailboxLocalParts__3 = sheet4
Facility__SheetMailboxLocalParts__4 = sheet5
```
(double-underscore `__` is .NET configuration's section-separator convention for environment variables). Azure App Service's Application Settings UI accepts this same `__` convention directly.

---

## 3. Primary Path — Azure App Service

1. **Resource group + App Service Plan** — a Linux or Windows plan both work; a small tier (B1) is comfortably sufficient at this app's actual concurrency (1–2 staff users, plus anonymous public traffic against the rate-limited public endpoints).
2. **Create the Web App**, runtime stack = .NET 10 (or the closest available .NET version at deploy time).
3. **Publish the app**:
   ```
   dotnet publish -c Release -o ./publish
   ```
   then deploy the `./publish` contents via `az webapp deploy`, a GitHub Actions workflow, or ZIP deploy through the portal — whichever fits your existing CI/CD.
4. **Set Application Settings** (Configuration → Application settings in the portal, or `az webapp config appsettings set`) for every key in §2 above. Application Settings are injected as environment variables at container/process start, which ASP.NET Core's configuration system picks up automatically (no code change needed).
5. **Custom domain + HTTPS**: bind the club's domain and enable an App Service Managed Certificate (or bring your own), then enforce HTTPS-only in the App Service TLS/SSL settings.
6. **Restart** the Web App after setting configuration (App Service usually does this automatically on a settings save, but confirm) so `FacilityConfiguration`'s startup validation runs against the real values.

---

## 4. Generic Requirements — Any Other Host

If not deploying to Azure App Service, the app has no Azure-specific dependency — it's a standard ASP.NET Core Blazor Server app. Any host needs:

- **.NET 10 ASP.NET Core runtime** (or the SDK, if building on the host itself).
- **Outbound HTTPS access** to `graph.microsoft.com` and `login.microsoftonline.com` — nothing else external is called.
- **HTTPS termination** — either the app's own Kestrel server with a bound certificate, or a reverse proxy (nginx, IIS as a reverse proxy, a cloud load balancer) terminating TLS in front of it. Blazor Server's SignalR circuit needs WebSocket support to pass through cleanly if a reverse proxy is in the path.
- **A secret-injection mechanism** — environment variables (using the `__` convention from §2.1) or a mounted configuration file are both sufficient; nothing here requires a specific secret manager.
- **A process supervisor** to keep the app running and restart it on failure — systemd unit, IIS as a process host (via the ASP.NET Core Hosting Bundle), a container orchestrator's own restart policy, or equivalent. Which one depends entirely on the host; the app itself has no opinion.
- The same §2 configuration reference table applies regardless of host — only *where* each value is set changes.

---

## 5. Post-Deploy Verification Checklist

- [ ] Visiting the app's root URL (`/`) shows the connectivity check page returning **OK** for every configured mailbox (sheet mailboxes + Club Events). A `FAILED` row here usually means either the Application Access Policy/RBAC scoping (architecture doc §6.3) or the `Facility` configuration is wrong.
- [ ] Staff sign-in (Entra SSO) succeeds and the `/calendar` page loads real data.
- [ ] `/public/calendar` loads without signing in, in a private/incognito browser window.
- [ ] `/api/public/availability` returns JSON without signing in.
- [ ] Mailbox audit logging is confirmed enabled on the resource mailboxes (per the provisioning checklist, architecture doc §7) — this is a tenant-side setting, not something the app itself can verify.
- [ ] If `Facility:TenantDomain` (or any other load-bearing `Facility` value) is deliberately left unset as a smoke test, the app should fail to start with a clear `InvalidOperationException` — confirms the fail-fast validation is actually wired up in this environment, not silently bypassed by a stale cached config.
