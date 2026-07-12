<#
Provisions master categories on the 6 facility resource mailboxes (5 sheets + Club Events).
Idempotent: safe to re-run - skips any category that already exists by displayName.
Requires: $env:CURLING_APP_CLIENT_SECRET set in this session before running.
#>

param(
    [string]$TenantId = "<tenant-id>",
    [string]$ClientId = "<client-id>"
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

$sheetCategories = @(
    @{ displayName = "Rental"; color = "preset4" },   # Green
    @{ displayName = "League"; color = "preset7" },   # Blue
    @{ displayName = "Event";  color = "preset9" },   # Cranberry
    @{ displayName = "Other";  color = "preset8" }    # Purple
)
$clubEventCategories = @(
    @{ displayName = "Bonspiel";   color = "preset1"  },  # Orange
    @{ displayName = "Tournament"; color = "preset3"  },  # Yellow
    @{ displayName = "Closure";    color = "preset12" }   # Gray
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

foreach ($mb in (1..5 | ForEach-Object { "sheet$_@anthefamily.onmicrosoft.com" })) {
    Set-MailboxCategories -Mailbox $mb -Categories $sheetCategories
}
Set-MailboxCategories -Mailbox "clubevents@anthefamily.onmicrosoft.com" -Categories $clubEventCategories

$results | Format-Table -AutoSize

$failures = $results | Where-Object { $_.Status -eq "FAILED" }
if ($failures) {
    Write-Warning "$($failures.Count) failure(s) - see table above. Safe to re-run this script; already-created categories will be skipped automatically."
} else {
    Write-Host "All categories created or already present. Done." -ForegroundColor Green
}
