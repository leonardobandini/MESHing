# SPDX-License-Identifier: GPL-2.0-or-later
# Verify that the ZIP is self-contained after extraction, without repository paths.
param([string]$Zip = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\MESHing-3.0.5.zip'))
$ErrorActionPreference = 'Stop'
$version = [regex]::Match([IO.Path]::GetFileName($Zip), '^MESHing-(3\.\d+\.\d+)\.zip$').Groups[1].Value
if (-not $version) { throw 'Expected a versioned MESHing ZIP.' }
$tempParent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $tempParent -PathType Container)) { throw 'Temporary parent missing.' }
$trial = Join-Path $tempParent ('sqm-v2-standalone-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trial | Out-Null
try {
    $unpacked = Join-Path $trial 'extracted'
    Expand-Archive -LiteralPath $Zip -DestinationPath $unpacked
    $installer = Join-Path $unpacked 'SQMPluginInstaller.exe'
    & $installer verify $unpacked
    if ($LASTEXITCODE -ne 0) { throw 'Extracted installer failed verification.' }
    if (-not (Test-Path -LiteralPath (Join-Path $unpacked 'SOURCE.md') -PathType Leaf)) {
        throw 'Corresponding source link is missing from the installer ZIP.'
    }
    & (Join-Path $unpacked 'install_MESHing.cmd') verify
    if ($LASTEXITCODE -ne 0) { throw 'Extracted CMD launcher failed verification.' }
    $destination = Join-Path $trial 'installed'
    & $installer test-install $destination
    if ($LASTEXITCODE -ne 0) { throw 'Standalone test installation failed.' }
    & $installer test-install $destination
    if ($LASTEXITCODE -ne 0) { throw 'Standalone replacement failed.' }
    $dll = Join-Path $destination 'SapQuadTransitionPlugin.dll'
    if ((Get-Item $dll).VersionInfo.FileVersion -ne "$version.0" -or
        -not (Test-Path (Join-Path $destination 'docs\help-gmsh-v2\assets\nTEST027-ogrid-result.svg')) -or
        ($version -in @('2.0.36','2.0.37','2.0.38','2.0.39','2.0.40','2.0.41','2.0.42','2.0.43','2.0.44','2.0.45','2.0.46') -and
            -not (Test-Path (Join-Path $destination 'docs\help-gmsh-v2\assets\regular-grid-sap-settings.json')))) {
        throw 'Installed version or V2 help example missing.'
    }
    if ($version -in @('2.0.38','2.0.39','2.0.40','2.0.41','2.0.42','2.0.43','2.0.44','2.0.45','2.0.46')) {
        foreach ($language in @('it','en')) {
            foreach ($tab in $(if ($version -in @('2.0.43','2.0.44','2.0.45','2.0.46')) { @('gmsh','additional','transfer') } else { @('gmsh','additional','json','transfer') })) {
                $imageVersion = if ($version -in @('2.0.44','2.0.45','2.0.46')) { 'v244' } elseif ($version -eq '2.0.43') { 'v243' } elseif ($version -eq '2.0.42') { 'v242' } elseif ($version -eq '2.0.41') { 'v241' } elseif ($version -in @('2.0.39','2.0.40')) { 'v239' } else { 'v238' }
                if (-not (Test-Path (Join-Path $destination "docs\help-gmsh-v2\assets\interface-$imageVersion-$language-$tab.png"))) {
                    throw "Missing installed $language screenshot of $tab tab."
                }
            }
        }
    }
    "MESHING_STANDALONE=PASS VERSION=$((Get-Item $dll).VersionInfo.FileVersion)"
} finally {
    if (Test-Path -LiteralPath $trial) { Remove-Item -LiteralPath $trial -Recurse -Force }
}
