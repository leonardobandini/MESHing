# SPDX-License-Identifier: GPL-2.0-or-later
# Verify a never-saved SAP/Bridge model is detected before and after its first Save.
param([Parameter(Mandatory=$true)][ValidateSet('SAP2000','CSiBridge')][string]$HostKind,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$Trial)
$ErrorActionPreference = 'Stop'
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $Trial.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or
    -not (Test-Path -LiteralPath $Plugin -PathType Leaf)) {
    throw 'Trial parent, directory or candidate DLL is unsafe.'
}
$install = 'C:\Program Files\Computers and Structures\' + $HostKind + ' 27'
$exe = Join-Path $install ($HostKind + '.exe')
$api = Join-Path $install 'CSiAPIv1.dll'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $api -PathType Leaf)) { throw 'Host executable or API not installed.' }
New-Item -ItemType Directory -Path $Trial | Out-Null
Add-Type -Path $api
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$saved = $type.GetMethod('HasSavedModelFile', [Reflection.BindingFlags]'Static,NonPublic')
if (-not $saved) { throw 'Saved-model guard missing.' }
$sap = $null
$ownedPid = 0
try {
    $mutex = [Threading.Mutex]::new($false, 'Local\FEMeshUnsavedModelStartup')
    try {
        $null = $mutex.WaitOne()
        $before = @(Get-Process $HostKind -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $helper = [CSiAPIv1.cHelper](New-Object CSiAPIv1.Helper)
        $sap = $helper.CreateObject($exe)
        $new = @(Get-Process $HostKind -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -notin $before })
        if ($new.Count -ne 1 -or -not $sap) { throw 'Cannot identify the isolated host.' }
        $ownedPid = $new[0].Id
        $start = [CSiAPIv1.cOAPI].GetMethod('ApplicationStart', [Type[]]@())
        if ([int]$start.Invoke($sap.PSObject.BaseObject,[object[]]@()) -ne 0) {
            throw 'Could not start isolated host.'
        }
    } finally {
        $mutex.ReleaseMutex(); $mutex.Dispose()
    }
    $model = $sap.SapModel
    if ($model.InitializeNewModel([CSiAPIv1.eUnits]::kN_m_C) -ne 0 -or
        $model.File.NewBlank() -ne 0) { throw 'Cannot create a blank model.' }
    $unsaved = [bool]$saved.Invoke($null,[object[]]@($model.PSObject.BaseObject))
    if ($unsaved) { throw 'A never-saved model was incorrectly treated as saved.' }
    $extension = if ($HostKind -eq 'SAP2000') { '.sdb' } else { '.bdb' }
    $file = Join-Path $Trial ('new-model' + $extension)
    if ($model.File.Save($file) -ne 0 -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'Could not save isolated model for the first time.'
    }
    if (-not [bool]$saved.Invoke($null,[object[]]@($model.PSObject.BaseObject))) {
        throw 'MESHing did not recognize the newly saved model.'
    }
    "$HostKind UNSAVED_DETECTED=PASS FIRST_SAVE_DETECTED=PASS PID=$ownedPid"
} finally {
    if ($sap) {
        try {
            $exit = [CSiAPIv1.cOAPI].GetMethod('ApplicationExit', [Type[]]@([bool]))
            [void]$exit.Invoke($sap.PSObject.BaseObject,[object[]]@($false))
        } catch { }
    }
    if ($ownedPid) {
        Start-Sleep -Milliseconds 700
        $process = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq $HostKind) {
            Stop-Process -Id $ownedPid -ErrorAction SilentlyContinue
        }
    }
}
