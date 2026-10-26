# SPDX-License-Identifier: GPL-2.0-or-later
# Inspect the restored V2 update notice without starting a CSI model.
param(
    [Parameter(Mandatory=$true)][string]$Plugin,
    [ValidateSet('Current','Future','Live','Unavailable','Offline','Draft','Prerelease','BadAsset','WrongRepo','BadTag')][string]$Scenario = 'Current',
    [string]$ExpectedVersion = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$pluginType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
$axis = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
$axes = [Array]::CreateInstance($axis, 0)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$currentVersion = [version]$pluginType.GetField('PluginVersion', $flags).GetRawConstantValue()
$current = "$($currentVersion.Major).$($currentVersion.Minor).$($currentVersion.Build)"
$future = "$($currentVersion.Major).$($currentVersion.Minor).$($currentVersion.Build + 1)"
$feed = [string]$pluginType.GetField('UpdateManifestUrl', $flags).GetRawConstantValue()
$download = [string]$pluginType.GetField('LatestDownloadUrl', $flags).GetRawConstantValue()
if ($feed -ne 'https://api.github.com/repos/leonardobandini/MESHing/releases/latest' -or
    $download -ne 'https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip') {
    throw 'The update check does not use the future MESHing release and static installer URL.'
}
$constructor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
    Where-Object { $_.GetParameters().Count -eq 6 })[0]
if ($null -eq $constructor) { throw 'Mesh options constructor missing.' }
$previousProxy = [System.Net.WebRequest]::DefaultWebProxy
if ($Scenario -eq 'Offline') {
    # Limit this process to an unreachable local proxy; never change OS settings.
    [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy('http://127.0.0.1:1')
}
if ($Scenario -notin @('Live','Offline')) {
    $version = if ($Scenario -in @('Future','Draft','Prerelease','BadAsset','WrongRepo','BadTag')) { $future } else { $current }
    $repo = if ($Scenario -eq 'WrongRepo') { 'FEMesh' } else { 'MESHing' }
    $tag = if ($Scenario -eq 'BadTag') { 'v3.invalid.1' } else { "v$version" }
    $json = @{
        tag_name = $tag;
        html_url = "https://github.com/leonardobandini/$repo/releases/tag/$tag";
        draft = $Scenario -eq 'Draft'; prerelease = $Scenario -eq 'Prerelease';
        assets = @(@{
            name = $(if ($Scenario -eq 'BadAsset') { 'other.zip' } else { 'MESHing-latest.zip' });
            browser_download_url = "https://github.com/leonardobandini/$repo/releases/download/$tag/MESHing-latest.zip"
        })
    } | ConvertTo-Json -Depth 5 -Compress
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = if ($Scenario -eq 'Unavailable') { '{' } else { $json }
} else {
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $null
}
$dialog = $constructor.Invoke([object[]]@('mm', $false, [string[]]@(), [string[]]@(), $axes, $true))
try {
    $dialog.Show()
    $instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
    $notice = $type.GetField('updateNotice', $instanceFlags).GetValue($dialog)
    $timer = $type.GetField('updateBlinkTimer', $instanceFlags).GetValue($dialog)
    if (-not $notice.Visible -or $notice.Left -ne 180 -or $notice.Top -ne 715) {
        throw 'The restored update notice is missing or moved.'
    }
    $expectUpdate = $Scenario -eq 'Future' -or
        ($Scenario -eq 'Live' -and $ExpectedVersion -and ([version]$ExpectedVersion -gt $currentVersion))
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        if ($expectUpdate -and $timer.Enabled -or
            ($Scenario -in @('Current','Draft','Prerelease','BadAsset','WrongRepo','BadTag') -or
                $Scenario -eq 'Live' -and -not $expectUpdate) -and
                $notice.Text -match '(up to date|aggiornata)' -or
            $Scenario -in @('Unavailable','Offline') -and $notice.Text -match '(Check for updates|Cerca aggiornamenti)') { break }
        Start-Sleep -Milliseconds 100
    }
    if ($expectUpdate) {
        if (-not $timer.Enabled -or $notice.Text -notmatch '(Update available|Aggiornamento disponibile)' -or
            $notice.Text -notmatch '(Download|Scarica)') { throw "Update notice missing: $($notice.Text)" }
        $actual = [string]$type.GetField('availableDownloadUrl', $instanceFlags).GetValue($dialog)
        if ($actual -ne $download) { throw "Download URL differs from the static installer link: $actual" }
        if ($ExpectedVersion -and $notice.Text -notmatch [regex]::Escape($ExpectedVersion)) {
            throw "Expected version $ExpectedVersion was not shown: $($notice.Text)"
        }
    } elseif ($Scenario -in @('Current','Draft','Prerelease','BadAsset','WrongRepo','BadTag','Live')) {
        if ($timer.Enabled -or $notice.Text -notmatch '(up to date|aggiornata)') {
            throw "Up-to-date notice missing: $($notice.Text)"
        }
    } elseif ($timer.Enabled -or $notice.Text -notmatch '(Check for updates|Cerca aggiornamenti)') {
        throw "Unavailable-feed fallback changed: $($notice.Text)"
    }
    "UPDATE_NOTICE_$($Scenario.ToUpperInvariant())=PASS TEXT=$($notice.Text)"
} finally {
    $dialog.Close(); $dialog.Dispose()
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $null
    [System.Net.WebRequest]::DefaultWebProxy = $previousProxy
}
