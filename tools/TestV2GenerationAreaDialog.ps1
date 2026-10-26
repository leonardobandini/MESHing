# Exercise the actual generation window on its independent UI thread.
param([string]$Plugin = (Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher\SapQuadTransitionPlugin.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
Add-Type -ReferencedAssemblies 'System.Windows.Forms','System.Drawing','System.Core' -TypeDefinition @'
using System;
using System.Linq;
using System.Windows.Forms;
public static class MeshingAreaDialogProbe {
    public static string ReadArea(Form form) {
        return (string)form.Invoke(new Func<string>(() =>
            form.Controls.OfType<Label>().OrderBy(label => label.Top).Skip(1).First().Text));
    }
}
'@
$oldSize = $env:SAP_QUAD_ATOMIC_SIZE
$oldManifest = $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON
$env:SAP_QUAD_ATOMIC_SIZE = '0.1m'
$env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = '{}'
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
    $axis = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
    $ctor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
        Where-Object { $_.GetParameters().Count -eq 6 })[0]
    $dialog = $ctor.Invoke([object[]]@('m', $false, [string[]]@(),
        [string[]]@(), [Array]::CreateInstance($axis, 0), $true))
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    try {
        $dialog.Show()
        $dialog.BeginWork('Preparing geometry...')
        $controller = $type.GetField('cancellationController', $flags).GetValue($dialog)
        $windowField = $controller.GetType().GetField('window', $flags)
        $deadline = (Get-Date).AddSeconds(5)
        $window = $null
        while ((Get-Date) -lt $deadline) {
            [Windows.Forms.Application]::DoEvents()
            $window = $windowField.GetValue($controller)
            if ($window -and $window.IsHandleCreated) { break }
            Start-Sleep -Milliseconds 40
        }
        if (-not $window -or -not $window.IsHandleCreated) { throw 'The generation window did not open.' }
        function ExpectArea($expected) {
            $deadline = (Get-Date).AddSeconds(3)
            do {
                [Windows.Forms.Application]::DoEvents()
                $actual = [MeshingAreaDialogProbe]::ReadArea($window)
                if ($actual -match ('^' + $expected + '$')) { return }
                Start-Sleep -Milliseconds 40
            } while ((Get-Date) -lt $deadline)
            throw "Expected area label '$expected', got '$actual'."
        }
        $dialog.SetMeshingAreas(0, 8)
        ExpectArea 'Area 1/8'
        $dialog.SetMeshingAreas(1, 8)
        ExpectArea 'Area 2/8'
        $dialog.SetProgress('Creating SAP2000 points...', 1, 4)
        ExpectArea 'Area 2/8'
        $dialog.ShowBatchAreas(8)
        ExpectArea 'Gmsh: areas 1.8/8 together'
        $dialog.ShowBatchAreas(8, $true)
        ExpectArea 'SAP2000 import: areas 1.8/8 together'
        $dialog.SetMeshingAreas(2, 8)
        ExpectArea 'Area 3/8'
        'GENERATION_MODAL_AREA=PASS AREA=1/8,2/8,3/8 GMSH_BATCH=PASS SAP_BATCH=PASS'
    }
    finally {
        $dialog.Cancelled()
        $dialog.Close()
        $dialog.Dispose()
    }
}
finally {
    $env:SAP_QUAD_ATOMIC_SIZE = $oldSize
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $oldManifest
}
