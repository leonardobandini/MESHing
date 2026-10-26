# SPDX-License-Identifier: GPL-2.0-or-later
# Regression for a grouped opening side coincident with separately named frames.
param([Parameter(Mandatory=$true)][string]$Plugin,
      [string]$ApiPath = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll')
$ErrorActionPreference = 'Stop'
Add-Type -Path $ApiPath
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$pluginType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$frameType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+FrameConstraint', $true)
$constructor = @($frameType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic'))[0]
$contains = $pluginType.GetMethod('OpeningContainsFrame',
    [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $constructor -or $null -eq $contains) { throw 'Opening membership API missing.' }
$opening = [Tuple]::Create([double[]]@(1,0,0), [double[]]@(1,1,0))
function Is-Member([double[]]$first, [double[]]$second) {
    $frame = $constructor.Invoke([object[]]@('other-frame','a','b',$first,$second,$false,$null,-1))
    return [bool]$contains.Invoke($null,[object[]]@($opening,$frame))
}
foreach ($pair in @(
        @([double[]]@(1,0,0), [double[]]@(1,.25,0)),
        @([double[]]@(1,.25,0), [double[]]@(1,.75,0)),
        @([double[]]@(1,1,0), [double[]]@(1,.75,0)))) {
    if (-not (Is-Member $pair[0] $pair[1])) { throw 'An overlapping subsegment lost opening membership.' }
}
if (Is-Member ([double[]]@(1,-.2,0)) ([double[]]@(1,.25,0))) {
    throw 'A partially outside segment was incorrectly included.'
}
if (Is-Member ([double[]]@(1.001,.25,0)) ([double[]]@(1.001,.75,0))) {
    throw 'An offset parallel segment was incorrectly included.'
}
'OPENING_MEMBERSHIP=PASS SUBSEGMENTS=3 OUTSIDE=REJECTED OFFSET=REJECTED'
