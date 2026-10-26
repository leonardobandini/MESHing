# Validate the visible V2 selected-edge switch on an isolated SAP test copy.
param([Parameter(Mandatory=$true)][string]$Candidate,
      [Parameter(Mandatory=$true)][string]$Trial)
$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP 'opencode'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'examples\atomic\models\nTEST010.sdb'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
if (-not (Test-Path -LiteralPath $temp -PathType Container) -or
    -not $Trial.StartsWith(($temp.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or -not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or
    -not (Test-Path -LiteralPath $runner -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $installed 'mesher\SapQuadMesherBackend.exe'))) {
    throw 'Unsafe trial folder or required Gmsh/runner files missing.'
}
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $Trial | Out-Null
$copy = Join-Path $Trial 'nTEST010-copy.sdb'
Copy-Item -LiteralPath $source -Destination $copy
$package = Join-Path $Trial 'plugin'
Copy-Item -LiteralPath $installed -Destination $package -Recurse
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $package 'SapQuadTransitionPlugin.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py') -Destination $package -Force
$envNames = @('SAP_GMSH_V2_ATOMIC_EXAMPLES','SAP_GMSH_V2_ATOMIC_PRESERVE_EDGES',
    'SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_QUAD_BOUNDARY_TRACE_DIR','SAP_QUAD_MESH_TRACE_DIR',
    'SAP_QUAD_COMPLETION_TRACE','SAP_GMSH_V2_SETTINGS_PATH')
$old = @{}
foreach ($name in $envNames) { $old[$name] = [Environment]::GetEnvironmentVariable($name) }
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$started = $false
try {
    [RunTest01Fast]::StartIsolatedPersistentSap()
    $started = $true
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = '1'
    $env:SAP_GMSH_V2_ATOMIC_PRESERVE_EDGES = '1'
    $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR = $Trial
    $env:SAP_QUAD_BOUNDARY_TRACE_DIR = $Trial
    $env:SAP_QUAD_MESH_TRACE_DIR = $Trial
    $env:SAP_QUAD_COMPLETION_TRACE = Join-Path $Trial 'completed.txt'
    $env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $Trial 'isolated-options.json'
    $manifest = Get-Content -LiteralPath (Join-Path $root 'examples\atomic\manifest.json') -Raw | ConvertFrom-Json
    $entry = @($manifest | Where-Object id -eq 'nTEST010')[0]
    if ($null -eq $entry -or @($entry.areas).Count -ne 1 -or @($entry.points).Count -ne 7) {
        throw 'The nTEST010 atomic selection manifest is incomplete.'
    }
    "PROTECTED_INPUT_AREAS=$($entry.areas -join ',') POINTS=$($entry.points -join ',')"
    [RunTest01Fast]::RunAtomicExampleWithTimeout(
        $copy, (Join-Path $Trial 'nTEST010-protected-result.sdb'),
        (Join-Path $package 'SapQuadTransitionPlugin.dll'), [string[]]$entry.areas,
        [string[]]$entry.points, [string[]]@(), '0.2m', '', '0.05m', '',
        $false, $true, 'General Purpose', 'xy',
        (Join-Path $Trial 'nTEST010-result.svg'), 180)
    $settings = @(Get-ChildItem -LiteralPath $Trial -Filter '*-settings.json' -File)
    if ($settings.Count -ne 1 -or -not (Get-Content -LiteralPath $settings[0].FullName -Raw |
        ConvertFrom-Json).preserveSelectedEdges -or
        -not (Select-String -LiteralPath (Join-Path $Trial 'area-A1-boundary.csv') -Pattern '^PRESERVE_SELECTED_EDGE,' -Quiet) -or
        -not (Test-Path -LiteralPath (Join-Path $Trial 'completed.txt') -PathType Leaf)) {
        throw 'Selected-edge preference was not passed through the SAP boundary and Gmsh settings.'
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash) {
        throw 'Original atomic SAP model was modified.'
    }
    "SELECTED_EDGES_SAP=PASS MODEL=nTEST010 FOLDER=$Trial"
} finally {
    if ($started) { [RunTest01Fast]::StopIsolatedPersistentSap() }
    foreach ($name in $old.Keys) { [Environment]::SetEnvironmentVariable($name, $old[$name]) }
}
