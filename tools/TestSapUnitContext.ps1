param([Parameter(Mandatory = $true)][string]$DllPath)

$ErrorActionPreference = 'Stop'
$api = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
[Reflection.Assembly]::LoadFrom($api) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
$plugin = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
if ($null -ne $plugin.GetMethod('RequiresDatabaseUnitScope', [Reflection.BindingFlags]'NonPublic,Static')) {
    throw 'Model-wide unit normalization must not be present.'
}
$snapshot = $assembly.GetType('SapQuadTransitionPlugin.ModelCsvSnapshot', $true)
$method = $snapshot.GetMethod('ConvertSnapshotTemperature', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $method) { throw 'Temperature conversion method not found.' }
$c = [CSiAPIv1.eTemperature]::C
$f = [CSiAPIv1.eTemperature]::F
$cases = @(
    @(26.5, $c, $c, 26.5),
    @(26.5, $f, $f, 26.5),
    @(-3.0555555555555554, $c, $f, 26.5),
    @(26.5, $f, $c, -3.0555555555555554)
)
foreach ($case in $cases) {
    $actual = [double]$method.Invoke($null, [object[]]@($case[0], $case[1], $case[2]))
    if ([math]::Abs($actual - [double]$case[3]) -gt 1e-9) {
        throw "Incorrect temperature conversion from $($case[1]) to $($case[2]): $actual"
    }
}
Write-Host 'SAP_UNITS_NO_GLOBAL_SCOPE_AND_TEMPERATURE_CONVERSION=PASS'
