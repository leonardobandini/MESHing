# Verify local numeric joint names on a disposable copy of test01.sdb.
param([Parameter(Mandatory=$true)][string]$Source,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [switch]$Undo)
$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP 'opencode'
$runner = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Release\net48\RunTest01Fast.dll'
if (-not (Test-Path -LiteralPath $temp -PathType Container) -or
    -not $ResultsRoot.StartsWith(($temp.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $ResultsRoot) -or
    -not (Test-Path -LiteralPath $Source -PathType Leaf) -or
    -not (Test-Path -LiteralPath $Plugin -PathType Leaf) -or
    -not (Test-Path -LiteralPath $runner -PathType Leaf) -or
    @(Get-Process SAP2000 -ErrorAction SilentlyContinue).Count) {
    throw 'Numeric test requires a new temp directory, a saved source and no open SAP2000.'
}
$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $ResultsRoot | Out-Null
$input = Join-Path $ResultsRoot 'test01-input.sdb'
$output = Join-Path $ResultsRoot $(if ($Undo) { 'test01-restored.sdb' } else { 'test01-meshed.sdb' })
Copy-Item -LiteralPath $Source -Destination $input -ErrorAction Stop
if ((Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Input copy differs.' }
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\SAP2000v1.dll'
[Reflection.Assembly]::LoadFrom($runner) | Out-Null
$old = @{}
foreach ($key in @('SAP_GMSH_V2_ATOMIC_REGULAR_GRID','SAP_GMSH_V2_ATOMIC_EXAMPLES',
    'SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_GMSH_V2_SETTINGS_PATH','SAP_QUAD_ATOMIC_REGULAR',
    'SAP_QUAD_ATOMIC_UNDO','SAP_QUAD_ATOMIC_BACKUP','SAP_QUAD_ATOMIC_WORKERS')) {
    $old[$key] = [Environment]::GetEnvironmentVariable($key)
}
$started = $false
try {
    [RunTest01Fast]::StartIsolatedPersistentSap()
    $started = $true
    $sap = [RunTest01Fast].GetField('persistentSap', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $model = $sap.SapModel
    if ($model.File.OpenFile($input) -ne 0) { throw 'Cannot open the copied test01.' }
    $groupCount=0; $types=$null; $names=$null
    $code=$model.GroupDef.GetAssignments('Group1',[ref]$groupCount,[ref]$types,[ref]$names)
    if ($code -ne 0 -or $groupCount -lt 1) {
        $areaCount=0; $areaNames=$null
        $model.AreaObj.GetNameList([ref]$areaCount,[ref]$areaNames) | Out-Null
        $selectionCount=0; $selectedTypes=$null; $selectedNames=$null
        $model.SelectObj.GetSelected([ref]$selectionCount,[ref]$selectedTypes,[ref]$selectedNames) | Out-Null
        "TEST01_INVENTORY_AREAS=$areaCount NAMES=$($areaNames -join ',') SAVED_SELECTION=$selectionCount"
        if ($areaCount -lt 1 -or $areaCount -gt 20) { throw 'Select a limited set of test01 areas for the trial.' }
        $groupCount=$areaCount; $types=@(1..$areaCount | ForEach-Object { 5 }); $names=$areaNames
    }
    $areas = @(); $points = @(); $frames = @()
    for ($i=0; $i -lt $groupCount; $i++) {
        switch ([int]$types[$i]) {
            5 { $areas += [string]$names[$i] }
            1 { $points += [string]$names[$i] }
            2 { $frames += [string]$names[$i] }
        }
    }
    $areas=@($areas | Sort-Object -Unique)
    $points=@($points | Sort-Object -Unique)
    $frames=@($frames | Sort-Object -Unique)
    if (-not $areas.Count) { throw 'Group1 has no areas to mesh.' }
    $beforeCount=0; $beforeNames=$null
    if ($model.PointObj.GetNameList([ref]$beforeCount,[ref]$beforeNames) -ne 0) {
        throw 'Cannot read original joint names.'
    }
    $beforeAreaCount=0; $beforeAreaNames=$null
    if ($model.AreaObj.GetNameList([ref]$beforeAreaCount,[ref]$beforeAreaNames) -ne 0) {
        throw 'Cannot read original area names.'
    }
    $original = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $beforeNames) { $null=$original.Add($name) }
    $originalAreas = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $beforeAreaNames) { $null=$originalAreas.Add($name) }
    [long]$maximum=0
    foreach ($name in $original) {
        [long]$value=0
        if ([long]::TryParse($name,[Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,[ref]$value)) {
            $maximum=[Math]::Max($maximum,$value)
        }
    }
    [long]$maximumArea=0
    foreach ($name in $originalAreas) {
        [long]$value=0
        if ([long]::TryParse($name,[Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,[ref]$value)) {
            $maximumArea=[Math]::Max($maximumArea,$value)
        }
    }
    "TEST01_SOURCE_JOINTS=$beforeCount MAX_NUMERIC=$maximum SELECTED_AREAS=$($areas -join ',') SELECTED_POINTS=$($points.Count) SELECTED_FRAMES=$($frames.Count)"
    $env:SAP_GMSH_V2_ATOMIC_REGULAR_GRID='1'
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES='1'
    $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR=$ResultsRoot
    $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $ResultsRoot 'isolated-options.json'
    $env:SAP_QUAD_ATOMIC_REGULAR='0'
    $env:SAP_QUAD_ATOMIC_UNDO=if ($Undo) { '1' } else { $null }
    $env:SAP_QUAD_ATOMIC_BACKUP='0'
    $env:SAP_QUAD_ATOMIC_WORKERS='1'
    [RunTest01Fast]::RunAtomicExampleWithTimeout($input,$output,$Plugin,
        [string[]]$areas,[string[]]$points,[string[]]$frames,'0.25m',
        '','0.05m','',$false,$true,'General Purpose','xy','',900)
    if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $sourceHash -or
        (Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -ne $sourceHash) {
        throw 'Result absent or source/input model changed.'
    }
    $settings=@(Get-ChildItem -LiteralPath $ResultsRoot -Filter '*-settings.json' -File)
    if (-not @($settings | Where-Object {
        (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).structured.mode -eq 'automatic'
    }).Count) { throw 'Automatic regular-grid option was not enabled.' }
    if ($model.File.OpenFile($output) -ne 0) { throw 'Saved meshed model did not reopen.' }
    $afterCount=0; $afterNames=$null
    if ($model.PointObj.GetNameList([ref]$afterCount,[ref]$afterNames) -ne 0) {
        throw 'Cannot read applied joint names.'
    }
    $afterAreaCount=0; $afterAreaNames=$null
    if ($model.AreaObj.GetNameList([ref]$afterAreaCount,[ref]$afterAreaNames) -ne 0) {
        throw 'Cannot read applied area names.'
    }
    if ($Undo) {
        $restored = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $afterNames) { $null=$restored.Add($name) }
        $restoredAreas = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $afterAreaNames) { $null=$restoredAreas.Add($name) }
        if ($afterCount -ne $beforeCount -or -not $restored.SetEquals($original)) {
            throw 'Undo did not restore the original joint names.'
        }
        if ($afterAreaCount -ne $beforeAreaCount -or -not $restoredAreas.SetEquals($originalAreas)) {
            throw 'Undo did not restore the original area names.'
        }
        "TEST01_UNDO=PASS SIZE=0.25m REGULAR=YES JOINTS=$afterCount AREAS=$afterAreaCount SOURCE_SHA256=$sourceHash OUTPUT=$output"
        return
    }
    $added=@($afterNames | Where-Object { -not $original.Contains($_) })
    if ($added.Count -lt 1 -or @($added | Where-Object { $_ -cnotmatch '^[0-9]+$' -or
        [long]$_ -le $maximum }).Count) {
        throw "New joint names are not exclusively numeric above $maximum."
    }
    $numbers=@($added | ForEach-Object { [long]$_ } | Sort-Object)
    for ($i=0; $i -lt $numbers.Count; $i++) {
        if ($numbers[$i] -ne $maximum + $i + 1) {
            throw "Joint numbering is not consecutive after $maximum."
        }
    }
    $newAreas=@($afterAreaNames | Where-Object { -not $originalAreas.Contains($_) })
    if ($newAreas.Count -lt 1 -or @($newAreas | Where-Object { $_ -cnotmatch '^[0-9]+$' -or
        [long]$_ -le $maximumArea }).Count -or
        @($afterNames + $afterAreaNames | Where-Object { $_ -match '^QT(?:P|A|B|UNDO|R)' }).Count) {
        throw 'The applied model retains nonnumeric or temporary names.'
    }
    $areaNumbers=@($newAreas | ForEach-Object { [long]$_ } | Sort-Object)
    for ($i=0; $i -lt $areaNumbers.Count; $i++) {
        if ($areaNumbers[$i] -ne $maximumArea + $i + 1) {
            throw "Area numbering is not consecutive after $maximumArea."
        }
    }
    "TEST01_NUMERIC_NAMES=PASS SIZE=0.25m REGULAR=YES NEW_JOINTS=$($added.Count) JOINTS=$($numbers[0])..$($numbers[-1]) NEW_AREAS=$($newAreas.Count) AREAS=$($areaNumbers[0])..$($areaNumbers[-1]) SOURCE_SHA256=$sourceHash OUTPUT=$output"
} finally {
    foreach ($key in $old.Keys) { [Environment]::SetEnvironmentVariable($key,$old[$key]) }
    if ($started) { [RunTest01Fast]::StopIsolatedPersistentSap() }
}
