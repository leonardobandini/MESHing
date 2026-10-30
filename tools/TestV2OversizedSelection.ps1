# Verify that the interactive dialog cannot Generate on Select All and rearms on a small selection.
param([string]$Plugin=(Join-Path $PSScriptRoot '..\SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$old=$env:SAP_GMSH_V2_SETTINGS_PATH
$env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $env:TEMP ('oversized-selection-'+[guid]::NewGuid().ToString('N')+'.json')
try {
    $assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $type=$assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog',$true)
    $axis=$assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis',$true)
    $ctor=@($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
        Where-Object {$_.GetParameters().Count -eq 6})[0]
    $dialog=$ctor.Invoke([object[]]@('m',$false,[string[]]@(),[string[]]@(),
        [Array]::CreateInstance($axis,0),$true))
    try {
        $dialog.Show()
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $generate=$type.GetField('generate',$flags).GetValue($dialog)
        $status=$type.GetField('status',$flags).GetValue($dialog)
        $dialog.UpdateOversizedSelection(23823,272,0,24748,500)
        if($generate.Enabled -or $status.Text -notmatch '23823' -or $status.Text -notmatch '500'){
            throw "Oversized selection was not blocked: $($status.Text)"
        }
        $dialog.UpdateLiveSelection(3,3,0,0,0,0,0,$false,[string[]]@(),
            [string[]]@(),[Array]::CreateInstance($axis,0))
        if(-not $generate.Enabled){throw 'Generate did not recover on a small selection.'}
        'OVERSIZED_SELECTION_UI=PASS BLOCKED=23823 REARMED=3'
    } finally {$dialog.Close();$dialog.Dispose()}
} finally {$env:SAP_GMSH_V2_SETTINGS_PATH=$old}
