# SPDX-License-Identifier: GPL-2.0-or-later
# Exercise repeated OAPI lock-button transitions on an isolated SAP2000 model copy.
param([Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$InputModel,
      [Parameter(Mandatory=$true)][string]$Trial)
$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $temp -PathType Container) -or
    -not $Trial.StartsWith(($temp.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or
    -not (Test-Path -LiteralPath $Plugin -PathType Leaf) -or
    -not (Test-Path -LiteralPath $InputModel -PathType Leaf)) {
    throw 'The isolated lock test requires existing inputs and a new safe temporary folder.'
}
New-Item -ItemType Directory -Path $Trial | Out-Null
$copy = Join-Path $Trial 'lock-test-model.sdb'
$originalHash = (Get-FileHash -LiteralPath $InputModel -Algorithm SHA256).Hash
Copy-Item -LiteralPath $InputModel -Destination $copy
if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $originalHash) {
    throw 'The SAP2000 input copy differs.'
}
$apiPath = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$exe = 'C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe'
Add-Type -Path $apiPath
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$lockType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+DialogModelLock', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$constructor = @($lockType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 2 })[0]
$ready = $lockType.GetMethod('CheckReady', $flags)
$acknowledge = $lockType.GetMethod('AcknowledgeExternalRelock', $flags)
$unexpected = $lockType.GetProperty('ExternalUnlockObserved', $flags)
$unlock = $lockType.GetMethod('UnlockForWork', $flags)
$relock = $lockType.GetMethod('RelockAfterWork', $flags)
$afterRestore = $lockType.GetMethod('RelockAfterRestore', $flags)
if (-not $constructor -or -not $ready -or -not $acknowledge -or -not $unexpected -or -not $afterRestore) {
    throw 'Missing model-lock guard methods in the candidate DLL.'
}
$sap = $null; $ownedPid = 0; $guard = $null
try {
    $mutex = [Threading.Mutex]::new($false, 'Local\FEMeshLockLifecycleStartup')
    try {
        $null = $mutex.WaitOne()
        $before = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $helper = [CSiAPIv1.cHelper](New-Object CSiAPIv1.Helper)
        $sap = $helper.CreateObject($exe)
        $new = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $before })
        if ($null -eq $sap -or $new.Count -ne 1) { throw 'Cannot identify the isolated SAP2000 instance.' }
        $ownedPid = $new[0].Id
        [IO.File]::WriteAllText((Join-Path $Trial 'owned-sap.pid'), [string]$ownedPid)
        $start = [CSiAPIv1.cOAPI].GetMethod('ApplicationStart', [Type[]]@())
        if ([int]$start.Invoke($sap.PSObject.BaseObject, [object[]]@()) -ne 0) { throw 'SAP2000 did not start.' }
    } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    $model = $sap.SapModel
    if ($model.File.OpenFile($copy) -ne 0 -or $model.GetModelIsLocked()) {
        throw 'The copied model is unavailable or was already locked.'
    }
    $guard = $constructor.Invoke([object[]]@($model.PSObject.BaseObject, $true))
    [void]$ready.Invoke($guard, [object[]]@())
    [void]$unlock.Invoke($guard, [object[]]@())
    if ($model.File.OpenFile($copy) -ne 0) { throw 'The plugin-managed same-path reopen failed.' }
    [void]$relock.Invoke($guard, [object[]]@())
    # A delayed unlock produced by the plugin's own OpenFile must be
    # normalized before resuming user-interaction monitoring.
    if ($model.SetModelIsLocked($false) -ne 0) { throw 'Cannot simulate reload-induced unlock.' }
    [void]$afterRestore.Invoke($guard, [object[]]@())
    [void]$ready.Invoke($guard, [object[]]@())
    if (-not $model.GetModelIsLocked() -or $unexpected.GetValue($guard)) {
        throw 'The pre-mesh reopen was incorrectly reported as a manual unlock.'
    }
    'MANAGED_REOPEN_RELOCK=PASS FALSE_UNLOCK_WARNING=ABSENT'
    foreach ($phase in @('JUST_OPENED','BEFORE_GENERATE','PREVIEW_FIRST','PREVIEW_SECOND',
        'PREVIEW_THIRD','AFTER_APPLY','AFTER_UNDO')) {
        if ($phase -eq 'BEFORE_GENERATE') {
            [void]$unlock.Invoke($guard, [object[]]@())
            if ($model.GetModelIsLocked()) { throw 'MESHing could not unlock its own work phase.' }
            [void]$relock.Invoke($guard, [object[]]@())
        }
        if ($model.SetModelIsLocked($false) -ne 0 -or $model.GetModelIsLocked()) {
            throw "The lock-button simulation did not unlock during $phase."
        }
        $blocked = $false
        try { [void]$ready.Invoke($guard, [object[]]@()) }
        catch {
            $blocked = $_.Exception.ToString() -match 'unlocked while MESHing was open'
            if (-not $blocked) { throw }
        }
        if (-not $blocked -or -not $model.GetModelIsLocked() -or
            -not $unexpected.GetValue($guard)) {
            throw "MESHing did not immediately relock and report the unlock during $phase."
        }
        [void]$acknowledge.Invoke($guard, [object[]]@())
        if ($unexpected.GetValue($guard) -or -not $model.GetModelIsLocked()) {
            throw "MESHing did not resume the same locked state after OK during $phase."
        }
        [void]$ready.Invoke($guard, [object[]]@())
        "LOCK_PHASE=$phase RELocked=PASS ACKNOWLEDGED=PASS"
    }
    $lockType.GetMethod('Dispose', $flags).Invoke($guard, [object[]]@()) | Out-Null
    $guard = $null
    if ($model.GetModelIsLocked()) { throw 'Closing MESHing did not restore the original unlocked state.' }
    if ((Get-FileHash -LiteralPath $InputModel -Algorithm SHA256).Hash -ne $originalHash -or
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'The lock test changed a saved model.'
    }
    'MODEL_LOCK_LIFECYCLE=PASS ATTEMPTS=7 ORIGINAL_FILES_UNCHANGED=YES'
} finally {
    if ($guard) { try { $lockType.GetMethod('Dispose', $flags).Invoke($guard, [object[]]@()) | Out-Null } catch { } }
    if ($sap) {
        try {
            $exit = [CSiAPIv1.cOAPI].GetMethod('ApplicationExit', [Type[]]@([bool]))
            [void]$exit.Invoke($sap.PSObject.BaseObject, [object[]]@($false))
        } catch { }
    }
    if ($ownedPid) {
        Start-Sleep -Milliseconds 700
        $owned = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($owned -and $owned.ProcessName -eq 'SAP2000') { Stop-Process -Id $ownedPid -ErrorAction SilentlyContinue }
    }
}
