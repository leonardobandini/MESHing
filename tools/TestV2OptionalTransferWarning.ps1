# A SAP return code 1 on an optional property is recorded, not a mesh failure.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
Add-Type -TypeDefinition @'
using System;
public static class SapOptionalTransferProbe {
    public static Action<string> FailedModifier = target => {
        throw new InvalidOperationException("SAP2000 error while transferring area stiffness modifiers to " +
            target + " from A1 [API: AreaObj.SetModifiers]: code 1");
    };
}
'@
$oldSize = $env:SAP_QUAD_ATOMIC_SIZE
$env:SAP_QUAD_ATOMIC_SIZE = '0.2m'
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $dialogType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
    $axisType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
    $loadsType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+AreaLoads', $true)
    $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    $ctor = @($dialogType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 6 })[0]
    $dialog = $ctor.Invoke([object[]]@('m', $false, [string[]]@(), [string[]]@(),
        [Array]::CreateInstance($axisType, 0), $true))
    try {
        $optional = $dialogType.GetMethod('CheckOptionalTransfer', $flags)
        if ($optional.Invoke($dialog, [object[]]@(1, 'A1', 'QTA1', 'local axes', 'AreaObj.SetLocalAxes')) -ne $false -or
            $optional.Invoke($dialog, [object[]]@(0, 'A1', 'QTA1', 'area mass', 'AreaObj.SetMass')) -ne $true) {
            throw 'Optional property return codes were not classified correctly.'
        }
        $blocked = $false
        try { $optional.Invoke($dialog, [object[]]@(2, 'A1', 'QTA1', 'area mass', 'AreaObj.SetMass')) | Out-Null }
        catch { $blocked = $_.Exception.ToString() -match 'AreaObj.SetMass.*code 2' }
        if (-not $blocked) { throw 'An unexpected SAP return code must remain fatal.' }
        $loads = $loadsType.GetConstructors($flags)[0].Invoke([object[]]@('A1'))
        $script:laterAssignment = 0
        $loadsType.GetMethod('Add', $flags).Invoke($loads,
            [object[]]@([SapOptionalTransferProbe]::FailedModifier)) | Out-Null
        $loadsType.GetMethod('Add', $flags).Invoke($loads, [object[]]@([Action[string]]{
            param($target)
            $script:laterAssignment++
        })) | Out-Null
        foreach ($target in @('QTA1', 'QTA2')) {
            $loadsType.GetMethod('Apply', $flags).Invoke($loads,
                [object[]]@($null, $target, [string[]]@(), $dialog)) | Out-Null
        }
        $warnings = $dialogType.GetField('transferWarnings', $flags).GetValue($dialog)
        $text = @($warnings.Keys) -join '; '
        if ($script:laterAssignment -ne 2 -or $warnings.Count -ne 2 -or
            $text -notmatch 'AreaObj.SetLocalAxes' -or $text -notmatch 'AreaObj.SetModifiers' -or
            @($warnings.Values | Where-Object { $_.Count -eq 2 }).Count -ne 1) {
            throw "Optional assignment warning or continuation failed: later=$script:laterAssignment warnings=$text"
        }
        $verify = $optional.Invoke($dialog, [object[]]@(1, 'A1', 'QTA3',
            'verification of thickness overrides', 'AreaObj.GetThickness'))
        $readFailure = [InvalidOperationException]::new(
            'SAP2000 error while reading table Area Design Procedures [API: DatabaseTables.GetTableForEditingArray]: code 1')
        $warn = $dialogType.GetMethod('WarnOptionalApiFailure', $flags).Invoke($dialog,
            [object[]]@($readFailure, 'A1', 'QTA3', 'area design procedure transfer or verification'))
        if ($verify -ne $false -or $warn -ne $true -or $warnings.Count -ne 4) {
            throw 'Optional property verification and design procedure read errors must be warnings.'
        }
        'V2_OPTIONAL_PROPERTY_CODE_1=WARNING MESH_CONTINUES=PASS OTHER_CODES_FATAL=PASS'
    } finally { $dialog.Dispose() }
} finally { $env:SAP_QUAD_ATOMIC_SIZE = $oldSize }
