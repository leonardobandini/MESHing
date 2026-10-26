# Run one disjoint portion of the V2 atomic SAP catalog in an owned SAP process.
param([Parameter(Mandatory=$true)][string]$Ids,
      [Parameter(Mandatory=$true)][string]$PluginPath,
       [Parameter(Mandatory=$true)][string]$RunDirectory,
        [int]$CaseTimeoutSeconds = 480,
        [int]$SapPid = 0,
       [switch]$StandardCircularHoles,
        [string]$InputModelDirectory = '', [string]$ManifestPath = '', [string]$OutputSuffix = '',
       [string]$ExpectedSapVersion = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $RunDirectory.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $RunDirectory) -or
    -not (Test-Path -LiteralPath $PluginPath -PathType Leaf)) { throw 'Unsafe or existing regression destination/plugin.' }
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw 'Build RunTest01Fast.csproj first.' }
$modelDirectory = if ($InputModelDirectory) { (Resolve-Path -LiteralPath $InputModelDirectory).Path }
                  else { Join-Path $root 'examples\atomic\models' }
$manifestFile = if ($ManifestPath) { (Resolve-Path -LiteralPath $ManifestPath).Path }
                else { Join-Path $root 'examples\atomic\manifest.json' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $RunDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()
$old = @{}
foreach ($key in @('SAP_GMSH_V2_ATOMIC_EXAMPLES','SAP_GMSH_V2_ATOMIC_OGRID',
    'SAP_GMSH_V2_ATOMIC_UI_PREVIEW','SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT',
    'SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_QUAD_COMPLETION_TRACE',
    'SAP_GMSH_V2_ATOMIC_UI_PAUSE_MS','SAP_GMSH_V2_SETTINGS_PATH')) {
    $old[$key] = [Environment]::GetEnvironmentVariable($key)
}
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$started = $false
try {
    if ($SapPid) {
        $process = Get-Process -Id $SapPid -ErrorAction Stop
        if ($process.ProcessName -ne 'SAP2000') { throw 'Attached worker requires SAP2000.' }
        [RunTest01Fast]::AttachPersistentSap($SapPid)
    } else { [RunTest01Fast]::StartIsolatedPersistentSap() }
    $started = $true
    $pidField = [RunTest01Fast].GetField('persistentSapPid',[Reflection.BindingFlags]'Static,NonPublic')
    [IO.File]::WriteAllText((Join-Path $RunDirectory 'sap.pid'), [string]$pidField.GetValue($null))
    $sapField = [RunTest01Fast].GetField('persistentSap', [Reflection.BindingFlags]'Static,NonPublic')
    $sap = $sapField.GetValue($null)
    $sapVersion = ''; $sapVersionNumber = 0.0
    $versionCode = $sap.SapModel.GetVersion([ref]$sapVersion,[ref]$sapVersionNumber)
    if ($versionCode -ne 0 -or ($ExpectedSapVersion -and -not $sapVersion.StartsWith($ExpectedSapVersion))) {
        throw "Unexpected SAP version: code=$versionCode version=$sapVersion required=$ExpectedSapVersion"
    }
    [IO.File]::WriteAllText((Join-Path $RunDirectory 'sap-version.txt'), $sapVersion)
    foreach ($id in $Ids.Split(',')) {
        $entry = @($manifest | Where-Object id -eq $id)[0]
        if (-not $entry) { throw "Unknown atomic SAP fixture: $id" }
        $folder = Join-Path $RunDirectory $id
        New-Item -ItemType Directory -Path $folder | Out-Null
        $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = '1'
        $env:SAP_GMSH_V2_ATOMIC_UI_PREVIEW = '1'
        $env:SAP_GMSH_V2_ATOMIC_OGRID = if ($entry.circularGroup -and -not $StandardCircularHoles) { '1' } else { $null }
        $env:SAP_GMSH_V2_ATOMIC_UI_PAUSE_MS = '100'
        $env:SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT = Join-Path $folder 'new-ui.png'
        $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR = $folder
        $env:SAP_QUAD_COMPLETION_TRACE = Join-Path $folder 'completed.txt'
        $env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $folder 'isolated-settings.json'
        $points = if ($entry.points) { [string[]]$entry.points } else { [string[]]@() }
        $frames = if ($entry.frames) { [string[]]$entry.frames } else { [string[]]@() }
        $size = ([double]$entry.maxSize).ToString([Globalization.CultureInfo]::InvariantCulture) + 'm'
        $diameter = if ($entry.diameter) { ([double]$entry.diameter).ToString([Globalization.CultureInfo]::InvariantCulture) + 'm' } else { '0.05m' }
        $fast = if ($null -eq $entry.fast) { $true } else { [bool]$entry.fast }
        $source = Join-Path $modelDirectory "$id.sdb"
        $outputModel = Join-Path $folder ($id + $(if ($OutputSuffix) { "-$OutputSuffix" } else { '' }) + '-meshed.sdb')
        $inputHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $failure = ''
        try {
            [RunTest01Fast]::RunAtomicExampleWithTimeout(
                $source, $outputModel, $PluginPath,
                [string[]]$entry.areas, $points, $frames, $size,
                [string]$entry.circularGroup, $diameter, [string]$entry.openingGroup,
                [bool]$entry.symmetry, $fast, [string]$entry.profile, [string]$entry.view,
                (Join-Path $folder "$id-result.svg"), $CaseTimeoutSeconds)
        }
        catch { $failure = $_.Exception.ToString() }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $inputHash) {
            throw "Input model was changed by SAP: $source"
        }
        if ($failure) { [IO.File]::WriteAllText((Join-Path $folder 'failure.txt'), $failure) }
        $traces = @(Get-ChildItem -LiteralPath $folder -Filter '*-settings.json' -File)
        $results.Add([pscustomobject]@{
            id = $id; status = if ($failure) { 'FAIL' } else { 'PASS' }
            backendJson = $traces.Count; screenshot = Test-Path -LiteralPath (Join-Path $folder 'new-ui.png')
            model = Test-Path -LiteralPath $outputModel
            failure = if ($failure) { $failure.Split([Environment]::NewLine)[0] } else { '' }
        })
        $results | Export-Csv -LiteralPath (Join-Path $RunDirectory 'summary.csv') -NoTypeInformation -Encoding UTF8
        Write-Host ("ATOMIC=$id STATUS=" + $(if ($failure) { 'FAIL' } else { 'PASS' }) + " JSON=$($traces.Count)")
        if ($failure -match 'exceeded the timeout') { break }
    }
}
finally {
    foreach ($key in $old.Keys) { [Environment]::SetEnvironmentVariable($key, $old[$key]) }
    if ($started) {
        if ($SapPid) { [RunTest01Fast]::DetachPersistentSap() }
        else { [RunTest01Fast]::StopIsolatedPersistentSap() }
    }
}
"REGRESSION_DIR=$RunDirectory"
"TOTAL=$($results.Count) PASS=$(@($results | Where-Object status -eq 'PASS').Count)"
