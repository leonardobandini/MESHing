# SPDX-License-Identifier: GPL-2.0-or-later
# Assemble ten local v2.0.68 release assets without touching published releases.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$version = '2.0.68'
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj')
if ($project.Project.PropertyGroup.Version -ne $version) { throw 'Plugin version differs from the candidate version.' }
$expected = Join-Path $artifacts "candidate-v$version"
if (-not $OutputDirectory) { $OutputDirectory = $expected }
if (-not [string]::Equals([IO.Path]::GetFullPath($OutputDirectory), [IO.Path]::GetFullPath($expected),
    [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $artifacts -PathType Container) -or
    (Test-Path -LiteralPath $OutputDirectory)) {
    throw "Candidate destination must be the new artifacts\candidate-v$version directory."
}
$installer = "FEMesh-$version.zip"
$source = "Sap2000QuadMesher-$version-source.zip"
$pinned = @{
    'gmsh-4.15.2-source.tgz' = 'be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e'
    'mmg-5.5.2-source.tar.gz' = '3078a4e281640ae927223d449d37dfb63e0a7afc8ed737fc075eb6e255269a65'
}
$archives = @($installer, 'FEMesh-latest.zip', $source,
    'gmsh-4.15.2-source.tgz', 'mmg-5.5.2-source.tar.gz')
foreach ($name in @($installer, $source)) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifacts $name) -PathType Leaf)) {
        throw "The new local build is missing: $name"
    }
}
foreach ($name in $pinned.Keys) {
    $path = Join-Path $PSScriptRoot "cache\$name"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pinned[$name]) {
        throw "Pinned upstream source differs: $name"
    }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach ($name in @($installer, $source)) {
    Copy-Item -LiteralPath (Join-Path $artifacts $name) -Destination (Join-Path $OutputDirectory $name)
}
foreach ($name in $pinned.Keys) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "cache\$name") -Destination (Join-Path $OutputDirectory $name)
}
Copy-Item -LiteralPath (Join-Path $OutputDirectory $installer) -Destination (Join-Path $OutputDirectory 'FEMesh-latest.zip')
foreach ($name in $archives) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($name + '.sha256')),
        "$hash  $name`n", [Text.Encoding]::ASCII)
    "ASSET=$name SHA256=$hash"
}
if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory $installer) -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'FEMesh-latest.zip') -Algorithm SHA256).Hash) {
    throw 'The versioned installer and latest alias are not identical.'
}
$actual = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Select-Object -ExpandProperty Name)
$expectedNames = @($archives + @($archives | ForEach-Object { $_ + '.sha256' }))
if ($actual.Count -ne 10 -or @($actual | Where-Object { $_ -notin $expectedNames }).Count) {
    throw 'The local candidate must contain exactly ten expected assets.'
}
"CANDIDATE_ASSETS=PASS COUNT=$($actual.Count) PATH=$OutputDirectory"
