# Writes one care text per species from data/species.json and puts them into the shared corpus of Docs-Brain, where the
# assistant of every user can find them. Idempotent: a text replaces the one with the same title. Run by up.ps1.
#
#   ./ecosystem/ingest-care.ps1
param(
    [string]$DocsBrain = 'http://localhost:5090',
    [string]$TokenUrl = 'http://localhost:8181/realms/greenhaus/protocol/openid-connect/token',
    [string]$ClientId = 'greenhaus-assistant',
    [string]$ClientSecret = 'greenhaus-assistant-dev-secret'
)

$ErrorActionPreference = 'Stop'
$greenhaus = (Resolve-Path "$PSScriptRoot\..").Path
$species = (Get-Content "$greenhaus\data\species.json" -Raw -Encoding UTF8 | ConvertFrom-Json).species
$token = (Invoke-RestMethod -Method Post -Uri $TokenUrl -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; client_id = $ClientId; client_secret = $ClientSecret }).access_token

function Range($r, $unit) { if ($null -eq $r) { return $null }; "$($r.min) to $($r.max)$unit" }

# The keyword half of Docs-Brain's search does not stem, so the texts use the words people ask with: the common name in
# singular and plural, "water", "light", "fertilize". Short sections, separated by blank lines, one chunk each.
function Care-Text($s) {
    $name = $s.commonName
    $plural = if ($name -match '(s|x|ch|sh)$') { "${name}es" } else { "${name}s" }
    $p = $s.preferred
    $sections = @("$name ($($s.scientificName)) care")
    $sections += "$name is a $($s.category) plant. This is how to care for $name, or for several $plural, in a pot or a bed."
    if ($p.soil_moisture) {
        $sections += "How to water ${name}: keep the soil moisture of $name between $(Range $p.soil_moisture ' percent'). Water $name when the soil gets drier than $($p.soil_moisture.min) percent, and stop before it is wetter than $($p.soil_moisture.max) percent. Too dry or too wet soil stresses $plural."
    }
    if ($p.light) {
        $sections += "Light for ${name}: $plural do best with $(Range $p.light ' lux'). Too little light makes $name weak, too much can scorch the leaves."
    }
    if ($p.temperature -or $p.air_humidity) {
        $climate = @()
        if ($p.temperature) { $climate += "temperature of $(Range $p.temperature ' degrees Celsius')" }
        if ($p.air_humidity) { $climate += "air humidity of $(Range $p.air_humidity ' percent')" }
        $sections += "Temperature and humidity for ${name}: $plural like a " + ($climate -join ' and ') + '.'
    }
    if ($p.airflow) { $sections += "Air flow for ${name}: gentle air movement of $(Range $p.airflow ' metres per second') keeps $name healthy and prevents mould." }
    if ($p.ph) { $sections += "Soil pH for ${name}: a pH of $(Range $p.ph '') suits $plural." }
    if ($s.fertilizerIntervalDays) { $sections += "How often to fertilize ${name}: fertilize $name about every $($s.fertilizerIntervalDays) days in the growing season. A pump cannot add fertilizer, so Greenhaus reminds you with a care task." }
    ($sections -join "`n`n") + "`n"
}

$work = Join-Path $env:TEMP "care-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
$failed = 0
foreach ($s in $species) {
    $title = "$($s.commonName) ($($s.scientificName)) care"
    $file = Join-Path $work "$($s.id).md"
    [IO.File]::WriteAllText($file, (Care-Text $s), (New-Object System.Text.UTF8Encoding($false)))
    $url = "$DocsBrain/api/shared/documents/$([Uri]::EscapeDataString($title))"
    $code = curl.exe -s -o NUL -w '%{http_code}' -X PUT -H "Authorization: Bearer $token" -F "file=@$file;type=text/markdown" $url
    if ($code -ne '201') { Write-Host "  FAIL $title -> $code" -ForegroundColor Red; $failed++ }
}
Remove-Item -Recurse -Force $work
if ($failed) { throw "$failed care text(s) were not stored" }
Write-Host "stored $($species.Count) care texts in the shared corpus"