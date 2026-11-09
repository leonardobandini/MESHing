# SPDX-License-Identifier: GPL-2.0-or-later
# Refresh corresponding sources without rebuilding the tested binary ZIP.
param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$candidate = Join-Path $artifacts "candidate-v$Version"
$name = "MESHing-$Version-source.zip"
$source = Join-Path $artifacts $name
$target = Join-Path $candidate $name
$binary = Join-Path $candidate "MESHing-$Version.zip"
$alias = Join-Path $candidate 'MESHing-latest.zip'
foreach ($path in @($source, "$source.sha256", $target, "$target.sha256", $binary, $alias)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing candidate asset: $path" }
}
$oldHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = "$oldHash  $name"
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $oldHash -or
    [IO.File]::ReadAllText("$source.sha256").Trim() -cne $expected -or
    [IO.File]::ReadAllText("$target.sha256").Trim() -cne $expected) {
    throw 'Existing source archive or checksum is inconsistent.'
}
$binaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
if ($binaryHash -ne (Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash) {
    throw 'The Latest alias differs from the binary ZIP.'
}
Remove-Item -LiteralPath $source, "$source.sha256"
try {
    & python (Join-Path $PSScriptRoot 'build-source-v2.py') $Version
    if ($LASTEXITCODE -ne 0) { throw 'Corresponding-source build failed.' }
    Copy-Item -LiteralPath $source -Destination $target -Force
    Copy-Item -LiteralPath "$source.sha256" -Destination "$target.sha256" -Force
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $binaryHash -or
        (Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash -ne $binaryHash) {
        throw 'Binary ZIP changed while refreshing sources.'
    }
    "SOURCE_REFRESH=PASS SHA256=$((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant())"
} catch {
    Copy-Item -LiteralPath $target -Destination $source -Force
    Copy-Item -LiteralPath "$target.sha256" -Destination "$source.sha256" -Force
    throw
}
