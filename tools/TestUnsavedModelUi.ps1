# SPDX-License-Identifier: GPL-2.0-or-later
# The first-save warning keeps the tabs/help visible while disabling mesh actions.
param([Parameter(Mandatory=$true)][string]$Plugin,
      [string]$ApiPath = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path $ApiPath
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class FEMeshFirstSaveWarning {
    public static volatile bool Seen;
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string caption);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static void Watch(string caption) {
        ThreadPool.QueueUserWorkItem(_ => {
            for (int i = 0; i < 120; i++) {
                IntPtr box = FindWindow("#32770", caption);
                if (box != IntPtr.Zero) {
                    Seen = true;
                    PostMessage(box, 0x0111, (IntPtr)1, IntPtr.Zero);
                    Thread.Sleep(400);
                    if (FindWindow("#32770", caption) == box)
                        PostMessage(box, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(100);
            }
        });
    }
}
'@
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$pluginType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
$axis = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
$axes = [Array]::CreateInstance($axis, 0)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$constructor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
    Where-Object { $_.GetParameters().Count -eq 6 })[0]
if (-not $constructor) { throw 'Mesh options constructor missing.' }
$oldManifest = $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON
$env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = '{}'
$dialog = $constructor.Invoke([object[]]@('m', $false, [string[]]@(), [string[]]@(), $axes, $true))
try {
    $dialog.Show()
    $dialog.SetSelectionCounts(1, 1, 0, 0, 0, 0, 0)
    $generate = $type.GetField('generate', $flags).GetValue($dialog)
    $undo = $type.GetField('undo', $flags).GetValue($dialog)
    $details = $type.GetField('details', $flags).GetValue($dialog)
    $tabs = $type.GetField('gmshPreviewTabs', $flags).GetValue($dialog)
    $status = $type.GetField('status', $flags).GetValue($dialog)
    $language = $type.GetField('language', $flags).GetValue($dialog)
    $dialog.SetRequiresSavedModel($true)
    if ($generate.Enabled -or $undo.Enabled -or $details.Enabled -or
        -not $tabs.Enabled -or -not $dialog.HelpButton -or
        $status.Text -notmatch 'Save this new model') {
        throw 'New unsaved model did not disable actions while keeping tabs/help available.'
    }
    $language.SelectedIndex = 1
    if ($generate.Enabled -or $status.Text -notmatch 'Salvare questo nuovo modello') {
        throw 'Language switch reenabled actions or hid the saved-model warning.'
    }
    $version = $pluginType.GetField('PluginVersion',
        [Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
    [FEMeshFirstSaveWarning]::Watch('MESHing ' + $version)
    $dialog.ShowNeverSavedModelWarning()
    if (-not [FEMeshFirstSaveWarning]::Seen) { throw 'No first-save warning appeared.' }
    $dialog.SetRequiresSavedModel($false)
    if (-not $generate.Enabled -or $undo.Enabled -or $details.Enabled) {
        throw 'First host save did not reenable Generate safely.'
    }
    'UNSAVED_MODEL_UI=PASS WARNING=SHOWN COMMANDS=DISABLED HELP=VISIBLE FIRST_SAVE=REENABLED'
} finally {
    $dialog.Close(); $dialog.Dispose()
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $oldManifest
}
