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
# The embeddings of Docs-Brain do not work across languages (a Ukrainian question does not find an English text), so every
# species also gets a Ukrainian text of its own, under its own title. The words and templates are data (data/care-texts-uk.json),
# which keeps non-ASCII text out of this script.
$uk = Get-Content "$greenhaus\data\care-texts-uk.json" -Raw -Encoding UTF8 | ConvertFrom-Json
function Fill([string]$template, [hashtable]$values) {
    foreach ($key in $values.Keys) { $template = $template.Replace("{$key}", [string]$values[$key]) }
    $template
}
function Range-Values($r) { @{ min = $r.min; max = $r.max } }
foreach ($s in $species) {
    $n = $uk.species.($s.id)
    if (-not $n) { Write-Host "  FAIL no Ukrainian names for $($s.id)" -ForegroundColor Red; $failed++; continue }
    $base = @{ name = $n.name; acc = $n.acc; plural = $n.plural; latin = $s.scientificName; category = $uk.categories.($s.category) }
    $t = $uk.templates; $p = $s.preferred
    $sections = @((Fill $t.title $base), (Fill $t.intro $base))
    if ($p.soil_moisture) { $sections += Fill $t.water ($base + (Range-Values $p.soil_moisture)) }
    if ($p.light) { $sections += Fill $t.light ($base + (Range-Values $p.light)) }
    $climate = @()
    if ($p.temperature) { $climate += Fill $t.climateTemperature (Range-Values $p.temperature) }
    if ($p.air_humidity) { $climate += Fill $t.climateHumidity (Range-Values $p.air_humidity) }
    if ($climate.Count) { $sections += Fill $t.climate ($base + @{ climate = ($climate -join ', ') }) }
    if ($p.airflow) { $sections += Fill $t.airflow ($base + (Range-Values $p.airflow)) }
    if ($p.ph) { $sections += Fill $t.ph ($base + (Range-Values $p.ph)) }
    if ($s.fertilizerIntervalDays) { $sections += Fill $t.fertilizer ($base + @{ days = $s.fertilizerIntervalDays }) }
    # Docs-Brain allows letters, digits, space and . _ - ( ) in a title, so an apostrophe (м'ята) is left out of it, not of the text.
    $title = (Fill $t.title $base).Replace("'", '')
    $file = Join-Path $work "$($s.id).uk.md"
    [IO.File]::WriteAllText($file, (($sections -join "`n`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    $url = "$DocsBrain/api/shared/documents/$([Uri]::EscapeDataString($title))"
    $code = curl.exe -s -o NUL -w '%{http_code}' -X PUT -H "Authorization: Bearer $token" -F "file=@$file;type=text/markdown" $url
    if ($code -ne '201') { Write-Host "  FAIL $($s.id) (uk) -> $code" -ForegroundColor Red; $failed++ }
}

Remove-Item -Recurse -Force $work
if ($failed) { throw "$failed care text(s) were not stored" }
Write-Host "stored $($species.Count) English and $($species.Count) Ukrainian care texts in the shared corpus"