# The dialog contains only the retained metrics, not an overall verdict.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'),
      [string]$ScreenshotPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$parent = 'SapQuadTransitionPlugin.cPlugin+'
$reportType = $assembly.GetType($parent + 'MeshQualityReport', $true)
$metricType = $assembly.GetType($parent + 'QualityMetric', $true)
$dialogType = $assembly.GetType($parent + 'QualityReportDialog', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$summary = 'Elements: 1163 quads, 286 triangles. Outside shape references: 75 (1.883% of area). Size exceptions: 73 elements (6.997% of area). Invalid: 0.'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Inspect([bool]$passed, [double[]]$scores, [bool]$capture) {
    $report = $reportType.GetConstructors($flags)[0].Invoke([object[]]@($passed, $summary))
    $list = $reportType.GetField('Metrics', $flags).GetValue($report)
    foreach ($score in $scores) {
        $item = $metricType.GetConstructors($flags)[0].Invoke([object[]]@('Metric', 'value', 'reference', $score))
        [void]$list.Add($item)
    }
    [void]$list.Add($metricType.GetConstructors($flags)[0].Invoke(
        [object[]]@('Maximum size', '0.5', 'Hidden display metric', [double]1)))
    $dialog = $dialogType.GetConstructors($flags)[0].Invoke([object[]]@($report))
    try {
        $outcome = @($dialog.Controls | Where-Object { $_.Text -match '^(RESULT:|RISULTATO:)' })
        $label = @($dialog.Controls | Where-Object { $_ -is [Windows.Forms.Label] -and $_.Top -eq 124 })
        $panel = @($dialog.Controls | Where-Object { $_ -is [Windows.Forms.FlowLayoutPanel] })
        Assert ($outcome.Count -eq 0 -and $label.Count -eq 0 -and $panel.Count -eq 1 -and
            $panel[0].Top -eq 88 -and $panel[0].Height -ge 440 -and
            $report.Passed -eq $passed -and $report.Summary -eq $summary) `
            'Overall verdict and text summary must be hidden without changing the quality report.'
        Assert ($list.Count -eq $scores.Count + 1 -and $panel[0].Controls.Count -eq $scores.Count) `
            'Maximum size must remain in the quality report but be hidden in its window.'
        if ($ScreenshotPath -and $capture) {
            Assert ((Test-Path (Split-Path -Parent $ScreenshotPath) -PathType Container) -and
                -not (Test-Path $ScreenshotPath)) 'Unsafe screenshot destination.'
            $dialog.Show()
            [Windows.Forms.Application]::DoEvents()
            $image = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
            try {
                $dialog.DrawToBitmap($image, [Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
                $image.Save($ScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $image.Dispose() }
        }
    } finally { $dialog.Close(); $dialog.Dispose() }
}
Inspect $true ([double[]]@(0.899, 1, 0.867, 0.781, 1)) $true
Inspect $true ([double[]]@(0.899, 1, 0.6)) $false
Inspect $false ([double[]]@(1, 1)) $false
$assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true).GetField('useItalian',
    [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $true)
Inspect $true ([double[]]@(0.899, 1, 0.867, 0.781, 1)) $false
'V2_QUALITY_METRICS_ONLY=PASS QUALITY_REPORT_PRESERVED=PASS'
