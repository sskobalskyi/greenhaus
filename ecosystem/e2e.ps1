# The whole story, end to end, across Orderflow, Billing, Fulfilment, Pulse and the Greenhaus API, for one new person:
#
#   buy a kit -> the purchase grants a tier -> a device is allocated and registered in Pulse -> the order ships and the device
#   becomes claimable -> the person claims it into a pot -> readings show in the pot -> the order shows as in use.
#
# Needs the stack from ./ecosystem/up.ps1. Exits 0 when every step holds.
param([string]$Sku = 'GH-POT-KIT-01')

$ErrorActionPreference = 'Stop'
$keycloak = 'http://localhost:8181'
$realm = "$keycloak/realms/greenhaus"
$gateway = 'http://localhost:8080/api'
$greenhaus = 'http://localhost:8091/api/greenhaus/v1'
$fulfilment = 'http://localhost:8093/api/fulfilment/v1'
$pulse = 'http://localhost:5080'

$script:failed = @()
function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  ok   $what" -ForegroundColor Green }
    else { Write-Host "  FAIL $what $detail" -ForegroundColor Red; $script:failed += $what }
}

function Wait-Until([scriptblock]$probe, [string]$what, [int]$seconds = 60) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        try { $value = & $probe; if ($value) { return $value } } catch { }
        Start-Sleep -Milliseconds 700
    }
    Write-Host "  FAIL timed out waiting for: $what" -ForegroundColor Red
    $script:failed += "timeout: $what"
    return $null
}

function Get-Token([string]$client, [string]$user, [string]$password, [string]$tokenRealm = 'greenhaus') {
    (Invoke-RestMethod -Method Post -Uri "$keycloak/realms/$tokenRealm/protocol/openid-connect/token" -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'password'; client_id = $client; username = $user; password = $password }).access_token
}

function Get-Service-Token {
    (Invoke-RestMethod -Method Post -Uri "$realm/protocol/openid-connect/token" -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'client_credentials'; client_id = 'pulse-service'; client_secret = 'pulse-dev-secret' }).access_token
}

function Call([string]$method, [string]$url, [string]$token, $body = $null) {
    $request = @{ Method = $method; Uri = $url; Headers = @{ Authorization = "Bearer $token" }; UseBasicParsing = $true }
    if ($null -ne $body) { $request.ContentType = 'application/json'; $request.Body = ($body | ConvertTo-Json -Depth 8 -Compress) }
    $response = Invoke-WebRequest @request
    if ($response.Content) { $response.Content | ConvertFrom-Json } else { $null }
}

function Read-Subject([string]$jwt) {
    $p = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/'); while ($p.Length % 4) { $p += '=' }
    ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json).sub
}

