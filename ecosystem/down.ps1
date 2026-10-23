# Stops the ecosystem started by up.ps1. Add -Volumes to delete the data too (fresh catalog, stock and users next time).
param([switch]$Volumes)

$greenhaus = (Resolve-Path "$PSScriptRoot\..").Path
$work = Join-Path (Split-Path $greenhaus -Parent) '.ecosystem'
$flag = if ($Volumes) { '-v' } else { $null }
$env:GREENHAUS_DIR = $greenhaus.Replace('\', '/')

foreach ($name in 'orderflow', 'pulse', 'docs-brain') {
    $dir = Join-Path $work $name
    if (Test-Path $dir) { docker compose -p "eco-$($name.Replace('-', ''))" --project-directory $dir -f "$dir/docker-compose.yml" down $flag }
}
docker compose -p eco-greenhaus --project-directory $greenhaus --profile real -f "$greenhaus/docker-compose.yml" -f "$greenhaus/docker-compose.auth.yml" down $flag
