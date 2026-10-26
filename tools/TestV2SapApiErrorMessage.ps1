# Verify failed SAP return codes retain the exact API and operation in EN/IT.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$pluginType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$snapshotType = $assembly.GetType('SapQuadTransitionPlugin.ModelCsvSnapshot', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$pluginCheck = $pluginType.GetMethod('Check', $flags)
$snapshotCheck = $snapshotType.GetMethod('Check', $flags)
$localize = $pluginType.GetMethod('LocalizeErrorMessage', $flags)
$italian = $pluginType.GetField('useItalian', $flags)
$wasItalian = $italian.GetValue($null)
function FailureMessage($method, $operation, $api) {
    try { $method.Invoke($null, [object[]]@(1, $operation, $api)) | Out-Null }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        return $cause.Message
    }
    throw 'A SAP2000 return code of 1 must fail.'
}
try {
    $italian.SetValue($null, $false)
    $point = FailureMessage $pluginCheck 'creating point' 'PointObj.AddCartesian'
    if ($point -ne 'SAP2000 error while creating point [API: PointObj.AddCartesian]: code 1' -or
        $localize.Invoke($null, [object[]]@($point)) -ne $point) {
        throw 'English SAP point API failure lost its operation or return code.'
    }
    $snapshot = FailureMessage $snapshotCheck 'restoring original area A1' 'AreaObj.ChangeName'
    $italian.SetValue($null, $true)
    foreach ($case in @(
        @($point, 'PointObj.AddCartesian', 'creating point'),
        @($snapshot, 'AreaObj.ChangeName', 'restoring original area A1'),
        @('Area A1: SAP2000 error while creating shell element 5 [API: AreaObj.AddByPoint]: code 1',
            'AreaObj.AddByPoint', 'Area A1')
    )) {
        $text = $localize.Invoke($null, [object[]]@([string]$case[0]))
        if ($text -notmatch '\(codice 1\)' -or -not $text.Contains($case[1]) -or
            -not $text.Contains($case[2])) {
            throw "Localized SAP error lost its API/operation: $text"
        }
    }
    'V2_SAP_CODE_1_API_CONTEXT=PASS EN_IT=PASS SNAPSHOT=PASS'
} finally { $italian.SetValue($null, $wasItalian) }
