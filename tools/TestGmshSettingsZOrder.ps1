# Exercise both settings dialogs with the same TopMost parent used by SAP2000.
param([Parameter(Mandatory=$true)][string]$PluginPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class WindowState { [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr handle); }'
$env:SAP_QUAD_ATOMIC_SIZE = '0.55m'
$env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = '{}'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PluginPath).Path)
$type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
$axis = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
$constructor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
    Where-Object { $_.GetParameters().Count -eq 6 })[0]
$parent = $constructor.Invoke([object[]]@('mm', $true, [string[]]@(),
    [string[]]@(), [Array]::CreateInstance($axis, 0), $true))
$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 150
$step = 0
$checks = 0
try {
    $parent.TopMost = $true
    $parent.ShowInTaskbar = $true
    $panel = $type.GetField('gmshPreviewOptions', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($parent)
    $panelType = $panel.GetType()
    $algorithm = $panelType.GetProperty('Algorithm', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($panel)
    $algorithmButton = $panelType.GetProperty('AlgorithmSettings', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($panel)
    $optimizer = $panelType.GetProperty('Optimizer', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($panel)
    $optimizationButton = $panelType.GetProperty('OptimizationSettings', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($panel)
    $timer.Add_Tick({
        try {
            $child = @([Windows.Forms.Application]::OpenForms | Where-Object { $_ -ne $parent -and $_.Owner -eq $parent }) | Select-Object -First 1
            if ($child) {
                if (-not $child.Visible -or -not $child.TopMost -or -not $child.Modal -or
                    [WindowState]::IsWindowEnabled($parent.Handle)) {
                    throw "Settings window not visible, topmost, and modal with disabled parent: $($child.Text)"
                }
                $script:checks++
                $child.DialogResult = if ($script:step -eq 1) { [Windows.Forms.DialogResult]::Cancel } else { [Windows.Forms.DialogResult]::OK }
                $child.Close()
                $script:step++
            } elseif ($script:step -eq 0) {
                $algorithm.SelectedIndex = 0 # MeshAdapt
                if (-not $algorithmButton.Enabled) { throw 'MeshAdapt settings button is disabled.' }
                $script:step = 1
                $algorithmButton.PerformClick()
            } elseif ($script:step -eq 2) {
                $algorithm.SelectedIndex = 4 # BAMG
                $script:step = 3
                $algorithmButton.PerformClick()
            } elseif ($script:step -eq 4) {
                $optimizer.SelectedIndex = 2 # QuadQuasiStructured
                if (-not $optimizationButton.Enabled) { throw 'Optimization settings button is disabled.' }
                $script:step = 5
                $optimizationButton.PerformClick()
            } elseif ($script:step -eq 6) {
                $timer.Stop()
                $parent.Close()
            }
        } catch {
            $script:errorMessage = $_.Exception.Message
            $timer.Stop()
            $parent.Close()
        }
    })
    $timer.Start()
    [void]$parent.ShowDialog()
    if ($errorMessage) { throw $errorMessage }
    if ($checks -ne 3) { throw "Only $checks/3 settings dialogs verified." }
    "SETTINGS_ZORDER=PASS dialogs=$checks (MeshAdapt Cancel, BAMG OK, optimizer OK)"
} finally {
    $timer.Dispose()
    $parent.Dispose()
}
