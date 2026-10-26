# SPDX-License-Identifier: GPL-2.0-or-later
# Isolated SAP atomic regression using a disposable staged V2 package.
param([Parameter(Mandatory=$true)][string]$Id,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [switch]$Undo,[switch]$ApplyBeforeUndo,[switch]$ParentFixture,[switch]$DesignFixture,
      [string]$DesignValue='Concrete Shell Design',[switch]$EdgeFixture,[switch]$Backup,
      [switch]$DesignWarningProbe,[switch]$Fast)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temp=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'opencode')) + [IO.Path]::DirectorySeparatorChar
$pluginFull=[IO.Path]::GetFullPath($Plugin)
$resultsFull=[IO.Path]::GetFullPath($ResultsRoot)
if(-not $pluginFull.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
   -not $resultsFull.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
   -not (Test-Path -LiteralPath $pluginFull -PathType Leaf) -or
   -not (Test-Path -LiteralPath (Split-Path -Parent $resultsFull) -PathType Container)) {
    throw 'Atomic trial needs a staged DLL and a results directory under TEMP\opencode.'
}
$previous=@($env:SAP_GMSH_V2_ATOMIC_EXAMPLES,$env:SAP_QUAD_ATOMIC_UNDO,
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO,$env:SAP_GMSH_V2_SETTINGS_PATH,
    $env:SAP_QUAD_ATOMIC_PARENT_FIXTURE,$env:SAP_QUAD_ATOMIC_DESIGN_FIXTURE,
    $env:SAP_QUAD_ATOMIC_DESIGN_VALUE,$env:SAP_QUAD_ATOMIC_EDGE_FIXTURE,
    $env:SAP_QUAD_ATOMIC_BACKUP,$env:SAP_QUAD_ATOMIC_DESIGN_WARNING_PROBE,
    $env:SAP_QUAD_ASSIGNMENT_WARNING_TRACE,$env:SAP_QUAD_ATOMIC_FORCE_FAST)
try {
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES='1'
    $env:SAP_QUAD_ATOMIC_UNDO=if($Undo){'1'}else{'0'}
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO=if($ApplyBeforeUndo){'1'}else{'0'}
    $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path (Split-Path -Parent $pluginFull) 'atomic-isolated-options.json'
    $env:SAP_QUAD_ATOMIC_PARENT_FIXTURE=if($ParentFixture){'1'}else{'0'}
    $env:SAP_QUAD_ATOMIC_DESIGN_FIXTURE=if($DesignFixture){'1'}else{'0'}
    $env:SAP_QUAD_ATOMIC_DESIGN_VALUE=$DesignValue
    $env:SAP_QUAD_ATOMIC_EDGE_FIXTURE=if($EdgeFixture){'1'}else{'0'}
    $env:SAP_QUAD_ATOMIC_BACKUP=if($Backup){'1'}else{'0'}
    $env:SAP_QUAD_ATOMIC_DESIGN_WARNING_PROBE=if($DesignWarningProbe){'1'}else{'0'}
    $env:SAP_QUAD_ASSIGNMENT_WARNING_TRACE=if($DesignWarningProbe){Join-Path $resultsFull 'design-warning.txt'}else{$null}
    $env:SAP_QUAD_ATOMIC_FORCE_FAST=if($Fast){'1'}else{'0'}
    & (Join-Path $root 'examples\atomic\run-example.ps1') -Id $Id -RepositoryRoot $root `
        -ResultsRoot $resultsFull -PluginPath $pluginFull
    if($LASTEXITCODE -ne 0) { throw "Atomic $Id failed (exit $LASTEXITCODE)." }
    if($DesignWarningProbe) {
        $text=Get-Content -LiteralPath (Join-Path $resultsFull 'design-warning.txt') -Raw -ErrorAction Stop
        if($text -notmatch 'Overwrites - Concrete Shell Design' -or
           ($Backup -and $text -notmatch 'quadmesher-backup-') -or
           (-not $Backup -and $text -notmatch 'OFF|DISATTIVATA')) {
            throw 'Design warning does not match the selected backup state.'
        }
        "XAPI_DESIGN_WARNING_PASS=$Backup"
    }
    "XAPI_ATOMIC_PASS=$Id UNDO=$Undo APPLY_BEFORE_UNDO=$ApplyBeforeUndo"
} finally {
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES=$previous[0]
    $env:SAP_QUAD_ATOMIC_UNDO=$previous[1]
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO=$previous[2]
    $env:SAP_GMSH_V2_SETTINGS_PATH=$previous[3]
    $env:SAP_QUAD_ATOMIC_PARENT_FIXTURE=$previous[4]
    $env:SAP_QUAD_ATOMIC_DESIGN_FIXTURE=$previous[5]
    $env:SAP_QUAD_ATOMIC_DESIGN_VALUE=$previous[6]
    $env:SAP_QUAD_ATOMIC_EDGE_FIXTURE=$previous[7]
    $env:SAP_QUAD_ATOMIC_BACKUP=$previous[8]
    $env:SAP_QUAD_ATOMIC_DESIGN_WARNING_PROBE=$previous[9]
    $env:SAP_QUAD_ASSIGNMENT_WARNING_TRACE=$previous[10]
    $env:SAP_QUAD_ATOMIC_FORCE_FAST=$previous[11]
}
