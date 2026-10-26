# Reuse unique existing SAP joints; refuse ambiguous input or new overlaps.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+PointCoordinateIndex', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$index = $type.GetConstructors($flags)[0].Invoke([object[]]@(0.0006))
$register = $type.GetMethod('RegisterArea', $flags)
$permit = $type.GetMethod('Permit', $flags)
$add = $type.GetMethod('AddOriginal', $flags)
$generated = $type.GetMethod('Add', $flags)
$lookup = $type.GetMethod('TryFind', $flags)
function Register($area, [int]$group, [string[]]$corners) {
    $args = [object[]]::new(3); $args[0]=$area; $args[1]=$group; $args[2]=$corners
    $register.Invoke($index,$args) | Out-Null
}
function AddOriginal($name, [double[]]$xyz) {
    $args = [object[]]::new(2); $args[0]=$xyz; $args[1]=$name
    $add.Invoke($index,$args) | Out-Null
}
function Lookup($area, [double[]]$xyz) {
    $args = [object[]]::new(3); $args[0]=$xyz; $args[1]=$area; $args[2]=$null
    $found = $lookup.Invoke($index,$args)
    return $(if ($found) { [string]$args[2] } else { '' })
}
Register '845' 0 ([string[]]@('878'))
Register '444' 1 ([string[]]@('109'))
Register 'S1' 2 ([string[]]@('A'))
Register 'S2' 2 ([string[]]@('B'))
$permit.Invoke($index,[object[]]@('845','109')) | Out-Null
$permit.Invoke($index,[object[]]@('845','878')) | Out-Null
$permit.Invoke($index,[object[]]@('444','109')) | Out-Null
AddOriginal '109' ([double[]]@(1200,600,0))
AddOriginal '878' ([double[]]@(1200,600,0))
$corner = [double[]]@(1200,600,0)
$first = Lookup '845' $corner
$second = Lookup '444' $corner
if ($first -ne '878' -or $second -ne '109') {
    throw "A coincident foreign node displaced the owner area corner: 845=$first 444=$second"
}
$new = [double[]]@(5000,600,0)
$generated.Invoke($index,[object[]]@($new,'QTPNEW','S1')) | Out-Null
if ((Lookup 'S2' $new) -ne 'QTPNEW' -or (Lookup '845' $new) -ne 'QTPNEW') {
    throw 'An existing joint must be found by coordinate even outside its original area group.'
}
$separate = [double[]]@(6000,600,0)
$generated.Invoke($index,[object[]]@($separate,'QTPGROUP0','845')) | Out-Null
if ((Lookup '845' $separate) -ne 'QTPGROUP0' -or (Lookup '444' $separate) -ne 'QTPGROUP0') {
    throw 'No second QTP may be generated on top of an indexed point.'
}
$require = $type.GetMethod('RequireUnoccupiedForNew', $flags)
$blocked = $false
try { $require.Invoke($index,[object[]]@($separate,'444')) | Out-Null }
catch { $blocked = $_.Exception.Message.Contains('QTPGROUP0') }
if (-not $blocked) {
    throw 'A second QTP at the same coordinates must be refused, not generated.'
}
$isolated = $type.GetConstructors($flags)[0].Invoke([object[]]@(0.0006))
$register.Invoke($isolated,[object[]]@('845',0,[string[]]@('878'))) | Out-Null
$register.Invoke($isolated,[object[]]@('444',1,[string[]]@('109'))) | Out-Null
$permit.Invoke($isolated,[object[]]@('845','878')) | Out-Null
$permit.Invoke($isolated,[object[]]@('444','109')) | Out-Null
$add.Invoke($isolated,[object[]]@($corner,'109')) | Out-Null
$add.Invoke($isolated,[object[]]@($corner,'878')) | Out-Null
$meshType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshData', $true)
$validate = $type.GetMethod('ValidateNewJointPositions', $flags)
$preparedType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+PreparedMesh', $true)
$preparedConstructor = $preparedType.GetConstructors($flags)[0]
$listType = [Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType([type[]]@($preparedType))
function Candidate($area, [double[]]$xyz) {
    $candidateMesh = $meshType.GetConstructors($flags)[0].Invoke([object[]]@())
    $candidateMesh.GetType().GetField('Nodes',$flags).GetValue($candidateMesh).Add(1,$xyz)
    $preparedConstructor.Invoke([object[]]@($area,'',0.0,0.0,$false,$null,$null,$candidateMesh,$null))
}
function Validate($pointIndex, $candidates) {
    $list = [Activator]::CreateInstance($listType)
    foreach ($candidate in $candidates) { $list.Add($candidate) }
    $args = [object[]]::new(1); $args[0]=$list
    $validate.Invoke($pointIndex,$args) | Out-Null
}
$foreignOriginal = [double[]]@(2500,600,0)
$add.Invoke($isolated,[object[]]@($foreignOriginal,'146')) | Out-Null
$permit.Invoke($isolated,[object[]]@('444','146')) | Out-Null
Validate $isolated @((Candidate '845' $foreignOriginal))
$lookupArgs=[object[]]@($foreignOriginal,'845',$null)
if (-not $lookup.Invoke($isolated,$lookupArgs) -or $lookupArgs[2] -ne '146') {
    throw 'An existing unique structural joint 146 must be reused across area groups.'
}
$blocked = $false
try { Validate $isolated @((Candidate '845' $corner),(Candidate '444' $corner)) }
catch { $blocked = $_.Exception.Message.Contains('109') -and $_.Exception.Message.Contains('878') }
if (-not $blocked) { throw 'Ambiguous original coincident joints must be rejected before insertion.' }
$twoGroups = $type.GetConstructors($flags)[0].Invoke([object[]]@(0.0006))
$register.Invoke($twoGroups,[object[]]@('S1',0,[string[]]@('A'))) | Out-Null
$register.Invoke($twoGroups,[object[]]@('S2',1,[string[]]@('B'))) | Out-Null
$blocked = $false
try { Validate $twoGroups @((Candidate 'S1' $new),(Candidate 'S2' $new)) }
catch { $blocked = $_.Exception.Message.Contains('S1 and S2') }
if (-not $blocked) { throw 'FAST preflight must refuse two new QTPs at the same coordinates.' }
$share = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true).GetMethod(
    'SharesTopologicalEdge', [Reflection.BindingFlags]'Static,NonPublic')
