# SPDX-License-Identifier: GPL-2.0-or-later
# Stage exactly ten checked assets for the current local, unpublished version.
param([switch]$RebuildExisting)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^3[.]\d+[.]\d+$' -or -not (Test-Path -LiteralPath $artifacts -PathType Container)) {
    throw 'Expected a MESHing 3.x project version and artifacts directory.'
}
$output = Join-Path $artifacts "candidate-v$version"
$archives = @("MESHing-$version.zip", 'MESHing-latest.zip', "MESHing-$version-source.zip",
    'gmsh-4.15.2-source.tgz', 'mmg-5.5.2-source.tar.gz')
$names = @($archives + @($archives | ForEach-Object { $_ + '.sha256' }))
$pinned = @{
    'gmsh-4.15.2-source.tgz' = 'be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e'
    'mmg-5.5.2-source.tar.gz' = '3078a4e281640ae927223d449d37dfb63e0a7afc8ed737fc075eb6e255269a65'
}
if (Test-Path -LiteralPath $output) {
    $previous = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name)
    if (-not $RebuildExisting -or $previous.Count -ne 10 -or
        @(Get-ChildItem -LiteralPath $output -Directory).Count -or
        @($previous | Where-Object { $_ -notin $names }).Count) {
        throw "Refusing to replace an unrecognized candidate directory: $output"
    }
    Remove-Item -LiteralPath $output -Recurse -Force
}
foreach ($name in @("MESHing-$version.zip", "MESHing-$version-source.zip")) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifacts $name) -PathType Leaf)) {
        throw "The versioned build is missing: $name"
    }
}
foreach ($name in $pinned.Keys) {
    $file = Join-Path $PSScriptRoot "cache\$name"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pinned[$name]) {
        throw "Pinned upstream source differs: $name"
    }
}
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $artifacts "MESHing-$version.zip") `
    -Destination (Join-Path $output 'MESHing-latest.zip')
foreach ($name in $archives) {
    $source = if ($name -eq 'MESHing-latest.zip') { Join-Path $output $name }
        elseif ($name -in $pinned.Keys) { Join-Path $PSScriptRoot "cache\$name" }
        else { Join-Path $artifacts $name }
    if ($name -ne 'MESHing-latest.zip')
    { Copy-Item -LiteralPath $source -Destination (Join-Path $output $name) }
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output ($name + '.sha256')),
        "$hash  $name`n", [Text.Encoding]::ASCII)
    "ASSET=$name SHA256=$hash"
}
$actual = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name)
if ($actual.Count -ne 10 -or @($actual | Where-Object { $_ -notin $names }).Count -or
    (Get-FileHash -LiteralPath (Join-Path $output 'MESHing-latest.zip') -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $output "MESHing-$version.zip") -Algorithm SHA256).Hash) {
    throw "Candidate $version must contain ten assets with an identical Latest alias."
}
"MESHING_CANDIDATE=PASS COUNT=$($actual.Count) PATH=$output"
