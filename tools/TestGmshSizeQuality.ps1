# Exercise the V2 sizing decision without creating or modifying an SAP model.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$method = $type.GetMethod('AcceptGmshSize', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $method) { throw 'V2 sizing policy is missing.' }
function Check([double]$max, [double]$fraction, [double]$weighted, [bool]$expected, [string]$label) {
    $actual = [bool]$method.Invoke($null, [object[]]@([double]0.05, $max, $fraction, $weighted))
    if ($actual -ne $expected) { throw "$label expected=$expected actual=$actual" }
}
Check 0.059 0 0 $true 'within tolerance'
Check 0.062 0.01854 0.00062 $true 'isolated 026-sized overrun'
Check 0.075 0.10 0.02 $true 'policy limits inclusive'
Check 0.062 0.11 0.004 $false 'widespread overrun'
Check 0.075 0.09 0.0225 $false 'large weighted excess'
Check 0.076 0.001 0.00027 $false 'severe isolated free edge'
Check 0 0 0 $true 'original protected edges only'
'GMSH_SIZE_POLICY=PASS LOCAL_OVERRUN=ACCEPTED DIFFUSE_AND_SEVERE=REJECTED'
