# SPDX-License-Identifier: GPL-2.0-or-later
# Exercise the installer prerequisite guard without changing the registry or downloading software.
param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Installer).Path)
$type = $assembly.GetType('SQMPluginInstaller', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$check = $type.GetMethod('HasRequiredVisualCppVersion', $flags)
$parse = $type.GetMethod('ParseVisualCppVersion', $flags)
$registry = $type.GetMethod('ReadVisualCppVersion', $flags)
$signature = $type.GetMethod('IsMicrosoftSigned', $flags)
if (-not $check -or -not $parse -or -not $registry -or -not $signature) {
    throw 'Installer prerequisite guard or publisher verification is missing.'
}
foreach ($case in @(
    @{ Version = $null; Expected = $false },
    @{ Version = '14.41.99999.9'; Expected = $false },
    @{ Version = '14.42.34226.2'; Expected = $false },
    @{ Version = '14.42.34226.3'; Expected = $true },
    @{ Version = '14.50.35719.0'; Expected = $true })) {
    $version = if ($case.Version) { [Version]$case.Version } else { $null }
    if ([bool]$check.Invoke($null, [object[]]@($version)) -ne $case.Expected) {
        throw "Incorrect prerequisite boundary for $($case.Version)"
    }
}
$parsed = $parse.Invoke($null, [object[]]@('v14.50.35719.00'))
if ([string]$parsed -ne '14.50.35719.0') {
    throw 'Microsoft version value was not parsed.'
}
$current = $registry.Invoke($null, [object[]]@())
if (-not [bool]$check.Invoke($null, [object[]]@($current))) {
    throw 'The known installed x64 Visual C++ runtime was not detected.'
}
$signed = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not [bool]$signature.Invoke($null, [object[]]@($signed.PSObject.BaseObject))) {
    throw 'A signed Microsoft executable was rejected.'
}
$unsigned = (Resolve-Path -LiteralPath $Installer).Path
if ([bool]$signature.Invoke($null, [object[]]@($unsigned.PSObject.BaseObject))) {
    throw 'An unsigned MESHing installer was accepted as Microsoft-signed.'
}
"VISUAL_CPP_PREREQUISITE=PASS ABSENT=BLOCKED VERSION_BOUNDARY=PASS INSTALLED=$current SIGNATURE=PASS UNSIGNED=BLOCKED DOWNLOAD=NOT_INVOKED"
