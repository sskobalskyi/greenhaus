# The whole story, end to end, across Orderflow, Billing, Fulfilment, Pulse and the Greenhaus API, for one new person:
#
#   buy a kit -> the purchase grants a tier -> a device is allocated and registered in Pulse -> the order ships and the device
#   becomes claimable -> the person claims it into a pot -> readings show in the pot -> the order shows as in use.
#
# Needs the stack from ./ecosystem/up.ps1. Exits 0 when every step holds.
param(
    [string]$Sku = 'GH-POT-KIT-01',
    # Also return the goods: the purchase grant goes to at risk, with a deadline.
    [switch]$Return,
    # With -Return, also wait for the grace period to end and the tier to fall back (needs up.ps1 -ShortGrace).
    [switch]$WaitForRevocation
)

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
Step 'The pot has a target and the device breaks it: Pulse raises an alert and the person is notified'
$requirements = Call 'GET' "$greenhaus/pots/$($pot.id)/requirements" $appToken
foreach ($m in $requirements.metrics) { if ($m.metric -eq 'soil_moisture') { $m.enabled = $true; $m | Add-Member -Force -NotePropertyName min -NotePropertyValue 40; $m | Add-Member -Force -NotePropertyName max -NotePropertyValue 70 } }
$null = Call 'PUT' "$greenhaus/pots/$($pot.id)/requirements" $appToken $requirements
$rules = Wait-Until { $r = @(Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId/rules" $service); if (@($r | Where-Object { $_.kind -eq 'threshold' -and $_.channelId -eq $soil.id }).Count) { $r } } 'the threshold rule to reach Pulse'
Check 'the target became an alert rule in Pulse' ([bool]$rules)
$dry = @{ deviceId = $deviceId; metric = 'soil_moisture'; channel = $soil.id; value = 22.0; timestamp = (Get-Date).ToUniversalTime().ToString('o') }
$null = Invoke-WebRequest -Method Post -Uri "$pulse/api/telemetry" -Headers @{ Authorization = "Bearer $service" } -ContentType 'application/json' -Body ('[' + ($dry | ConvertTo-Json -Compress) + ']') -UseBasicParsing
$alert = Wait-Until { $n = Call 'GET' "$greenhaus/notifications?unread=true" $appToken; $hit = @($n | Where-Object { $_.kind -eq 'alert_raised' }); if ($hit.Count) { $hit[0] } } 'the alert notification'
Check 'the person has an unread alert notification for this pot' ([bool]$alert -and $alert.potId -eq $pot.id) "(notification: $($alert | ConvertTo-Json -Compress))"
$status2 = Wait-Until { $s = Call 'GET' "$greenhaus/pots/$($pot.id)/status" $appToken; if ($s.state -eq 'attention') { $s } } 'the pot to show attention'
Check 'the pot status says attention and names the worst zone' ([bool]$status2 -and [bool]$status2.worstZoneId)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The pot waters itself: the pump is on auto and the soil is too dry'
$null = Call 'PUT' "$greenhaus/pots/$($pot.id)/channels/$($soil.id)/calibration" $appToken @{ dryRaw = 800; wetRaw = 300 }
$pump = Call 'PUT' "$greenhaus/pots/$($pot.id)/actuators/pump/mode" $appToken @{ mode = 'auto' }
Check 'the pump is on auto (the Gardener tier allows it)' ($pump.mode -eq 'auto')
$auto = Wait-Until { $l = @(Call 'GET' "$greenhaus/pots/$($pot.id)/audit-log" $appToken | Where-Object { $_.actor -eq 'automation' -and $_.action -eq 'run' -and $_.actuatorId -eq 'pump' }); if ($l.Count) { $l[0] } } 'the control loop to water' 90
Check 'the control loop decided to water and wrote why in the audit log' ([bool]$auto) "(entry: $($auto | ConvertTo-Json -Compress))"
$sent = Wait-Until { $c = @(Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId/commands" $service | Where-Object { $_.type -eq 'run_actuator' -and $_.actuatorId -eq 'pump' }); if ($c.Count) { $c } } 'the automatic command in Pulse'
Check 'Pulse holds the command the loop sent' ([bool]$sent)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The person waters by hand: the command reaches the device through Pulse'
$receipt = Call 'POST' "$greenhaus/pots/$($pot.id)/actuators/pump/run" $appToken @{ durationSeconds = 10 }
Check 'Greenhaus accepted the run and gave a command id' ([bool]$receipt.commandId)
$queued = Wait-Until { $c = @(Call 'GET' "$pulse/api/pulse/v2/devices/$deviceId/commands" $service | Where-Object { $_.commandId -eq $receipt.commandId }); if ($c.Count) { $c[0] } } 'the command in Pulse'
Check 'Pulse holds the command for the device (queued or beyond)' ([bool]$queued -and $queued.type -eq 'run_actuator' -and $queued.actuatorId -eq 'pump') "(command: $($queued | ConvertTo-Json -Compress))"
$log = Call 'GET' "$greenhaus/pots/$($pot.id)/audit-log" $appToken
Check 'the audit log says who asked, the person, and which command' (@($log | Where-Object { $_.action -eq 'run' -and $_.actor -eq 'user' }).Count -ge 1)

# ---------------------------------------------------------------------------------------------------------------------
Step 'The person asks the assistant about the pot'
$null = Call 'POST' "$greenhaus/pots/$($pot.id)/plants" $appToken @{ speciesId = 'basil'; nickname = 'Basil on the sill' }
$before = (Call 'GET' "$greenhaus/me/entitlements" $appToken).usage.aiQuestionsThisMonth
$request = @{ Method = 'Post'; Uri = "$greenhaus/pots/$($pot.id)/assistant/ask"; Headers = @{ Authorization = "Bearer $appToken" }; ContentType = 'application/json'; UseBasicParsing = $true
              Body = (@{ question = 'Is the soil moisture of my pot ok?' } | ConvertTo-Json -Compress) }
$stream = Invoke-WebRequest @request
Check 'the answer streams as server-sent events' ($stream.Headers['Content-Type'] -like 'text/event-stream*')
$events = @($stream.Content -split "`n`n" | Where-Object { $_.Trim().StartsWith('data:') } | ForEach-Object { $_.Trim().Substring(5).Trim() | ConvertFrom-Json })
$done = @($events | Where-Object { $_.type -eq 'done' })[0]
$answer = (@($events | Where-Object { $_.type -eq 'token' } | ForEach-Object { $_.text }) -join '')
Check 'the assistant answered' ($done.mode -eq 'answer') "(mode $($done.mode), reason $($done.reason))"
# The mock AI of Docs-Brain builds its answer from what Greenhaus told it about the pot, so the latest reading and its state must show up in it.
Check 'the answer is grounded in the pot''s latest reading (22, below the target)' ($answer -match '22' -and $answer -match 'below') "(answer: $answer)"
$after = Wait-Until { $u = (Call 'GET' "$greenhaus/me/entitlements" $appToken).usage.aiQuestionsThisMonth; if ($u -eq ($before + 1)) { $u } } 'the question to be counted'
Check 'the question counts against the monthly quota' ($null -ne $after)

# A species question is answered from the shared care corpus, which every user's assistant can cite.
$care = @{ Method = 'Post'; Uri = "$greenhaus/pots/$($pot.id)/assistant/ask"; Headers = @{ Authorization = "Bearer $appToken" }; ContentType = 'application/json'; UseBasicParsing = $true
           Body = (@{ question = 'How should I water basil?' } | ConvertTo-Json -Compress) }
$careStream = Invoke-WebRequest @care
$careEvents = @($careStream.Content -split "`n`n" | Where-Object { $_.Trim().StartsWith('data:') } | ForEach-Object { $_.Trim().Substring(5).Trim() | ConvertFrom-Json })
$sources = @($careEvents | Where-Object { $_.type -eq 'sources' } | ForEach-Object { $_.sources })
$basil = @($sources | Where-Object { $_.shared -eq $true -and $_.title -like 'Basil*' })
Check 'the answer cites the shared care text for basil' ($basil.Count -ge 1) "(sources: $(($sources | ForEach-Object { $_.title }) -join '; '))"

# ---------------------------------------------------------------------------------------------------------------------
Step 'The shop shows the order as in use'
$activated = Wait-Until { $t = Call 'GET' "$gateway/orders/$orderId/timeline" $shopToken; if (@($t | Where-Object { $_.step -eq 'Activated' }).Count) { $t } } 'the Activated step'
Check 'the order timeline has the Activated step with the device id' ($activated -and (@($activated | Where-Object { $_.step -eq 'Activated' })[0].detail -eq $deviceId))
$final = Call 'GET' "$gateway/orders/$orderId" $shopToken
Check 'the order is shipped and in use' ($final.status -eq 'Shipped' -and [bool]$final.activatedAt)

if ($Return -or $WaitForRevocation) {
    Step 'The person returns the goods: the purchase grant is at risk'
    $null = Call 'POST' "$gateway/orders/$orderId/return" $shopToken
    $risk = Wait-Until { $e = Call 'GET' "$greenhaus/me/entitlements" $appToken; $s = @($e.sources | Where-Object { $_.status -eq 'at_risk' }); if ($s.Count) { $e } } 'the grant to become at risk'
    Check 'the grant is at risk, with the deadline to resolve it' ([bool]$risk -and [bool]@($risk.sources)[0].graceEndsAt)
    Check 'the person keeps the tier during the grace period' ($risk.tier -eq 'gardener')

    if ($WaitForRevocation) {
        Step 'The grace period ends: the grant is revoked and the tier falls back'
        $back = Wait-Until { $e = Call 'GET' "$greenhaus/me/entitlements" $appToken; if ($e.tier -eq 'novice') { $e } } 'the tier to fall back to Novice' 120
        Check 'the tier is Novice again' ([bool]$back)
        Check 'no purchase grant is listed any more' ($back -and @($back.sources | Where-Object { $_.type -eq 'purchase_grant' }).Count -eq 0)
        $kept = Call 'GET' "$greenhaus/pots/$($pot.id)" $appToken
        Check 'the pot and its device are kept (nothing is deleted on a downgrade)' ($kept.id -eq $pot.id)
    }
}

# ---------------------------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:failed.Count -eq 0) { Write-Host 'The whole story holds, from the purchase to the reading.' -ForegroundColor Green; exit 0 }
Write-Host "$($script:failed.Count) check(s) failed:" -ForegroundColor Red
$script:failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
