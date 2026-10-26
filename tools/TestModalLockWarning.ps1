# SPDX-License-Identifier: GPL-2.0-or-later
# Verify the lock warning blocks SAP's window and an already open SAP dialog.
param([Parameter(Mandatory=$true)][string]$Plugin,
      [string]$ApiPath = 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path $ApiPath
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class FEMeshWarningWindows {
    public static IntPtr HostWindow, ConfirmationWindow;
    public static volatile bool Seen, Observed, Dismissed;
    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string caption);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool WindowEnumerator(IntPtr window, IntPtr unused);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumerator callback, IntPtr unused);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    public static string Titles() {
        StringBuilder titles = new StringBuilder();
        EnumWindows((window, unused) => {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            StringBuilder title = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) > 0) titles.Append('[').Append(title).Append(']');
            return true;
        }, IntPtr.Zero);
        return titles.ToString();
    }
    public static IntPtr FindTitle(string caption) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, unused) => {
            StringBuilder title = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) > 0 && title.ToString() == caption) {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void Watch(string caption) {
        ThreadPool.QueueUserWorkItem(_ => {
            for (int attempt = 0; attempt < 120; attempt++) {
                IntPtr box = FindWindow("#32770", caption);
                if (box != IntPtr.Zero) {
                    Seen = true;
                    Observed = !IsWindowEnabled(HostWindow) && !IsWindowEnabled(ConfirmationWindow);
                    Dismissed = PostMessage(box, 0x0111, (IntPtr)1, IntPtr.Zero);
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
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$version = $pluginType.GetField('PluginVersion', $flags).GetRawConstantValue()
$constructor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
    Where-Object { $_.GetParameters().Count -eq 6 })[0]
if (-not $constructor) { throw 'Mesh options constructor missing.' }
$oldManifest = $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON
$env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = '{}'
$hostWindow = New-Object Windows.Forms.Form
$hostWindow.Text = 'SAP2000 test host'
$lockButton = New-Object Windows.Forms.Button
$lockButton.Text = 'SAP2000 lock button'
$lockButton.SetBounds(10, 10, 160, 32)
$hostWindow.Controls.Add($lockButton)
$panButton = New-Object Windows.Forms.Button
$panButton.Text = 'Pan / zoom'
$panButton.SetBounds(10, 52, 160, 32)
$hostWindow.Controls.Add($panButton)
$script:lockClicks = 0
$script:panClicks = 0
$script:warningDiagnostic = ''
$lockButton.Add_Click({
    # PerformClick bypasses the mouse hit test. A real click cannot reach a
    # disabled SAP window; keep the simulated command subject to the same gate.
    if (-not [FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle)) { return }
    $script:lockClicks++
    $script:dialog.ReportModelLockViolation($script:warningDiagnostic)
})
$panButton.Add_Click({ $script:panClicks++ })
$confirmation = New-Object Windows.Forms.Form
$confirmation.Text = 'SAP2000 test unlock confirmation'
$dialog = $null
try {
    $hostWindow.Show()
    $confirmation.Show()
    $dialog = $constructor.Invoke([object[]]@('m', $false, [string[]]@(), [string[]]@(), $axes, $true))
    $script:dialog = $dialog
    $dialog.Show()
    $dialog.AllowViewportInteraction($hostWindow.Handle)
    [FEMeshWarningWindows]::HostWindow = $hostWindow.Handle
    [FEMeshWarningWindows]::ConfirmationWindow = $confirmation.Handle
    $instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
    $generate = $type.GetField('generate', $instanceFlags).GetValue($dialog)
    $undo = $type.GetField('undo', $instanceFlags).GetValue($dialog)
    $details = $type.GetField('details', $instanceFlags).GetValue($dialog)
    $status = $type.GetField('status', $instanceFlags).GetValue($dialog)
    $progressTitle = if ($pluginType.GetField('useItalian', $flags).GetValue($null)) {
        'MESHing - Generazione'
    } else { 'MESHing - Generation' }
    $dialog.BlockHostForOperation()
    try {
        $dialog.BeginWork('Preparing geometry...')
        $progressWindow = [IntPtr]::Zero
        for ($wait = 0; $wait -lt 80 -and $progressWindow -eq [IntPtr]::Zero; $wait++) {
            Start-Sleep -Milliseconds 100
            [Windows.Forms.Application]::DoEvents()
            $progressWindow = [FEMeshWarningWindows]::FindTitle($progressTitle)
        }
        if ($progressWindow -eq [IntPtr]::Zero -or
            -not [FEMeshWarningWindows]::IsWindowEnabled($progressWindow) -or
            [FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle) -or $generate.Enabled) {
            throw "Generate progress did not keep SAP2000 blocked while allowing cancellation: progress=$progressWindow title=$progressTitle progressEnabled=$([FEMeshWarningWindows]::IsWindowEnabled($progressWindow)) hostEnabled=$([FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle)) generateEnabled=$($generate.Enabled) visible=$([FEMeshWarningWindows]::Titles())."
        }
        $beforeClick = $script:lockClicks
        $lockButton.PerformClick()
        if ([FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle) -or
            $script:lockClicks -ne $beforeClick) {
            throw 'The lock button reacted to a simulated click during Generate.'
        }
        $dialog.Cancelled()
    } finally { $dialog.UnblockHostAfterOperation() }
    if (-not [FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle)) {
        throw 'Generate progress left the host disabled after cancellation.'
    }
    $phases = @(
        @{ Name='just-opened'; Clicks=1; Generate='Generate'; GenerateEnabled=$true; UndoEnabled=$false; DetailsEnabled=$false },
        @{ Name='preview'; Clicks=3; Generate='Apply'; GenerateEnabled=$true; UndoEnabled=$true; DetailsEnabled=$true },
        @{ Name='applied'; Clicks=1; Generate='Generate'; GenerateEnabled=$false; UndoEnabled=$false; DetailsEnabled=$true },
        @{ Name='after-undo'; Clicks=1; Generate='Generate'; GenerateEnabled=$true; UndoEnabled=$false; DetailsEnabled=$false }
    )
    foreach ($phase in $phases) {
        $generate.Text = $phase.Generate
        $generate.Enabled = $phase.GenerateEnabled
        $undo.Enabled = $phase.UndoEnabled
        $details.Enabled = $phase.DetailsEnabled
        $status.Text = "MESHing phase: $($phase.Name)"
        for ($attempt = 1; $attempt -le $phase.Clicks; $attempt++) {
            [FEMeshWarningWindows]::Seen = [FEMeshWarningWindows]::Observed =
                [FEMeshWarningWindows]::Dismissed = $false
            [FEMeshWarningWindows]::Watch('MESHing ' + $version)
            $script:warningDiagnostic = "Simulated lock-button click: $($phase.Name) #$attempt"
            $beforeClick = $script:lockClicks
            $lockButton.PerformClick()
            if ($script:lockClicks -ne $beforeClick + 1) {
                throw "The enabled lock button did not react during $($phase.Name)/$attempt."
            }
            if (-not [FEMeshWarningWindows]::Seen -or -not [FEMeshWarningWindows]::Observed -or
                -not [FEMeshWarningWindows]::Dismissed) {
                throw "Warning $($phase.Name)/$attempt did not block the host/confirmation or could not be dismissed."
            }
            if (-not [FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle) -or
                -not [FEMeshWarningWindows]::IsWindowEnabled($confirmation.Handle)) {
                throw "SAP windows remained disabled after warning $($phase.Name)/$attempt."
            }
            if ($generate.Enabled -ne $phase.GenerateEnabled -or $undo.Enabled -ne $phase.UndoEnabled -or
                $details.Enabled -ne $phase.DetailsEnabled -or $generate.Text -ne $phase.Generate -or
                $status.Text -ne "MESHing phase: $($phase.Name)" -or $dialog.UndoFailure -ne $null -or
                -not $dialog.Visible) {
                throw "Warning $($phase.Name)/$attempt altered MESHing's current phase."
            }
            $beforePan = $script:panClicks
            $panButton.PerformClick()
            if ($script:panClicks -ne $beforePan + 1) {
                throw "Pan/zoom was not available after OK during $($phase.Name)/$attempt."
            }
        }
    }
    $dialog.BlockHostForOperation()
    try {
        if ([FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle)) {
            throw 'Generate did not block host interaction.'
        }
        foreach ($applying in @($true,$false)) {
            $progress = $type.GetMethod('ShowCloseProgress', [Reflection.BindingFlags]'Instance,Public,NonPublic').Invoke($dialog,[object[]]@($applying))
            try {
                if ([FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle) -or
                    $dialog.Enabled -or -not $progress.Visible -or -not $progress.Enabled) {
                    throw 'Apply/Undo progress did not keep both the host and options window modal.'
                }
                $beforeClick = $script:lockClicks
                $lockButton.PerformClick()
                if ([FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle) -or
                    $script:lockClicks -ne $beforeClick) {
                    throw 'The lock button reacted to a simulated click during Apply/Undo progress.'
                }
            } finally {
                $progress.Tag = $true
                $progress.Close(); $progress.Dispose()
            }
        }
        if (-not $dialog.Enabled) { throw 'The options window remained disabled after progress finished.' }
    } finally { $dialog.UnblockHostAfterOperation() }
    if (-not [FEMeshWarningWindows]::IsWindowEnabled($hostWindow.Handle)) {
        throw 'Host interaction was not restored after the progress window.'
    }
    'MODAL_LOCK_WARNING=PASS PHASES=OPEN_GENERATE_PREVIEW_APPLY_UNDO LOCK_CLICKS_BLOCKED_DURING_PROGRESS=3 LOCK_WARNINGS=6 PAN_ZOOM_AFTER_OK=PASS HOST_AND_CONFIRMATION_BLOCKED=YES PREVIEW_PRESERVED=YES'
} finally {
    if ($dialog) { $dialog.Close(); $dialog.Dispose() }
    $confirmation.Close(); $confirmation.Dispose()
    $hostWindow.Close(); $hostWindow.Dispose()
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $oldManifest
}
