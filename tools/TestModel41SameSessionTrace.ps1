# Two full Generate/apply cycles in one isolated SAP process on disposable SDBs.
# The API display read is a guard for the repeated trace path, not a substitute
# for the user's manual Show Tables > Excel GUI sequence.
param([Parameter(Mandatory=$true)][string]$Source,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot)
$ErrorActionPreference='Stop'
$parent=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'opencode')).TrimEnd('\')+'\'
$folder=[IO.Path]::GetFullPath($ResultsRoot)
$root=Split-Path -Parent $PSScriptRoot
if(-not $folder.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path -LiteralPath $folder) -or
   -not(Test-Path -LiteralPath (Split-Path -Parent $folder) -PathType Container) -or
   -not(Test-Path -LiteralPath $Source -PathType Leaf) -or
   -not(Test-Path -LiteralPath $Plugin -PathType Leaf)){
    throw 'Use a new results folder in TEMP\opencode and existing model/plugin.'
}
$hash=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $folder|Out-Null
$first=Join-Path $folder 'Steel_Plate-first-input.sdb'
$second=Join-Path $folder 'Steel_Plate-second-input.sdb'
Copy-Item -LiteralPath $Source -Destination $first
Copy-Item -LiteralPath $Source -Destination $second
[Reflection.Assembly]::LoadFrom('C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll')|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'))|Out-Null
$names=@('SAP_GMSH_V2_ATOMIC_OGRID','SAP_QUAD_ATOMIC_BACKUP',
    'SAP_QUAD_ATOMIC_CLOSE_APPLY','SAP_QUAD_LEAVE_COMPLETED_DIALOG',
    'SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_GMSH_V2_SETTINGS_PATH',
    'SAP_GMSH_V2_ATOMIC_MINIMUM_CHORDS','SAP_GMSH_V2_ATOMIC_WIDTH_DIAMETERS')
$old=@{}
foreach($name in $names){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
$started=$false
try{
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$null)}
    [RunTest01Fast]::StartIsolatedPersistentSap();$started=$true
    $pidOwned=[int][RunTest01Fast].GetField('persistentSapPid',
        [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $model=[RunTest01Fast].GetField('persistentSap',
        [Reflection.BindingFlags]'Static,NonPublic').GetValue($null).SapModel
    $env:SAP_GMSH_V2_ATOMIC_OGRID='1'
    $env:SAP_QUAD_ATOMIC_BACKUP='0'
    $env:SAP_QUAD_ATOMIC_CLOSE_APPLY='1'
    $env:SAP_QUAD_LEAVE_COMPLETED_DIALOG='0'
    $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR=$folder
    $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $folder 'saved-options.json'
    foreach($step in 1..2){
        $input=if($step -eq 1){$first}else{$second}
        $output=Join-Path $folder "Steel_Plate-$step-meshed.sdb"
        [RunTest01Fast]::RunAtomicExampleWithTimeout($input,$output,$Plugin,
            [string[]]@('1','2','3'),[string[]]@(),[string[]]@(),
            '0.01m','BOLTS','0.02m','SHAFT',$false,$true,
            'General Purpose','xy','',600)
        if(-not(Test-Path -LiteralPath $output -PathType Leaf)){throw "Meshed SDB $step missing."}
        if($step -eq 1){
            $version=0;$records=0;$groups=$null;$fields=$null;$values=$null
            $code=$model.DatabaseTables.GetTableForDisplayArray('Joint Coordinates',
                [ref]$groups,'All',[ref]$version,[ref]$fields,[ref]$records,[ref]$values)
            if($code -ne 0 -or $records -lt 100){throw "SAP display joint table failed: $code/$records"}
            "FIRST_DISPLAY_JOINTS=$records"
        }
        "GENERATE_$step=PASS PID=$pidOwned MODEL=$output"
    }
    $traces=@(Get-ChildItem -LiteralPath $folder -Filter 'manifest*-settings.json' -File)
    if($traces.Count -ne 2 -or @($traces|Select-Object -ExpandProperty Name -Unique).Count -ne 2){
        throw "The two Generate calls did not keep distinct settings JSON traces: $($traces.Count)."
    }
    "SAME_SESSION=PASS TRACES=$($traces.Name -join ',') ORIGINAL_UNCHANGED=PASS"
}finally{
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$old[$name])}
    if($started){[RunTest01Fast]::StopIsolatedPersistentSap()}
    if((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $hash){
        throw 'Original Steel_Plate.sdb changed.'
    }
}
