<#
Provisions master categories on the facility's resource mailboxes (the configured sheet count,
plus Club Events). Idempotent: safe to re-run - skips any category that already exists by displayName.
Requires: $env:CURLING_APP_CLIENT_SECRET set in this session before running.
#>

param(
    [string]$TenantId = "<tenant-id>",
    [string]$ClientId = "<client-id>",
    [string]$TenantDomain = "<tenant-domain>",
    [int]$SheetCount = 5
)

if (-not $env:CURLING_APP_CLIENT_SECRET) {
    throw "CURLING_APP_CLIENT_SECRET is not set in this session. Set it before running this script, e.g.: `$env:CURLING_APP_CLIENT_SECRET = '<value>'"
}

$tokenResponse = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{
    client_id     = $ClientId
    client_secret = $env:CURLING_APP_CLIENT_SECRET
    scope         = "https://graph.microsoft.com/.default"
    grant_type    = "client_credentials"
}
$headers = @{ Authorization = "Bearer $($tokenResponse.access_token)" }

# Mirrors CalendarStyles.CategoryColor / ClubEventCategoryColor as closely as Exchange's fixed
# preset palette allows, so the Outlook read-only fallback view (architecture doc D2) doesn't show a
# different color scheme than the web UI. Preset names are Outlook's own: preset0 Red, preset1
# Orange, preset3 Yellow, preset4 Green, preset5 Teal, preset7 Blue, preset8 Purple, preset9
# Cranberry, preset10 Steel, preset12 Gray, preset13 DarkGray.
#
# Every BookingCategory / ClubEventCategory value is listed. Bonspiel and Maintenance were missing
# from the sheet list, and the club list still said "Tournament" - renamed to "Activities" during the
# build (architecture doc §4.4) - so both were provisioning an incomplete/stale palette.
$sheetCategories = @(
    @{ displayName = "GroupEvent";  color = "preset7"  },  # Blue      - app #2d5f8a
    @{ displayName = "League";      color = "preset4"  },  # Green     - app #4a8a5f
    @{ displayName = "Event";       color = "preset9"  },  # Cranberry - app #a05fa8 (reserved for club events, kept in the palette so a stray value still renders)
    @{ displayName = "Bonspiel";    color = "preset1"  },  # Orange    - app #c2622f
    @{ displayName = "Maintenance"; color = "preset12" },  # Gray      - app #8a97a3
    @{ displayName = "PracticeIce"; color = "preset8"  },  # Purple    - app #7a6acb (closest preset to the app's lavender)
    @{ displayName = "Other";       color = "preset0"  }   # Red       - app #c0392b
)
$clubEventCategories = @(
    @{ displayName = "Bonspiel";   color = "preset1"  },  # Orange    - app #c2622f
    @{ displayName = "Activities"; color = "preset3"  },  # Yellow    - app #2e7d8c
    @{ displayName = "Meetings";   color = "preset9"  },  # Cranberry - app #a63a5d
    @{ displayName = "Closure";    color = "preset12" },  # Gray      - app #6b7680
    @{ displayName = "Other";      color = "preset10" }   # Steel     - app #9c9690
)

$results = @()

function Set-MailboxCategories {
    param(
        [string]$Mailbox,
        [array]$Categories
    )

    $uri = "https://graph.microsoft.com/v1.0/users/$Mailbox/outlook/masterCategories"

    try {
        $existing = (Invoke-RestMethod -Method Get -Headers $headers -Uri $uri).value
        $existingNames = $existing | ForEach-Object { $_.displayName }
    } catch {
        $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Category = "(list existing)"; Status = "FAILED"; Detail = $_.Exception.Message }
        return
    }

    foreach ($cat in $Categories) {
        if ($existingNames -contains $cat.displayName) {
            $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Category = $cat.displayName; Status = "SKIPPED (already exists)"; Detail = "" }
            continue
        }
        try {
            Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" -Uri $uri -Body ($cat | ConvertTo-Json) | Out-Null
            $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Category = $cat.displayName; Status = "CREATED"; Detail = "" }
        } catch {
            $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Category = $cat.displayName; Status = "FAILED"; Detail = $_.Exception.Message }
        }
    }
}

foreach ($mb in (1..$SheetCount | ForEach-Object { "sheet$_@$TenantDomain" })) {
    Set-MailboxCategories -Mailbox $mb -Categories $sheetCategories
}
Set-MailboxCategories -Mailbox "clubevents@$TenantDomain" -Categories $clubEventCategories

$results | Format-Table -AutoSize

$failures = $results | Where-Object { $_.Status -eq "FAILED" }
if ($failures) {
    Write-Warning "$($failures.Count) failure(s) - see table above. Safe to re-run this script; already-created categories will be skipped automatically."
} else {
    Write-Host "All categories created or already present. Done." -ForegroundColor Green
}
