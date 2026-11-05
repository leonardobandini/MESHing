# SPDX-License-Identifier: GPL-2.0-or-later
# Refresh only the local candidate's corresponding source after documentation checks.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$candidate = Join-Path $artifacts 'candidate-v3.0.12'
$source = Join-Path $artifacts 'MESHing-3.0.12-source.zip'
$checksum = $source + '.sha256'
$target = Join-Path $candidate 'MESHing-3.0.12-source.zip'
$targetChecksum = $target + '.sha256'
$binary = Join-Path $candidate 'MESHing-3.0.12.zip'
$alias = Join-Path $candidate 'MESHing-latest.zip'
$required = @($source, $checksum, $target, $targetChecksum, $binary, $alias)
if (-not (Test-Path -LiteralPath $artifacts -PathType Container) -or
    -not (Test-Path -LiteralPath $candidate -PathType Container) -or
    @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count) {
    throw 'Complete local candidate and source archives are required.'
}
$oldHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = "$oldHash  MESHing-3.0.12-source.zip"
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $oldHash -or
    [IO.File]::ReadAllText($checksum).Trim() -cne $expected -or
    [IO.File]::ReadAllText($targetChecksum).Trim() -cne $expected) {
    throw 'Existing source archives or checksums are inconsistent.'
}
$binaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
if ($binaryHash -ne (Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash) {
    throw 'The candidate Latest alias differs from the tested installer.'
}
Remove-Item -LiteralPath $source, $checksum
try {
    & python (Join-Path $PSScriptRoot 'build-source-v2.py') '3.0.12'
    if ($LASTEXITCODE -ne 0) { throw 'Corresponding-source build failed.' }
    Copy-Item -LiteralPath $source -Destination $target -Force
    Copy-Item -LiteralPath $checksum -Destination $targetChecksum -Force
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $binaryHash -or
        (Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash -ne $binaryHash) {
        throw 'The tested installer changed during source refresh.'
    }
    "MESHING_312_SOURCE_REFRESH=PASS SHA256=$((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant())"
} catch {
    Copy-Item -LiteralPath $target -Destination $source -Force
    Copy-Item -LiteralPath $targetChecksum -Destination $checksum -Force
    throw
}
