# The preview may be undone; Apply no longer keeps an Undo snapshot.
param([Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [string[]]$Ids = @('nTEST001','nTEST002','nTEST010','nTEST012','nTEST033','nTEST035'),
      [ValidateRange(30, 240)][int]$TimeoutSeconds = 90,
      [string]$InputModelDirectory = '', [string]$OutputSuffix = '',
      [string]$ExpectedSapVersion = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path $env:TEMP 'opencode'
$runner = Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'
$modelDirectory = if ($InputModelDirectory) { (Resolve-Path -LiteralPath $InputModelDirectory).Path }
                  else { Join-Path $root 'examples\atomic\models' }
if (-not (Test-Path -LiteralPath $temp -PathType Container) -or
    -not $ResultsRoot.StartsWith(($temp.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $ResultsRoot) -or
    -not (Test-Path -LiteralPath $Plugin -PathType Leaf) -or
    -not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw 'Unsafe preview-Undo regression input.' }
New-Item -ItemType Directory -Path $ResultsRoot | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $root 'examples\atomic\manifest.json') -Raw | ConvertFrom-Json
$previous = @($env:SAP_GMSH_V2_ATOMIC_EXAMPLES, $env:SAP_QUAD_ATOMIC_UNDO,
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO, $env:SAP_GMSH_V2_SETTINGS_PATH)
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$owned = $false
try {
    [RunTest01Fast]::StartIsolatedPersistentSap()
    $owned = $true
    $sapField = [RunTest01Fast].GetField('persistentSap',[Reflection.BindingFlags]'Static,NonPublic')
    $sapVersion=''; $sapVersionNumber=0.0
    $code=$sapField.GetValue($null).SapModel.GetVersion([ref]$sapVersion,[ref]$sapVersionNumber)
    if ($code -ne 0 -or ($ExpectedSapVersion -and -not $sapVersion.StartsWith($ExpectedSapVersion))) {
        throw "Unexpected SAP version: code=$code version=$sapVersion required=$ExpectedSapVersion"
    }
    [IO.File]::WriteAllText((Join-Path $ResultsRoot 'sap-version.txt'),$sapVersion)
    $pidField=[RunTest01Fast].GetField('persistentSapPid',[Reflection.BindingFlags]'Static,NonPublic')
    [IO.File]::WriteAllText((Join-Path $ResultsRoot 'sap.pid'),[string]$pidField.GetValue($null))
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = '1'
    $env:SAP_QUAD_ATOMIC_UNDO = '1'
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO = '0'
    foreach ($id in $Ids) {
        $entry = @($manifest | Where-Object id -eq $id)[0]
        if (-not $entry) { throw "Unknown atomic case: $id" }
        $folder = Join-Path $ResultsRoot $id
        New-Item -ItemType Directory -Path $folder | Out-Null
        $env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $folder 'isolated.json'
        $points = if ($entry.points) { [string[]]$entry.points } else { [string[]]@() }
        $frames = if ($entry.frames) { [string[]]$entry.frames } else { [string[]]@() }
        $size = ([double]$entry.maxSize).ToString([Globalization.CultureInfo]::InvariantCulture) + 'm'
        $source=Join-Path $modelDirectory "$id.sdb"
        $output=Join-Path $folder ($id + $(if ($OutputSuffix) { "-$OutputSuffix" } else { '' }) + '-restored.sdb')
        $sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        [RunTest01Fast]::RunAtomicExampleWithTimeout(
            $source, $output, (Resolve-Path -LiteralPath $Plugin).Path,
            [string[]]$entry.areas, $points, $frames, $size, '', '0.05m', '',
            $false, $true, 'General Purpose', [string]$entry.view, '', $TimeoutSeconds)
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) {
            throw "Input was saved during beta28 Preview Undo: $source"
        }
        if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
            throw "Preview Undo did not produce a restored model: $id"
        }
        "PREVIEW_UNDO=$id PASS"
    }
    "PREVIEW_UNDO_REGRESSION=PASS COUNT=$($Ids.Count)"
} finally {
    if ($owned) { [RunTest01Fast]::StopIsolatedPersistentSap() }
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES = $previous[0]
    $env:SAP_QUAD_ATOMIC_UNDO = $previous[1]
    $env:SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO = $previous[2]
    $env:SAP_GMSH_V2_SETTINGS_PATH = $previous[3]
}