$long = [double[][]]@([double[]]@(0,0,0),[double[]]@(4,0,0),
    [double[]]@(4,-1,0),[double[]]@(0,-1,0))
$short = [double[][]]@([double[]]@(0,0,0),[double[]]@(1,0,0),
    [double[]]@(1,1,0),[double[]]@(0,1,0))
$shortCoincidentEnd = [double[][]]@([double[]]@(0,0,0),[double[]]@(4,0,0),
    [double[]]@(4,1,0),[double[]]@(0,1,0))
if (-not $share.Invoke($null,[object[]]@([string[]]@('A','D','E','F'),$long,
    [string[]]@('A','B','C','G'),$short))) {
    throw 'A named station inside a longer edge must connect the partial shared seam.'
}
if ($share.Invoke($null,[object[]]@([string[]]@('A','D','E','F'),$long,
    [string[]]@('A','B','C','G'),$shortCoincidentEnd)) -or
    $share.Invoke($null,[object[]]@([string[]]@('A','D','E','F'),$long,
    [string[]]@('X','B','C','G'),$short))) {
    throw 'Merely coincident edge endpoints must remain disconnected.'
}
$offset = [double[][]]@([double[]]@(0.5,0,0),[double[]]@(1.5,0,0),
    [double[]]@(1.5,1,0),[double[]]@(0.5,1,0))
$sameSide = [double[][]]@([double[]]@(0.5,0,0),[double[]]@(1.5,0,0),
    [double[]]@(1.5,-1,0),[double[]]@(0.5,-1,0))
if (-not $share.Invoke($null,[object[]]@([string[]]@('A','D','E','F'),$long,
    [string[]]@('H','I','J','K'),$offset)) -or
    $share.Invoke($null,[object[]]@([string[]]@('A','D','E','F'),$long,
    [string[]]@('H','I','J','K'),$sameSide))) {
    throw 'Only opposite-side partial seams without coincident original joints may be connected.'
}
'UNIQUE_EXISTING_JOINT_REUSE=PASS AMBIGUOUS_INPUT_REJECTED=PASS SECOND_QTP_REJECTED=PASS PARTIAL_SEAMS=PASS'
