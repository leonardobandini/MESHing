param([Parameter(Mandatory = $true)][string]$DllPath)

$ErrorActionPreference = 'Stop'
$api = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
[Reflection.Assembly]::LoadFrom($api) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
$plugin = $assembly.GetType('SapQuadTransitionPlugin.cPlugin')
$method = $plugin.GetMethod('RequiresDatabaseUnitScope', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $method) { throw 'Unit-context decision method not found.' }

$mm = [CSiAPIv1.eUnits]::kN_mm_C
$m = [CSiAPIv1.eUnits]::kN_m_C
$cases = @(
    @('27.1.0', 27.1, $m, $mm, $false),
    @('27.2.0', 27.2, $mm, $mm, $false),
    @('27.2.0', 27.2, $m, $mm, $true),
    @('27.10.0', 27.1, $m, $mm, $true),
    @('28.0.0', 28.0, $m, $mm, $true),
    @('', 27.2, $m, $mm, $true)
)
foreach ($case in $cases) {
    $actual = [bool]$method.Invoke($null, [object[]]@($case[0], $case[1], $case[2], $case[3]))
    if ($actual -ne $case[4]) {
        throw "Unexpected unit scope for SAP $($case[0]), present=$($case[2]), database=$($case[3]): $actual"
    }
}
Write-Host 'SAP unit-context policy: pre-27.2 compatibility and 27.2+ normalization PASS'
