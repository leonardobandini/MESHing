# Disposable xAPI meshing trials. Never save the user's open model.
param([Parameter(Mandatory=$true)][ValidateSet('CSiBridge','ETABS')][string]$HostKind,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
       [switch]$InspectOnly,
       [switch]$Undo,
       [switch]$AllAtomic,
       [ValidatePattern('^nTEST\d{3}$')][string[]]$CaseIds=@(),
        [string]$Ids='',
        [string]$InputModelDirectory='', [string]$ManifestPath='')
$ErrorActionPreference='Stop'
$temp=Join-Path $env:TEMP 'opencode'
if(-not(Test-Path -LiteralPath $temp -PathType Container) -or
    -not $ResultsRoot.StartsWith($temp.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $ResultsRoot) -or
    -not(Test-Path -LiteralPath $Plugin -PathType Leaf) -or
    ((-not $Ids -or $HostKind -ne 'CSiBridge') -and
        @(Get-Process $HostKind -ErrorAction SilentlyContinue).Count)){
    throw 'Host trial requires a new temporary folder and no existing instance of the requested host.'
}
if($Ids -and $CaseIds.Count){throw 'Use either Ids or CaseIds, not both.'}
$requested=if($Ids){@($Ids.Split(',')|ForEach-Object {$_.Trim()})}else{@()}
if($requested.Count -and ($HostKind -ne 'CSiBridge' -or
    @($requested|Where-Object {$_ -notmatch '^nTEST\d{3}$'}).Count -or
    @($requested|Select-Object -Unique).Count -ne $requested.Count)){
    throw 'Ids must be distinct comma-separated CSiBridge nTEST numbers.'
}
$install=if($HostKind -eq 'CSiBridge'){'C:\Program Files\Computers and Structures\CSiBridge 27'}
         else{'C:\Program Files\Computers and Structures\ETABS 23'}
$exe=Join-Path $install ($HostKind+'.exe')
$api=Join-Path $install 'CSiAPIv1.dll'
if(-not(Test-Path -LiteralPath $exe -PathType Leaf) -or
   -not(Test-Path -LiteralPath $api -PathType Leaf)){throw 'Host executable or xAPI missing.'}
