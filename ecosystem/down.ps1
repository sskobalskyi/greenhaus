# Stops the ecosystem started by up.ps1. Add -Volumes to delete the data too (fresh catalog, stock and users next time).
param([switch]$Volumes)

$greenhaus = (Resolve-Path "$PSScriptRoot\..").Path
$cv = Split-Path $greenhaus -Parent
while ($cv -and -not (Test-Path (Join-Path $cv 'orderflow'))) { $cv = Split-Path $cv -Parent }
$work = Join-Path $cv '.ecosystem'
$flag = if ($Volumes) { '-v' } else { $null }
$env:GREENHAUS_DIR = $greenhaus.Replace('\', '/')

# The controllers that e2e.ps1 started.
docker ps -aq --filter "name=eco-kit-" | ForEach-Object { docker rm -f $_ | Out-Null }

foreach ($name in 'orderflow', 'pulse', 'docs-brain') {
    $dir = Join-Path $work $name
    if (Test-Path $dir) { docker compose -p "eco-$($name.Replace('-', ''))" --project-directory $dir -f "$dir/docker-compose.yml" down --remove-orphans $flag }
}
docker compose -p eco-greenhaus --project-directory $greenhaus --profile real -f "$greenhaus/docker-compose.yml" -f "$greenhaus/docker-compose.auth.yml" down $flag
