# SPDX-License-Identifier: GPL-2.0-or-later
# Regenerate help examples with the V2 backend; never reuse V1 result SVGs.
param(
    [string]$Ids = 'nTEST001,nTEST003,nTEST010,nTEST011,nTEST012,nTEST014',
    [Parameter(Mandatory=$true)][string]$PluginPath,
    [switch]$PreserveSelectedEdges,
    [switch]$OGrid,
    [switch]$RegularGrid,
    [switch]$AllowSymmetry,
    [switch]$DisableSymmetry,
    [switch]$ForceSymmetry,
    [int]$SymmetryAxisIndex = -1
)
$ErrorActionPreference = 'Stop'
if (($AllowSymmetry -or $ForceSymmetry) -and $DisableSymmetry -or $SymmetryAxisIndex -lt -1) {
    throw 'Choose one symmetry mode and an axis index >= 0 when supplied.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$temporary = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $temporary -PathType Container)) { throw "Temporary parent missing: $temporary" }
$run = Join-Path $temporary ('gmsh-v2-examples-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$manifest = Get-Content (Join-Path $root 'examples\atomic\manifest.json') -Raw | ConvertFrom-Json
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
$previousRegularGrid = $env:SAP_GMSH_V2_ATOMIC_REGULAR_GRID
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw 'Build RunTest01Fast.csproj -c Release before generating V2 examples.'
}
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
[RunTest01Fast]::StartIsolatedPersistentSap()
try {
    foreach ($id in $Ids.Split(',')) {
        $entry = @($manifest | Where-Object id -eq $id)[0]
        if ($null -eq $entry) { throw "Unknown atomic case: $id" }
        if ($OGrid -and -not $entry.circularGroup) {
            throw "Case $id has no circular-hole group; run it without -OGrid."
        }
        if ($entry.symmetry -and -not $AllowSymmetry -and -not $DisableSymmetry) {
            throw "Case $id requires -AllowSymmetry or -DisableSymmetry for a controlled V2 comparison."
        }
        $folder = Join-Path $run $id
        New-Item -ItemType Directory -Path $folder | Out-Null
        $points = if ($entry.points) { [string[]]$entry.points } else { [string[]]@() }
        $frames = if ($entry.frames) { [string[]]$entry.frames } else { [string[]]@() }
        $size = ([double]$entry.maxSize).ToString([Globalization.CultureInfo]::InvariantCulture)
        $diameter = if ($entry.circularGroup) {
            ([double]$entry.diameter).ToString([Globalization.CultureInfo]::InvariantCulture)
        } else { '0' }
        $fast = if ($null -ne $entry.fast) { [bool]$entry.fast } else { $true }
        $useSymmetry = if ($ForceSymmetry) { $true } else { [bool]($entry.symmetry -and $AllowSymmetry) }
        "V2_EXAMPLE_OPTIONS=$id OGrid=$([bool]$OGrid) ExactSymmetry=$useSymmetry FAST=$fast"
        $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = '1'
        if ($PreserveSelectedEdges) { $env:SAP_GMSH_V2_ATOMIC_PRESERVE_EDGES = '1' }
        if ($OGrid) { $env:SAP_GMSH_V2_ATOMIC_OGRID = '1' }
        $env:SAP_GMSH_V2_ATOMIC_REGULAR_GRID = if ($RegularGrid) { '1' } else { '0' }
        if ($SymmetryAxisIndex -ge 0) { $env:SAP_GMSH_V2_ATOMIC_SYMMETRY_INDEX = [string]$SymmetryAxisIndex }
        $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR = $folder
        $env:SAP_QUAD_COMPLETION_TRACE = Join-Path $folder 'completed.txt'
        try {
            [RunTest01Fast]::RunAtomicExampleWithTimeout(
                (Join-Path $root "examples\atomic\models\$id.sdb"),
                (Join-Path $folder "$id-meshed.sdb"), $plugin,
                [string[]]$entry.areas, $points, $frames, $size, [string]$entry.circularGroup,
                $diameter, [string]$entry.openingGroup,
                $useSymmetry,
                $fast, [string]$entry.profile, [string]$entry.view,
                (Join-Path $folder "$id-result.svg"), 240)
            if (-not (Test-Path -LiteralPath (Join-Path $folder "$id-source.svg")) -or
                -not (Test-Path -LiteralPath (Join-Path $folder "$id-result.svg")) -or
                -not (Test-Path -LiteralPath (Join-Path $folder 'completed.txt'))) {
                throw "Case $id lacks source/result diagram or actual Gmsh settings."
            }
            "V2_EXAMPLE=$id PASS FOLDER=$folder"
        } finally {
            $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = $null
            $env:SAP_GMSH_V2_ATOMIC_PRESERVE_EDGES = $null
            $env:SAP_GMSH_V2_ATOMIC_OGRID = $null
            $env:SAP_GMSH_V2_ATOMIC_REGULAR_GRID = $previousRegularGrid
            $env:SAP_GMSH_V2_ATOMIC_SYMMETRY_INDEX = $null
            $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR = $null
            $env:SAP_QUAD_COMPLETION_TRACE = $null
        }
    }
    "V2_EXAMPLE_RUN=$run"
} finally { [RunTest01Fast]::StopIsolatedPersistentSap() }
