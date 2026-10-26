# Reproduce one user group selection on a disposable model copy; do not save the source.
param([Parameter(Mandatory=$true)][string]$Source,
      [Parameter(Mandatory=$true)][string]$Candidate,
      [Parameter(Mandatory=$true)][string]$Trial,
      [string]$Group = 'Group1', [string]$Size = '600m', [switch]$Standard)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $Trial.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or
    -not (Test-Path -LiteralPath $Source -PathType Leaf) -or
    -not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or
    -not (Test-Path -LiteralPath $runner -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $installed 'mesher\SapQuadMesherBackend.exe') -PathType Leaf)) {
    throw 'Unsafe group test inputs or missing isolated runtime.'
}
$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $Trial | Out-Null
$modelCopy = Join-Path $Trial 'test01-group-copy.sdb'
Copy-Item -LiteralPath $Source -Destination $modelCopy
$package = Join-Path $Trial 'plugin'
Copy-Item -LiteralPath $installed -Destination $package -Recurse
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $package 'SapQuadTransitionPlugin.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py') -Destination $package -Force
& (Join-Path $package 'SQMPluginInstaller.exe') verify $package
if ($LASTEXITCODE -ne 0) { throw 'Isolated candidate verification failed.' }
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\SAP2000v1.dll'
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$old = @{}
foreach ($name in @('SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_GMSH_V2_ATOMIC_UI_PREVIEW',
    'SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT','SAP_QUAD_BOUNDARY_TRACE_DIR',
    'SAP_QUAD_MESH_TRACE_DIR','SAP_QUAD_CANCEL_TRACE_PATH','SAP_GMSH_V2_SETTINGS_PATH')) {
    $old[$name] = [Environment]::GetEnvironmentVariable($name)
}
$started = $false
try {
    [RunTest01Fast]::StartIsolatedPersistentSap()
    $started = $true
    $field = [RunTest01Fast].GetField('persistentSap', [Reflection.BindingFlags]'Static,NonPublic')
    $sap = $field.GetValue($null)
    if (-not $sap) { throw 'Isolated SAP2000 object was not retained.' }
    $model = $sap.SapModel
    $open = $model.File.OpenFile($modelCopy)
    if ($open -ne 0) { throw "Cannot open disposable model copy (SAP code $open)." }
    $count = 0; $types = $null; $names = $null
    $code = $model.GroupDef.GetAssignments($Group, [ref]$count, [ref]$types, [ref]$names)
    if ($code -ne 0 -or $count -le 0) { throw "Group '$Group' is missing or empty (code $code)." }
    $areas = @(); $points = @(); $frames = @(); $other = @()
    for ($index=0; $index -lt $count; $index++) {
        switch ([int]$types[$index]) {
            5 { $areas += [string]$names[$index] }
            1 { $points += [string]$names[$index] }
            2 { $frames += [string]$names[$index] }
            default { $other += "$($types[$index]):$($names[$index])" }
        }
    }
    $areas = @($areas | Select-Object -Unique)
    $points = @($points | Select-Object -Unique)
    $frames = @($frames | Select-Object -Unique)
    if (-not $areas.Count) { throw "Group '$Group' contains no selected area objects." }
    [pscustomobject]@{ group=$Group; size=$Size; standard=[bool]$Standard;
        areas=$areas; points=$points;
        frames=$frames; other=$other } | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $Trial 'group-selection.json') -Encoding UTF8
    "GROUP_SELECTION=$Group AREAS=$($areas -join ',') POINTS=$($points -join ',') FRAMES=$($frames -join ',') OTHER=$($other -join ',')"
    $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR = $Trial
    $env:SAP_GMSH_V2_ATOMIC_UI_PREVIEW = '1'
    $env:SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT = Join-Path $Trial 'options.png'
    $env:SAP_QUAD_BOUNDARY_TRACE_DIR = $Trial
    $env:SAP_QUAD_MESH_TRACE_DIR = $Trial
    $env:SAP_QUAD_CANCEL_TRACE_PATH = Join-Path $Trial 'stages.txt'
    $env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $Trial 'isolated-options.json'
    try {
        [RunTest01Fast]::RunAtomicExampleWithTimeout($modelCopy,
            (Join-Path $Trial 'test01-meshed.sdb'), (Join-Path $package 'SapQuadTransitionPlugin.dll'),
            [string[]]$areas, [string[]]$points, [string[]]$frames, $Size,
            '', '0.05m', '', $false, (-not [bool]$Standard), 'General Purpose', 'xy',
            (Join-Path $Trial 'result.svg'), 180)
        'GROUP_TRIAL=COMPLETED_NO_ERROR'
    } catch {
        $failure = $_.Exception.ToString()
        [IO.File]::WriteAllText((Join-Path $Trial 'failure.txt'), $failure)
        try {
            $currentAreaCount = 0; $currentAreas = $null
            if ($model.AreaObj.GetNameList([ref]$currentAreaCount, [ref]$currentAreas) -eq 0) {
                $meshAreas = @($currentAreas | Where-Object { $_ -eq '845' -or $_ -like 'QTA*' })
                $namesInMesh = [Collections.Generic.List[string]]::new()
                foreach ($area in $meshAreas) {
                    $jointCount = 0; $joints = $null
                    if ($model.AreaObj.GetPoints($area, [ref]$jointCount, [ref]$joints) -eq 0) {
                        foreach ($joint in $joints) { $namesInMesh.Add($joint) }
                    }
                }
                [pscustomobject]@{ currentAreas=$currentAreaCount; generatedShells=$meshAreas.Count;
                    node878Count=@($namesInMesh | Where-Object { $_ -eq '878' }).Count;
                    node109Count=@($namesInMesh | Where-Object { $_ -eq '109' }).Count;
                    generatedJointNames=@($namesInMesh | Select-Object -Unique) } | ConvertTo-Json -Depth 3 |
                    Set-Content -LiteralPath (Join-Path $Trial 'postmesh-connectivity.json') -Encoding UTF8
                $null = $model.File.Save((Join-Path $Trial 'postmesh-copy.sdb'))
            }
        } catch { 'POSTMESH_INSPECTION=' + $_.Exception.Message }
        'GROUP_TRIAL=FAILED ERROR=' + $failure.Split([Environment]::NewLine)[0]
    }
} finally {
    if ($started) { [RunTest01Fast]::StopIsolatedPersistentSap() }
    foreach ($name in $old.Keys) { [Environment]::SetEnvironmentVariable($name, $old[$name]) }
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $sourceHash)
    { throw 'Original user model changed during isolated reproduction.' }
    'USER_SOURCE_UNCHANGED=PASS'
}