# ---------------------------------------------------------------------------------------------------------------------
Step 'A new person registers in the shared realm'
$master = (Invoke-RestMethod -Method Post -Uri "$keycloak/realms/master/protocol/openid-connect/token" -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
$name = 'buyer' + (Get-Date -Format 'MMddHHmmss')
$created = Invoke-WebRequest -Method Post -Uri "$keycloak/admin/realms/greenhaus/users" -Headers @{ Authorization = "Bearer $master" } -ContentType 'application/json' -UseBasicParsing `
    -Body (@{ username = $name; email = "$name@example.com"; firstName = 'Bea'; lastName = 'Buyer'; enabled = $true; emailVerified = $true
              credentials = @(@{ type = 'password'; value = 'Passw0rd!x'; temporary = $false }) } | ConvertTo-Json -Depth 5)
Check 'the person exists in the realm' ($created.StatusCode -eq 201)
$shopToken = Get-Token 'orderflow-web' $name 'Passw0rd!x'
$appToken = Get-Token 'greenhaus-e2e' $name 'Passw0rd!x'
$sub = Read-Subject $shopToken
Check 'one subject in the shop and in the Greenhaus API' ((Read-Subject $appToken) -eq $sub) "($sub)"

# ---------------------------------------------------------------------------------------------------------------------
Step "The person buys a kit ($Sku) in Orderflow"
$order = Call 'POST' "$gateway/orders" $shopToken @{ lines = @(@{ sku = $Sku; quantity = 1 }) }
$orderId = $order.id
Check 'the order was placed for this person' ($order.customerId -eq $sub) "(customerId $($order.customerId))"
$confirmed = Wait-Until { (Call 'GET' "$gateway/orders/$orderId" $shopToken).status -eq 'Confirmed' } 'the order to be confirmed'
Check 'the order is confirmed (stock reserved, payment taken)' ([bool]$confirmed)

# ---------------------------------------------------------------------------------------------------------------------
Step 'Billing grants a tier for the purchase, and the Greenhaus API applies it'
$tier = Wait-Until { $e = Call 'GET' "$greenhaus/me/entitlements" $appToken; if ($e.tier -eq 'gardener') { $e } } 'the purchase grant to reach the Greenhaus API'
Check 'the effective tier is Gardener' ($tier.tier -eq 'gardener')
$sourceTypes = @($tier.sources | ForEach-Object { $_.type })
Check 'it comes from a purchase grant' ($sourceTypes -contains 'purchase_grant') ('(sources: ' + ($sourceTypes -join ',') + ')')

# ---------------------------------------------------------------------------------------------------------------------
Step 'Fulfilment allocates a device and Pulse registers it'
$adminToken = Get-Token 'greenhaus-e2e' 'admin' 'admin'
$bundle = Wait-Until { $a = Call 'GET' "$fulfilment/orders/$orderId/allocations" $adminToken; if (@($a).Count -ge 1) { @($a)[0] } } 'the allocation'
Check 'a device was allocated for the order' ($null -ne $bundle)
$deviceId = $bundle.deviceId
$claimToken = $bundle.claimToken
Check 'the packing bundle carries the claim token and the claim link' ([bool]$claimToken -and [bool]$bundle.claimUrl)
$service = Get-Service-Token
$registered = Wait-Until { Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId" $service } 'Pulse to register the device'
Check 'Pulse registered the device as allocated' ($registered.state -eq 'allocated') "(state $($registered.state))"
Check 'its channels and actuators came from the kit' ($registered.capabilities.channels.Count -ge 3 -and $registered.capabilities.actuators.Count -ge 1)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The shop ships the order and the device becomes claimable'
$shopAdmin = Get-Token 'orderflow-web' 'admin' 'admin'
$null = Call 'POST' "$gateway/admin/orders/$orderId/ship" $shopAdmin @{}
$shipped = Wait-Until { $d = Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId" $service; if ($d.state -eq 'shipped') { $d } } 'Pulse to mark the device shipped'
Check 'Pulse marked the device shipped (by fulfilment)' ([bool]$shipped)
$after = Wait-Until { $a = @(Call 'GET' "$fulfilment/orders/$orderId/allocations" $adminToken)[0]; if ($a.state -eq 'shipped') { $a } } 'fulfilment to record the shipment'
Check 'fulfilment erased the secrets once shipped' ($after -and -not $after.claimToken -and -not $after.credential)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The person claims the device into a pot'
$pot = Call 'POST' "$greenhaus/pots" $appToken @{ name = 'Kitchen herbs'; kind = 'pot' }
$preview = Call 'GET' "$greenhaus/claims/preview?deviceId=$deviceId&token=$claimToken" $appToken
Check 'the claim preview says the device is claimable' ($preview.claimable -eq $true)
$claim = Call 'POST' "$greenhaus/claims" $appToken @{ deviceId = $deviceId; token = $claimToken; potId = $pot.id }
Check 'the claim succeeded' ($claim.deviceId -eq $deviceId)
$claimed = Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId" $service
Check 'Pulse says the device belongs to this person' (($claimed.state -eq 'claimed' -or $claimed.state -eq 'active') -and $claimed.ownerId -eq $sub) "(state $($claimed.state), owner $($claimed.ownerId))"
$actuators = Call 'GET' "$greenhaus/pots/$($pot.id)/actuators" $appToken
Check 'the pot has the kit''s pump with limits inside the hardware limits' ((@($actuators) | Where-Object { $_.id -eq 'pump' }).limits.maxRunSeconds -le 60)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The device reports and the pot shows it'
$soil = @($claimed.capabilities.channels | Where-Object { $_.metric -eq 'soil_moisture' })[0]
$reading = @{ deviceId = $deviceId; metric = 'soil_moisture'; channel = $soil.id; value = 31.5; timestamp = (Get-Date).ToUniversalTime().ToString('o') }
$null = Invoke-WebRequest -Method Post -Uri "$pulse/api/telemetry" -Headers @{ Authorization = "Bearer $service" } -ContentType 'application/json' -Body ('[' + ($reading | ConvertTo-Json -Compress) + ']') -UseBasicParsing
$status = Wait-Until { $s = Call 'GET' "$greenhaus/pots/$($pot.id)/status" $appToken; if (@($s.zones.metrics | Where-Object { $_.metric -eq 'soil_moisture' -and $_.value -eq 31.5 }).Count) { $s } } 'the reading in the pot status'
Check 'the pot status shows the reading from the device' ([bool]$status)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The shop shows the order as in use'
$activated = Wait-Until { $t = Call 'GET' "$gateway/orders/$orderId/timeline" $shopToken; if (@($t | Where-Object { $_.step -eq 'Activated' }).Count) { $t } } 'the Activated step'
Check 'the order timeline has the Activated step with the device id' ($activated -and (@($activated | Where-Object { $_.step -eq 'Activated' })[0].detail -eq $deviceId))
$final = Call 'GET' "$gateway/orders/$orderId" $shopToken
Check 'the order is shipped and in use' ($final.status -eq 'Shipped' -and [bool]$final.activatedAt)

# ---------------------------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:failed.Count -eq 0) { Write-Host 'The whole story holds, from the purchase to the reading.' -ForegroundColor Green; exit 0 }
Write-Host "$($script:failed.Count) check(s) failed:" -ForegroundColor Red
$script:failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
