# SPDX-License-Identifier: GPL-2.0-or-later
# Test migration on copies of the SAP2000 menu files, not on the live profile.
param([string]$Installer = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Sap2000QuadMesher-2.0.46\SQMPluginInstaller.exe'))
$ErrorActionPreference = 'Stop'
$profile = Join-Path $env:LOCALAPPDATA 'Computers and Structures\SAP2000 27'
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Missing temporary test parent.' }
$trial = Join-Path $parent ('sqm-v2-menu-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trial | Out-Null
try {
    foreach ($name in @('SAP2000v27.ini', 'SAP2000v27.settings')) {
        Copy-Item -LiteralPath (Join-Path $profile $name) -Destination $trial
    }
    & $Installer test-menu $trial
    if ($LASTEXITCODE -ne 0) { throw 'Menu migration test failed.' }
    $ini = [IO.File]::ReadAllText((Join-Path $trial 'SAP2000v27.ini'))
    $section = [regex]::Match($ini, '(?ms)^\[PlugIn\]\r?\n.*?(?=^\[|\z)').Value
    $names = [regex]::Matches($section, '(?m)^\s*PlugInName=([^\r\n]*)')
    $count = [regex]::Match($section, '(?m)^\s*NumberPlugIns=(\d+)').Groups[1].Value
    if ([int]$count -ne $names.Count -or @($names | Where-Object { $_.Groups[1].Value.Trim() -eq 'SapGmshPlugin' }).Count -ne 0 -or
        @($names | Where-Object { $_.Groups[1].Value.Trim() -eq 'SapQuadTransitionPlugin' }).Count -ne 1) {
        throw 'INI still contains the preview or has an invalid plugin count.'
    }
    [xml]$settings = Get-Content -LiteralPath (Join-Path $trial 'SAP2000v27.settings') -Raw
    [xml]$entries = $settings.Settings.PlugIn
    $pluginNames = @($entries.ArrayOfString.string | ForEach-Object {
        [xml]$entry = $_.TrimStart([char]0xfeff,[char]' ',[char]13,[char]10)
        $entry.PlugIn.m_name
    })
    if ($pluginNames -contains 'SapGmshPlugin' -or @($pluginNames | Where-Object { $_ -eq 'SapQuadTransitionPlugin' }).Count -ne 1) {
        throw 'SAP2000 settings still contain the old V2 preview entry.'
    }
    'V2_MENU_MIGRATION=PASS'
} finally {
    if (Test-Path -LiteralPath $trial) { Remove-Item -LiteralPath $trial -Recurse -Force }
}
