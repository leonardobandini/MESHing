# SPDX-License-Identifier: GPL-2.0-or-later
# Assemble only the ten intended local v2.0.66 release assets, without publishing.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifacts 'candidate-v2.0.66' }
$expected = Join-Path $artifacts 'candidate-v2.0.66'
if (-not [string]::Equals([IO.Path]::GetFullPath($OutputDirectory), [IO.Path]::GetFullPath($expected),
    [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $artifacts -PathType Container) -or
    (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'Candidate destination must be the new artifacts\candidate-v2.0.66 directory.'
}
$files = @('FEMesh-2.0.66.zip', 'Sap2000QuadMesher-2.0.66-source.zip',
    'gmsh-4.15.2-source.tgz', 'mmg-5.5.2-source.tar.gz')
$pinned = @{
    'gmsh-4.15.2-source.tgz' = 'be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e'
    'mmg-5.5.2-source.tar.gz' = '3078a4e281640ae927223d449d37dfb63e0a7afc8ed737fc075eb6e255269a65'
}
foreach ($name in $files) {
    $path = Join-Path $artifacts $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing local asset: $name" }
    if ($pinned.ContainsKey($name) -and
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pinned[$name]) {
        throw "Source hash changed: $name"
    }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach ($name in $files) {
    Copy-Item -LiteralPath (Join-Path $artifacts $name) -Destination (Join-Path $OutputDirectory $name)
}
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'FEMesh-2.0.66.zip') -Destination (Join-Path $OutputDirectory 'FEMesh-latest.zip')
$names = @('FEMesh-2.0.66.zip', 'FEMesh-latest.zip', 'Sap2000QuadMesher-2.0.66-source.zip',
    'gmsh-4.15.2-source.tgz', 'mmg-5.5.2-source.tar.gz')
foreach ($name in $names) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($name + '.sha256')),
        "$hash  $name`n", [Text.Encoding]::ASCII)
    "ASSET=$name SHA256=$hash"
}
if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'FEMesh-2.0.66.zip') -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'FEMesh-latest.zip') -Algorithm SHA256).Hash) {
    throw 'The versioned ZIP and latest alias must be byte-identical.'
}
$actual = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Select-Object -ExpandProperty Name)
$expectedNames = @($names + @($names | ForEach-Object { $_ + '.sha256' }))
if ($actual.Count -ne 10 -or @($actual | Where-Object { $_ -notin $expectedNames }).Count) {
    throw 'Candidate directory does not contain exactly the requested ten assets.'
}
"CANDIDATE_ASSETS=PASS COUNT=$($actual.Count) PATH=$OutputDirectory"
