# SPDX-License-Identifier: GPL-2.0-or-later
# Prepare only versioned 3.0.5 assets; the stable latest link remains untouched.
param([switch]$RebuildExisting)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$version = '3.0.5'
$output = Join-Path $artifacts "candidate-v$version"
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj')
if ($project.Project.PropertyGroup.Version -ne $version -or
    -not (Test-Path -LiteralPath $artifacts -PathType Container)) {
    throw 'Expected plugin version 3.0.5 and the artifacts directory.'
}
$archives = @("MESHing-$version.zip", "MESHing-$version-source.zip",
    'gmsh-4.15.2-source.tgz', 'mmg-5.5.2-source.tar.gz')
$names = @($archives + @($archives | ForEach-Object { $_ + '.sha256' }))
$pinned = @{
    'gmsh-4.15.2-source.tgz' = 'be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e'
    'mmg-5.5.2-source.tar.gz' = '3078a4e281640ae927223d449d37dfb63e0a7afc8ed737fc075eb6e255269a65'
}
if (Test-Path -LiteralPath $output) {
    $previous = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name)
    if (-not $RebuildExisting -or $previous.Count -ne 8 -or
        @(Get-ChildItem -LiteralPath $output -Directory).Count -or
        @($previous | Where-Object { $_ -notin $names }).Count) {
        throw 'Refusing to replace an unrecognized 3.0.5 candidate directory.'
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
foreach ($name in $archives) {
    $source = if ($name -in $pinned.Keys) { Join-Path $PSScriptRoot "cache\$name" }
        else { Join-Path $artifacts $name }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $name)
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output ($name + '.sha256')),
        "$hash  $name`n", [Text.Encoding]::ASCII)
    "ASSET=$name SHA256=$hash"
}
$actual = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name)
if ($actual.Count -ne 8 -or @($actual | Where-Object { $_ -notin $names }).Count -or
    (Test-Path -LiteralPath (Join-Path $output 'MESHing-latest.zip'))) {
    throw 'The 3.0.5 candidate must contain only eight versioned/source assets.'
}
"MESHING_CANDIDATE=PASS COUNT=$($actual.Count) PATH=$output"
