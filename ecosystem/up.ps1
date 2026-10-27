# Starts the whole ecosystem on this machine: Orderflow (shop), Pulse (devices) and the Greenhaus services, on one broker
# and one Keycloak realm. Local Docker only. See ecosystem/README.md.
#
#   ./ecosystem/up.ps1            # start (exports Orderflow and Pulse from origin/main on the first run)
#   ./ecosystem/up.ps1 -Refresh   # export them again from the latest origin/main first
#   ./ecosystem/up.ps1 -ShortGrace   # billing's grace period for a returned order is 20 seconds instead of 7 days (for e2e.ps1 -WaitForRevocation)
param([switch]$Refresh, [switch]$ShortGrace)

# Docker writes its progress to stderr, so native errors must not stop the script: exit codes are checked instead.
$ErrorActionPreference = 'Continue'
$greenhaus = (Resolve-Path "$PSScriptRoot\..").Path
# The other projects sit next to this repository, or above it when this is a git worktree.
$cv = Split-Path $greenhaus -Parent
while ($cv -and -not (Test-Path (Join-Path $cv 'orderflow'))) { $cv = Split-Path $cv -Parent }
if (-not $cv) { throw 'The orderflow, pulse and docs-brain repositories were not found next to this one' }
$work = Join-Path $cv '.ecosystem'
New-Item -ItemType Directory -Force $work | Out-Null

# Pulse and Orderflow belong to other sessions and their working trees may hold unfinished work, so the stack is built from
# a clean export of each project's main branch.
function Export-Main([string]$repo, [string]$mustHave = '') {
    $target = Join-Path $work $repo
    # An export made before a project gained a file this script needs is stale, whatever -Refresh says.
    $stale = $mustHave -ne '' -and -not (Test-Path (Join-Path $target $mustHave))
    if ((Test-Path $target) -and -not $Refresh -and -not $stale) { return $target }
    $source = Join-Path $cv $repo
    git -C $source fetch -q origin
    $zip = Join-Path $work "$repo.zip"
    git -C $source archive --format=zip -o $zip origin/main
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Expand-Archive -Force $zip $target
    Remove-Item $zip
    Write-Host "exported $repo from origin/main ($(git -C $source rev-parse --short origin/main))"
    return $target
}

function Wait-Url([string]$url, [string]$name, [int]$seconds = 180) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        try { if ((Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { Write-Host "up: $name"; return } } catch { }
        Start-Sleep -Seconds 2
    }
    throw "$name did not become ready at $url"
}

$orderflow = Export-Main 'orderflow'
$pulse = Export-Main 'pulse' 'docker-compose.mqtt-auth.yml'
$docsBrain = Export-Main 'docs-brain'
if ($ShortGrace) { $env:BILLING_GRACE = '00:00:20'; $env:BILLING_TICK = '00:00:05' }
$env:GREENHAUS_DIR = $greenhaus.Replace('\', '/')

# Orderflow first: its RabbitMQ is the one broker of the ecosystem. Its own Keycloak does not start, it uses the shared realm.
Write-Host '== Orderflow'
docker compose -p eco-orderflow --project-directory $orderflow `
    -f "$orderflow/docker-compose.yml" -f "$orderflow/docker-compose.shared-realm.yml" -f "$greenhaus/ecosystem/orderflow.yml" `
    up -d --build
if ($LASTEXITCODE) { throw 'Orderflow did not start' }

# Greenhaus: Keycloak with the shared realm, the API, billing and fulfilment, with their Pulse token from that realm.
Write-Host '== Greenhaus'
$env:PULSE_URL = 'http://host.docker.internal:5080/api/pulse/v2'
$env:PULSE_TOKEN_URL = 'http://host.docker.internal:8181/realms/greenhaus/protocol/openid-connect/token'
$env:PULSE_CLIENT_ID = 'pulse-service'
$env:PULSE_CLIENT_SECRET = 'pulse-dev-secret'
$env:DOCSBRAIN_TOKEN_URL = $env:PULSE_TOKEN_URL
$env:DOCSBRAIN_CLIENT_ID = 'greenhaus-assistant'
$env:DOCSBRAIN_CLIENT_SECRET = 'greenhaus-assistant-dev-secret'
docker compose -p eco-greenhaus --project-directory $greenhaus --profile real `
    -f "$greenhaus/docker-compose.yml" -f "$greenhaus/docker-compose.auth.yml" -f "$greenhaus/ecosystem/greenhaus.yml" `
    up -d --build api billing fulfilment keycloak
if ($LASTEXITCODE) { throw 'Greenhaus did not start' }

# Pulse: only what the check needs (API, Kafka-compatible broker, TimescaleDB, and EMQX for the devices, on host port 1884, which asks Pulse
# who may connect: a device is its id and the credential from the packing bundle).
# Its dashboard is served on 8085 (PULSE_WEB_PORT), because 8080 is the Orderflow gateway; the pulse-web client of the shared realm allows it.
Write-Host '== Pulse'
$env:PULSE_WEB_PORT = '8085'
docker compose -p eco-pulse --project-directory $pulse `
    -f "$pulse/docker-compose.yml" -f "$pulse/docker-compose.shared-realm.yml" -f "$pulse/docker-compose.events.yml" -f "$pulse/docker-compose.mqtt.yml" -f "$pulse/docker-compose.mqtt-auth.yml" -f "$greenhaus/ecosystem/pulse.yml" `
    up -d --build redpanda timescaledb emqx api web
if ($LASTEXITCODE) { throw 'Pulse did not start' }

# Docs-Brain: the assistant behind the Greenhaus API. Mock AI, so no model download; only its API and database. Its web UI would claim 8090.
Write-Host '== Docs-Brain'
docker compose -p eco-docsbrain --project-directory $docsBrain --env-file "$greenhaus/ecosystem/docsbrain.env" `
    -f "$docsBrain/docker-compose.yml" `
    up -d --build postgres api
if ($LASTEXITCODE) { throw 'Docs-Brain did not start' }

# The kit emulator image, which e2e.ps1 runs as the controller of the allocated device.
Write-Host '== Kit emulator'
docker build -q -f "$greenhaus/tools/KitEmulator/Dockerfile" -t greenhaus-kit-emulator $greenhaus | Out-Null
if ($LASTEXITCODE) { throw 'The kit emulator image did not build' }

Write-Host '== Waiting'
Wait-Url 'http://localhost:8181/realms/greenhaus/.well-known/openid-configuration' 'Keycloak (greenhaus realm)'
Wait-Url 'http://localhost:8080/health' 'Orderflow gateway'
Wait-Url 'http://localhost:5080/health' 'Pulse API'
Wait-Url 'http://localhost:8085' 'Pulse dashboard'
Wait-Url 'http://localhost:8091/health' 'Greenhaus API'
Wait-Url 'http://localhost:8092/health' 'Greenhaus billing'
Wait-Url 'http://localhost:8093/health' 'Greenhaus fulfilment'
Wait-Url 'http://localhost:5090/health' 'Docs-Brain API'
& "$greenhaus/ecosystem/ingest-care.ps1"
Write-Host 'The ecosystem is up. Run ./ecosystem/e2e.ps1 for the end-to-end check, ./ecosystem/down.ps1 to stop it.'
