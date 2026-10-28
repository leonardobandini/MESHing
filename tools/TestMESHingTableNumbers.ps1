param([Parameter(Mandatory=$true)][string]$PluginPath)
$ErrorActionPreference = 'Stop'
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PluginPath).Path)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$format = $type.GetMethod('FormatSapTableNumber', $flags)
$windows = $type.GetMethod('SapTableDecimalSeparator', $flags)
foreach ($separator in @(',', '.')) {
    $text = [string]$format.Invoke($null, [object[]]@(1234.125, $separator))
    if ($text -cne ('1234' + $separator + '125')) { throw "Unexpected table number: $text" }
    $small = [string]$format.Invoke($null, [object[]]@(-0.00000125, $separator))
    $parsed = [double]::Parse($small.Replace(',', '.'),
        [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture)
    if ($parsed -ne -0.00000125) { throw "Lost coordinate precision: $small" }
}
$current = [string]$windows.Invoke($null, @())
if ($current -cne ',' -and $current -cne '.') { throw "Unsupported Windows separator: $current" }
"SAP_TABLE_NUMBERS=PASS comma,dot; Windows=$current"