New-Item -ItemType Directory -Path $ResultsRoot|Out-Null
Add-Type -Path $api
Add-Type -ReferencedAssemblies $api -TypeDefinition @'
using CSiAPIv1;
public sealed class FEMeshHostCallback : cPluginCallback {
    public bool Finished {get; private set;}
    public int ErrorFlag {get; private set;}
    public void Finish(int error) {Finished=true;ErrorFlag=error;}
}
'@
$old=@{}
foreach($key in @('SAP_QUAD_ATOMIC_SIZE','SAP_QUAD_ATOMIC_FAST','SAP_QUAD_ATOMIC_BACKUP',
    'SAP_QUAD_ATOMIC_PROFILE','SAP_QUAD_ATOMIC_WORKERS','SAP_QUAD_AUTOMATION_ERROR',
    'SAP_QUAD_COMPLETION_TRACE','SAP_GMSH_V2_SETTINGS_PATH','SAP_QUAD_TEST_UPDATE_MANIFEST_JSON',
    'SAP_QUAD_ATOMIC_UNDO','SAP_QUAD_ATOMIC_HOLE_GROUP','SAP_QUAD_ATOMIC_DIAMETER',
    'SAP_QUAD_ATOMIC_OPENING_GROUP','SAP_QUAD_ATOMIC_SYMMETRY',
    'SAP_GMSH_V2_ATOMIC_EXAMPLES','SAP_GMSH_V2_ATOMIC_OGRID',
    'SAP_GMSH_V2_ATOMIC_UI_PREVIEW','SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT',
    'SAP_GMSH_V2_SETTINGS_TRACE_DIR')){
    $old[$key]=[Environment]::GetEnvironmentVariable($key)
}
$sap=$null;$ownedPid=0
try {
    $mutex=[Threading.Mutex]::new($false,'Local\FEMeshBridgeRegressionStartup')
    try {
        $null=$mutex.WaitOne()
        $before=@(Get-Process $HostKind -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $helper=[CSiAPIv1.cHelper](New-Object CSiAPIv1.Helper)
        $sap=$helper.CreateObject($exe)
        if($null -eq $sap){throw "Cannot create $HostKind OAPI object."}
        $new=@(Get-Process $HostKind -ErrorAction SilentlyContinue | Where-Object {$_.Id -notin $before})
        if($new.Count -eq 1){$ownedPid=$new[0].Id;[IO.File]::WriteAllText((Join-Path $ResultsRoot 'host.pid'),[string]$ownedPid)}
        $startMethod=[CSiAPIv1.cOAPI].GetMethod('ApplicationStart',[Type[]]@())
        if($null -eq $startMethod){throw "$HostKind xAPI has no parameterless ApplicationStart."}
        $start=[int]$startMethod.Invoke($sap.PSObject.BaseObject,[object[]]@())
        if($start -ne 0 -or $new.Count -ne 1){throw "$HostKind ApplicationStart=$start, new instances=$($new.Count)."}
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
    $model=$sap.SapModel
    $version='';$versionNumber=0.0
    $code=$model.GetVersion([ref]$version,[ref]$versionNumber)
    if($code -ne 0){throw "Cannot query $HostKind version."}
    "$HostKind VERSION=$version PID=$ownedPid"
    $assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $manifestFile=if($ManifestPath){(Resolve-Path -LiteralPath $ManifestPath).Path}
                  else{Join-Path (Split-Path -Parent $PSScriptRoot) 'examples\atomic\manifest.json'}
    $models=if($InputModelDirectory){(Resolve-Path -LiteralPath $InputModelDirectory).Path}
            else{Join-Path (Split-Path -Parent $PSScriptRoot) 'examples\atomic\models'}
    $manifest=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $cases=if($HostKind -eq 'CSiBridge'){
        if($requested.Count){$requested}elseif($CaseIds.Count){@($CaseIds)}elseif($AllAtomic){@($manifest | ForEach-Object id)}else{@('nTEST001','nTEST014')}
    }else{@('synthetic-floor')}
    $completed=0
    foreach($id in $cases){
        $entry=if($HostKind -eq 'CSiBridge'){@($manifest|Where-Object id -eq $id)[0]}else{$null}
        if($HostKind -eq 'CSiBridge' -and -not $entry){throw "Missing manifest entry for $id"}
        $folder=Join-Path $ResultsRoot $id
        New-Item -ItemType Directory -Path $folder|Out-Null
        $output=Join-Path $folder ($id+$(if($HostKind -eq 'CSiBridge'){'.bdb'}else{'.edb'}))
        if($HostKind -eq 'CSiBridge'){
            $source=Join-Path $models ($id+'.sdb')
            $input=Join-Path $folder ($id+'-input.sdb')
            $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            Copy-Item -LiteralPath $source -Destination $input
            if((Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -ne $hash){throw 'SDB trial copy differs.'}
            [IO.File]::WriteAllText((Join-Path $folder 'input.sha256'),$hash)
            $code=$model.File.OpenFile($input)
            if($code -ne 0){throw "$HostKind cannot open copied $id SDB (code=$code)."}
        } else {
            $code=$model.InitializeNewModel([CSiAPIv1.eUnits]::kN_m_C)
            if($code -ne 0){throw "ETABS cannot initialize new model ($code)."}
            $code=$model.File.NewBlank()
            if($code -ne 0){throw "ETABS cannot create a blank model ($code)."}
            $points=[string[]]@()
            foreach($xyz in @(@(0.0,0.0,0.0),@(2.0,0.0,0.0),@(2.0,1.0,0.0),@(0.0,1.0,0.0))){
                $name=''
                $code=$model.PointObj.AddCartesian($xyz[0],$xyz[1],$xyz[2],[ref]$name,'','Global',$false,0)
                if($code -ne 0){throw "ETABS PointObj.AddCartesian failed ($code)."}
                $points+= [string]$name
            }
            $area=''
            $code=$model.AreaObj.AddByPoint(4,[ref]$points,[ref]$area,'Default','FEMeshTrial')
            if($code -ne 0){throw "ETABS AreaObj.AddByPoint failed ($code)."}
        }
        $code=$model.File.Save($output)
        if($code -ne 0 -or -not(Test-Path -LiteralPath $output -PathType Leaf)){
            throw "$HostKind did not save the temporary $id model ($code)."
        }
        "$HostKind SAVED_MODEL_FILENAME=$($model.GetModelFilename($true)) EXT=$([IO.Path]::GetExtension($model.GetModelFilename($true)))"
        if($HostKind -eq 'ETABS'){
            $totalTables=0;$tableKeys=@();$tableNames=@();$tableTypes=@();$tableEmpty=@()
            $listed=$model.DatabaseTables.GetAllTables([ref]$totalTables,[ref]$tableKeys,
                [ref]$tableNames,[ref]$tableTypes,[ref]$tableEmpty)
            if($listed -ne 0){throw "ETABS GetAllTables failed ($listed)."}
            if($InspectOnly){
                for($i=0;$i -lt $totalTables;$i++){
                    if($tableKeys[$i] -match 'Coordinate|Connectivity|Area Section|Story|Point Object|Shell Object|Floor|Area Assignments'){
                        "ETABS_AVAILABLE_KEY=$($tableKeys[$i]) TYPE=$($tableTypes[$i]) EMPTY=$($tableEmpty[$i])"
                    }
                }
            }
            foreach($key in @('Joint Coordinates','Connectivity - Area','Area Section Assignments')){
                $index=[array]::IndexOf($tableKeys,$key)
                $v=0;$fields=@();$records=0;$data=@()
                $edit=$model.DatabaseTables.GetTableForEditingArray($key,'',[ref]$v,
                    [ref]$fields,[ref]$records,[ref]$data)
                $displayVersion=0;$displayRecords=0;$groups=@();$shown=@();$displayed=@()
                $display=$model.DatabaseTables.GetTableForDisplayArray($key,[ref]$groups,'',
                    [ref]$displayVersion,[ref]$shown,[ref]$displayRecords,[ref]$displayed)
                "ETABS_GEOMETRY_TABLE=$key LISTED=$($index -ge 0) EMPTY=$(if($index -ge 0){$tableEmpty[$index]}else{'N/A'}) TYPE=$(if($index -ge 0){$tableTypes[$index]}else{'N/A'}) EDIT=$edit EDIT_ROWS=$records DISPLAY=$display DISPLAY_ROWS=$displayRecords EDIT_FIELDS=$($fields -join '|') DISPLAY_FIELDS=$($shown -join '|')"
            }
            if($InspectOnly){
                foreach($key in @('Point Object Connectivity','Floor Object Connectivity',
                    'Area Assignments - Section Properties')){
                    $v=0;$fields=@();$records=0;$data=@()
                    $code=$model.DatabaseTables.GetTableForEditingArray($key,'',[ref]$v,
                        [ref]$fields,[ref]$records,[ref]$data)
                    "ETABS_EDITABLE_SCHEMA=$key CODE=$code ROWS=$records FIELDS=$($fields -join '|')"
                    if($code -eq 0 -and $records -gt 0 -and $fields.Count){
                        "ETABS_EDITABLE_FIRST_ROW=$(@($data[0..($fields.Count-1)]) -join '|')"
                    }
                }
            }
            $storyBefore=@{}
            foreach($key in @('Story Definitions','Tower and Base Story Definitions')){
                $v=0;$fields=@();$records=0;$data=@()
                $edit=$model.DatabaseTables.GetTableForEditingArray($key,'',[ref]$v,
                    [ref]$fields,[ref]$records,[ref]$data)
                $displayVersion=0;$displayRecords=0;$groups=@();$shown=@();$displayed=@()
                $display=$model.DatabaseTables.GetTableForDisplayArray($key,[ref]$groups,'',
                    [ref]$displayVersion,[ref]$shown,[ref]$displayRecords,[ref]$displayed)
                "ETABS_STORY_TABLE=$key EDIT=$edit DISPLAY=$display ROWS=$displayRecords FIELDS=$($shown -join '|')"
                if($display -eq 0 -and $displayRecords -gt 0 -and $shown.Count){
                    "ETABS_STORY_FIRST_ROW=$(@($displayed[0..($shown.Count-1)]) -join '|')"
                    $storyBefore[$key]=[pscustomobject]@{Version=$displayVersion;Fields=@($shown);Rows=$displayRecords;Data=@($displayed)}
                } else {
                    throw "ETABS story table $key is not fully readable."
                }
            }
        }
        if($InspectOnly){'ETABS_STORY_INSPECTION_COMPLETE=YES';return}
        $sourceAreas=0;$areas=@()
        $code=$model.AreaObj.GetNameList([ref]$sourceAreas,[ref]$areas)
        if($code -ne 0 -or $sourceAreas -lt 1){throw "No $HostKind areas before meshing."}
        $originalAreas=@($areas)
        $sourcePointCount=0;$sourcePointNames=@()
        if($model.PointObj.GetNameList([ref]$sourcePointCount,[ref]$sourcePointNames) -ne 0){
            throw "$HostKind/$id cannot list source joint names."
        }
        $originalPoints=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($point in @($sourcePointNames)){
            if($point){$null=$originalPoints.Add($point)}
        }
        $originalAreaSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($area in $originalAreas){$null=$originalAreaSet.Add($area)}
        if($model.SelectObj.ClearSelection() -ne 0){throw 'Cannot clear selection.'}
        $selectedAreas=if($entry){[string[]]$entry.areas}else{[string[]]$areas}
        if(@($selectedAreas|Where-Object {$_ -notin $areas}).Count){throw "Missing selected area in $id"}
        foreach($area in $selectedAreas){
            if($model.AreaObj.SetSelected([string]$area,$true,[CSiAPIv1.eItemType]::Objects) -ne 0){
                throw "Cannot select $area."
            }
        }
        if($AllAtomic){
            foreach($point in @($entry.points)){
                if($point -and $model.PointObj.SetSelected([string]$point,$true,[CSiAPIv1.eItemType]::Objects) -ne 0){
                    throw "Cannot select point $point in $id"
                }
            }
            foreach($frame in @($entry.frames)){
                if($frame -and $model.FrameObj.SetSelected([string]$frame,$true,[CSiAPIv1.eItemType]::Objects) -ne 0){
                    throw "Cannot select frame $frame in $id"
                }
            }
        }
        $size=if($entry -and $AllAtomic){[double]$entry.maxSize}else{if($id -eq 'nTEST014'){0.16}else{0.5}}
        $env:SAP_QUAD_ATOMIC_SIZE=$size.ToString('G',[Globalization.CultureInfo]::InvariantCulture)+'m'
        $env:SAP_QUAD_ATOMIC_FAST=if($entry -and $null -ne $entry.fast -and -not [bool]$entry.fast){'0'}else{'1'}
        $env:SAP_QUAD_ATOMIC_BACKUP='0'
        $env:SAP_QUAD_ATOMIC_PROFILE=if($entry -and $entry.profile){[string]$entry.profile}else{'General Purpose'}
        $env:SAP_QUAD_ATOMIC_WORKERS='1'
        $env:SAP_QUAD_ATOMIC_UNDO=if($Undo){'1'}else{$null}
        $env:SAP_QUAD_ATOMIC_HOLE_GROUP=if($entry.circularGroup){[string]$entry.circularGroup}else{$null}
        $env:SAP_QUAD_ATOMIC_DIAMETER=if($entry.diameter){([double]$entry.diameter).ToString('G',[Globalization.CultureInfo]::InvariantCulture)+'m'}else{'0.05m'}
        $env:SAP_QUAD_ATOMIC_OPENING_GROUP=if($entry.openingGroup){[string]$entry.openingGroup}else{$null}
        $env:SAP_QUAD_ATOMIC_SYMMETRY=if($entry.symmetry){'1'}else{'0'}
        $env:SAP_GMSH_V2_ATOMIC_EXAMPLES=if($AllAtomic){'1'}else{$null}
        $env:SAP_GMSH_V2_ATOMIC_OGRID=if($entry.circularGroup -and $AllAtomic){'1'}else{$null}
        $env:SAP_GMSH_V2_ATOMIC_UI_PREVIEW=if($AllAtomic){'1'}else{$null}
        $env:SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT=if($AllAtomic){Join-Path $folder 'new-ui.png'}else{$null}
        $env:SAP_GMSH_V2_SETTINGS_TRACE_DIR=if($AllAtomic){$folder}else{$null}
        $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON='{}'
        $env:SAP_QUAD_AUTOMATION_ERROR=Join-Path $folder 'plugin-error.txt'
        $env:SAP_QUAD_COMPLETION_TRACE=Join-Path $folder 'completed.txt'
        $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $folder 'isolated-options.json'
        $callback=[FEMeshHostCallback]::new()
        $pluginObject=$assembly.CreateInstance('SapQuadTransitionPlugin.cPlugin')
        if($null -eq $pluginObject){throw 'FEMesh DLL did not load.'}
        $args=[object[]]@($model.PSObject.BaseObject,$callback.PSObject.BaseObject)
        $null=$pluginObject.GetType().GetMethod('Main').Invoke($pluginObject,$args)
        if(-not $callback.Finished -or $callback.ErrorFlag -ne 0 -or
           (Test-Path -LiteralPath $env:SAP_QUAD_AUTOMATION_ERROR)){
            $detail=if(Test-Path -LiteralPath $env:SAP_QUAD_AUTOMATION_ERROR){
                [IO.File]::ReadAllText($env:SAP_QUAD_AUTOMATION_ERROR)}else{[string]$callback.ErrorFlag}
            throw "$HostKind/$id FEMesh failed: $detail"
        }
        $after=0;$generated=@()
        if($model.AreaObj.GetNameList([ref]$after,[ref]$generated) -ne 0){
            throw "$HostKind/$id cannot list generated or restored areas."
        }
        if($Undo){
            if($after -ne $sourceAreas -or @(Compare-Object $originalAreas @($generated)).Count){
                throw "$HostKind/$id Undo did not restore the original areas ($after / $sourceAreas)."
            }
        } else {
            $completion=Join-Path $folder 'completed.txt'
            if($after -lt 1 -or -not(Test-Path -LiteralPath $completion -PathType Leaf)){
                throw "$HostKind/$id mesh was not completed ($after / $sourceAreas)."
            }
            $trace=[IO.File]::ReadAllText($completion)
            if($trace -notmatch '(?m)^elements=[1-9][0-9]*\s*$' -or
               $trace -notmatch ('(?m)^after_mesh_areas='+$after+'(?:\s|$)')){
                throw "$HostKind/$id completion trace differs from the live area inventory."
            }
        }
        if($HostKind -eq 'ETABS'){
            foreach($key in $storyBefore.Keys){
                $v=0;$rows=0;$groups=@();$fields=@();$values=@()
                $code=$model.DatabaseTables.GetTableForDisplayArray($key,[ref]$groups,'',
                    [ref]$v,[ref]$fields,[ref]$rows,[ref]$values)
                $beforeStory=$storyBefore[$key]
                if($code -ne 0 -or $v -ne $beforeStory.Version -or $rows -ne $beforeStory.Rows -or
                   (@($fields) -join [char]0) -cne ($beforeStory.Fields -join [char]0) -or
                   (@($values) -join [char]0) -cne ($beforeStory.Data -join [char]0)){
                    throw "ETABS story definitions changed during FEMesh: $key"
                }
            }
            'ETABS_IMMUTABLE_STORIES=PASS'
        }
        if($model.File.Save($output) -ne 0){throw "$HostKind/$id result did not save."}
        if($model.File.OpenFile($output) -ne 0){throw "$HostKind/$id result did not reopen."}
        $reopened=0;$reopenedNames=@()
        if($model.AreaObj.GetNameList([ref]$reopened,[ref]$reopenedNames) -ne 0 -or
           $reopened -ne $after){throw "$HostKind/$id reopened model lost mesh areas."}
        if($HostKind -eq 'CSiBridge' -and -not $Undo){
            $reopenedPoints=0;$reopenedPointNames=@()
            if($model.PointObj.GetNameList([ref]$reopenedPoints,[ref]$reopenedPointNames) -ne 0){
                throw "$HostKind/$id cannot list reopened joint names."
            }
            $newTempAreas=@($reopenedNames|Where-Object {
                $_ -match '^QT(?:P|A|B|UNDO|R)' -and -not $originalAreaSet.Contains($_)
            })
            $newTempPoints=@($reopenedPointNames|Where-Object {
                $_ -match '^QT(?:P|A|B|UNDO|R)' -and -not $originalPoints.Contains($_)
            })
            if($newTempAreas.Count -or $newTempPoints.Count){
                throw "$HostKind/$id introduced temporary labels: areas=$($newTempAreas -join ',') joints=$($newTempPoints -join ',')."
            }
            $preexisting=@($reopenedNames|Where-Object {$_ -match '^QT(?:P|A|B|UNDO|R)' -and $originalAreaSet.Contains($_)}).Count +
                @($reopenedPointNames|Where-Object {$_ -match '^QT(?:P|A|B|UNDO|R)' -and $originalPoints.Contains($_)}).Count
            "$HostKind/$id APPLIED_OBJECT_NAMES=PASS POINTS=$reopenedPoints AREAS=$reopened PREEXISTING_QT=$preexisting"
        }
        "$HostKind/$id PASS MODE=$(if($Undo){'UNDO'}else{'APPLY'}) BEFORE=$sourceAreas AFTER=$after OUTPUT=$output"
        $completed++
    }
    $expected=if($requested.Count){$requested.Count}elseif($CaseIds.Count){$CaseIds.Count}else{36}
    if($AllAtomic -and $completed -ne $expected){throw "Incomplete $HostKind matrix: $completed / $expected"}
    "$HostKind MATRIX=PASS COUNT=$completed HOST_PID=$ownedPid"
} finally {
    foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}
    if($sap -ne $null){
        try {
            $exitMethod=[CSiAPIv1.cOAPI].GetMethod('ApplicationExit',[Type[]]@([bool]))
            [void]$exitMethod.Invoke($sap.PSObject.BaseObject,[object[]]@($false))
        }catch{}
    }
    if($ownedPid){
        Start-Sleep -Milliseconds 700
        $p=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if($p -and $p.ProcessName -eq $HostKind){
            Stop-Process -Id $ownedPid -ErrorAction SilentlyContinue
        }
    }
}
