# Generate once on a disposable Steel_Plate copy; leave the owned SAP process
# open for the user's manual Display > Show Tables > Excel operation.
param([Parameter(Mandatory=$true)][string]$Source,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temp=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'opencode')).TrimEnd('\')+'\'
$folder=[IO.Path]::GetFullPath($ResultsRoot)
if(-not $folder.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path -LiteralPath $folder) -or
   -not(Test-Path -LiteralPath (Split-Path -Parent $folder) -PathType Container) -or
   -not(Test-Path -LiteralPath $Source -PathType Leaf) -or
   -not(Test-Path -LiteralPath $Plugin -PathType Leaf) -or
   @(Get-Process SAP2000 -ErrorAction SilentlyContinue).Count){
    throw 'Use a new TEMP\opencode folder, existing source/plugin and no open SAP2000.'
}
$hash=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $folder|Out-Null
$first=Join-Path $folder 'Steel_Plate-first-input.sdb'
$second=Join-Path $folder 'Steel_Plate-second-input.sdb'
$output=Join-Path $folder 'Steel_Plate-first-meshed.sdb'
Copy-Item -LiteralPath $Source -Destination $first
Copy-Item -LiteralPath $Source -Destination $second
[Reflection.Assembly]::LoadFrom('C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll')|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'))|Out-Null
$names=@('SAP_GMSH_V2_ATOMIC_OGRID','SAP_QUAD_ATOMIC_BACKUP',
    'SAP_QUAD_ATOMIC_CLOSE_APPLY','SAP_QUAD_LEAVE_COMPLETED_DIALOG',
    'SAP_GMSH_V2_ATOMIC_MINIMUM_CHORDS','SAP_GMSH_V2_ATOMIC_WIDTH_DIAMETERS',
    'SAP_GMSH_V2_SETTINGS_PATH','SAP_GMSH_V2_SETTINGS_TRACE_DIR','SAP_QUAD_COMPLETION_TRACE')
$old=@{}
foreach($name in $names){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
$owned=$false;$detached=$false;$pidOwned=0
try{
    # The SAP process inherited these variables in an earlier trial; its next
    # GUI Generate tried to copy the same manifest-settings.json twice. Start
    # the host clean, then set automation only in this PowerShell test process.
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$null)}
    [RunTest01Fast]::StartIsolatedPersistentSap();$owned=$true
    $pidOwned=[int][RunTest01Fast].GetField('persistentSapPid',
        [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    [IO.File]::WriteAllText((Join-Path $folder 'owned-sap.pid'),[string]$pidOwned)
    $env:SAP_GMSH_V2_ATOMIC_OGRID='1'
    $env:SAP_QUAD_ATOMIC_BACKUP='0'
    $env:SAP_QUAD_ATOMIC_CLOSE_APPLY='1'
    $env:SAP_QUAD_LEAVE_COMPLETED_DIALOG='0'
    $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $folder 'settings.json'
    $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR=$folder
    $env:SAP_QUAD_COMPLETION_TRACE=Join-Path $folder 'completed.txt'
    [RunTest01Fast]::RunAtomicExampleWithTimeout($first,$output,$Plugin,
        [string[]]@('1','2','3'),[string[]]@(),[string[]]@(),'0.01m',
        'BOLTS','0.02m','SHAFT',$false,$true,'General Purpose','xy','',600)
    if(-not (Test-Path -LiteralPath $output -PathType Leaf)){
        throw 'First meshed SDB is missing.'
    }
    $sap=[RunTest01Fast].GetField('persistentSap',
        [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    if(-not [string]::Equals($sap.SapModel.GetModelFilename($true),$output,
        [StringComparison]::OrdinalIgnoreCase)){
        throw 'The meshed SDB is not active in SAP2000.'
    }
    [RunTest01Fast]::DetachPersistentSap();$detached=$true
    if(-not (Get-Process -Id $pidOwned -ErrorAction SilentlyContinue)){
        throw 'The first meshing SAP process exited unexpectedly.'
    }
    "FIRST_MESH=PASS SAP_PID=$pidOwned ACTIVE_MESHED=$output SECOND_COPY=$second"
}finally{
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$old[$name])}
    if($owned -and -not $detached){
        try{[RunTest01Fast]::StopIsolatedPersistentSap()}catch{Write-Warning "Owned SAP exit: $_"}
    }
    if((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $hash){
        throw 'Original Steel_Plate.sdb changed.'
    }
}
