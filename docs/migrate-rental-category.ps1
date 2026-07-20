<#
One-time data migration: renames the literal "Rental" category tag on existing sheet-mailbox
events to "GroupEvent", matching the app's BookingCategory.Rental -> BookingCategory.GroupEvent
rename. Renaming the master category list (provision-categories.ps1) only changes what Outlook
shows for NEW category assignments - it does not touch the categories array already stored on
existing events, which is what the app actually reads back to determine a booking's category.
This script fixes that at the source instead of leaving a permanent parse-time compatibility shim
in the app.

Only sheet mailboxes are affected - Club Events uses a separate category taxonomy (Bonspiel/
Activities/Closure/Other) that was never touched by the Rental rename.

Run with -WhatIf first to preview every change with no writes, e.g.:
    .\migrate-rental-category.ps1 -TenantId ... -ClientId ... -TenantDomain ... -WhatIf
Then re-run without -WhatIf to actually apply it.

Requires: $env:CURLING_APP_CLIENT_SECRET set in this session before running.
#>

[CmdletBinding(SupportsShouldProcess)]
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

$results = @()

function Migrate-MailboxEvents {
    param([string]$Mailbox)

    # Plain /events, not /calendarView - returns series masters and single events once each,
    # never per-occurrence expansions, so a recurring series only needs one PATCH (its master).
    $uri = "https://graph.microsoft.com/v1.0/users/$Mailbox/events?`$select=id,subject,categories&`$top=999"

    while ($uri) {
        try {
            $page = Invoke-RestMethod -Method Get -Headers $headers -Uri $uri
        } catch {
            $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Event = "(list events)"; Status = "FAILED"; Detail = $_.Exception.Message }
            return
        }

        foreach ($evt in $page.value) {
            if (-not $evt.categories -or $evt.categories -notcontains "Rental") {
                continue
            }

            # @() forces a real array even when there's exactly one category - without it, a
            # single-element pipeline result collapses to a bare scalar on assignment, and
            # ConvertTo-Json then serializes categories as a plain string ("GroupEvent") instead
            # of an array (["GroupEvent"]), which Graph's schema rejects.
            $newCategories = @($evt.categories | ForEach-Object { if ($_ -eq "Rental") { "GroupEvent" } else { $_ } })
            $change = "categories: [$($evt.categories -join ', ')] -> [$($newCategories -join ', ')]"

            if (-not $PSCmdlet.ShouldProcess("$Mailbox / $($evt.subject)", "Update $change")) {
                $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Event = $evt.subject; Status = "SKIPPED (-WhatIf)"; Detail = $change }
                continue
            }

            try {
                $body = @{ categories = $newCategories } | ConvertTo-Json
                # Event ids can contain characters (/, +) that aren't safe unescaped in a URL path
                # segment - encode it rather than interpolating the raw id.
                $encodedId = [uri]::EscapeDataString($evt.id)
                Invoke-RestMethod -Method Patch -Headers $headers -ContentType "application/json" -Uri "https://graph.microsoft.com/v1.0/users/$Mailbox/events/$encodedId" -Body $body | Out-Null
                $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Event = $evt.subject; Status = "MIGRATED"; Detail = $change }
            } catch {
                # $_.ErrorDetails.Message carries Graph's actual JSON error body (the real reason,
                # e.g. an invalid property value) - the raw exception message is just ".NET saying
                # the status code wasn't 2xx", with no detail on why.
                $detail = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
                $script:results += [PSCustomObject]@{ Mailbox = $Mailbox; Event = $evt.subject; Status = "FAILED"; Detail = $detail }
            }
        }

        $uri = $page.'@odata.nextLink'
    }
}

foreach ($mb in (1..$SheetCount | ForEach-Object { "sheet$_@$TenantDomain" })) {
    Migrate-MailboxEvents -Mailbox $mb
}

if ($results.Count -eq 0) {
    Write-Host "No events tagged 'Rental' were found - nothing to migrate." -ForegroundColor Green
} else {
    $results | Format-Table -AutoSize
}

$failures = $results | Where-Object { $_.Status -eq "FAILED" }
if ($failures) {
    Write-Warning "$($failures.Count) failure(s) - see table above. Safe to re-run; already-migrated events will simply no longer match on a later pass."
}
