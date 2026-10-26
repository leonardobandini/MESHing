# Validate X/Close rollback against a disposable SAP2000 model and plugin bundle.
param([Parameter(Mandatory=$true)][string]$Candidate,
       [Parameter(Mandatory=$true)][string]$Trial,
       [string]$Source = '',
       [switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
$source = if ($Source) { $Source } else { Join-Path $root 'examples\atomic\models\nTEST001.sdb' }
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $Trial.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or
    -not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or
    -not (Test-Path -LiteralPath $source -PathType Leaf) -or
    -not (Test-Path -LiteralPath $runner -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $installed 'SQMPluginInstaller.exe') -PathType Leaf)) {
    throw 'Unsafe trial folder or missing test inputs.'
}
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $Trial | Out-Null
$model = Join-Path $Trial 'nTEST001-copy.sdb'
Copy-Item -LiteralPath $source -Destination $model
$package = Join-Path $Trial 'plugin'
Copy-Item -LiteralPath $installed -Destination $package -Recurse
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $package 'SapQuadTransitionPlugin.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py') -Destination $package -Force
& (Join-Path $package 'SQMPluginInstaller.exe') verify $package
if ($LASTEXITCODE -ne 0) { throw 'Disposable candidate verification failed.' }
$oldClose = $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED
$oldCloseApply = $env:SAP_QUAD_ATOMIC_CLOSE_APPLY
$oldAtomic = $env:SAP_GMSH_V2_ATOMIC_EXAMPLES
$oldUndoTrace = $env:SAP_QUAD_UNDO_TRACE
$oldSettings = $env:SAP_GMSH_V2_SETTINGS_PATH
$env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED = if ($Apply) { $null } else { '1' }
$env:SAP_QUAD_ATOMIC_CLOSE_APPLY = if ($Apply) { '1' } else { $null }
$env:SAP_GMSH_V2_ATOMIC_EXAMPLES = '1'
$env:SAP_QUAD_UNDO_TRACE = Join-Path $Trial 'undo-method.txt'
$env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $Trial 'options.json'
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$started = $false
try {
    [RunTest01Fast]::StartIsolatedPersistentSap()
    $started = $true
    [RunTest01Fast]::RunAtomicExampleWithTimeout(
        $model, (Join-Path $Trial 'nTEST001-restored.sdb'),
        (Join-Path $package 'SapQuadTransitionPlugin.dll'),
        [string[]]@('A1'), [string[]]@(), [string[]]@(), '0.2m', '', '0.05m', '',
        $false, $true, 'General Purpose', 'xy', '', 120)
} finally {
    if ($started) { [RunTest01Fast]::StopIsolatedPersistentSap() }
    $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED = $oldClose
    $env:SAP_QUAD_ATOMIC_CLOSE_APPLY = $oldCloseApply
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = $oldAtomic
    $env:SAP_QUAD_UNDO_TRACE = $oldUndoTrace
    $env:SAP_GMSH_V2_SETTINGS_PATH = $oldSettings
}
if ((-not $Apply -and -not (Test-Path -LiteralPath (Join-Path $Trial 'undo-method.txt') -PathType Leaf)) -or
    ($Apply -and (Test-Path -LiteralPath (Join-Path $Trial 'undo-method.txt') -PathType Leaf)) -or
    -not (Test-Path -LiteralPath (Join-Path $Trial 'nTEST001-restored.sdb') -PathType Leaf)) {
    throw 'Close did not execute the chosen Apply/Undo operation.'
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'The original atomic SAP model changed.'
}
"CLOSE_$($(if ($Apply) { 'APPLY' } else { 'UNDO' }))_SAP=PASS MODEL=nTEST001 SOURCE_UNCHANGED=PASS"
