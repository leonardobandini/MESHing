# Capture the shipped dialog, in both languages, with numbered callouts.
# Uses a UI-only demonstration selection; it never starts SAP2000 or opens a model.
param([Parameter(Mandatory=$true)][string]$Plugin, [string]$Assets = '',
      [string[]]$CaptureTabs = @('gmsh','additional','transfer'),
      [string]$ImageVersion = 'v259')
$ErrorActionPreference = 'Stop'
if (-not $Assets) { $Assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\help-gmsh-v2\assets' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
if (-not (Test-Path -LiteralPath $Assets -PathType Container) -or
    -not (Test-Path -LiteralPath $Plugin -PathType Leaf)) { throw 'Missing plugin or documentation assets directory.' }
$oldSize = $env:SAP_QUAD_ATOMIC_SIZE
$oldSettings = $env:SAP_GMSH_V2_SETTINGS_PATH
$oldUpdate = $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON
$env:SAP_QUAD_ATOMIC_SIZE = '0.1m'
$env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $env:TEMP ('opencode\sqm-help-' + [Guid]::NewGuid().ToString('N') + '.json')
$env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = '{}'
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
    $axisType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    function Field($object, $name) { $object.GetType().GetField($name, $flags).GetValue($object) }
    $ctor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
        Where-Object { $_.GetParameters().Count -eq 6 })[0]
    $axes = [Array]::CreateInstance($axisType, 1)
    $axisCtor = @($axisType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
        Where-Object { $_.GetParameters().Count -eq 3 -and $_.GetParameters()[2].ParameterType -eq [int] })[0]
    $axes.SetValue($axisCtor.Invoke([object[]]@([double[]]@(0,0,0), [double[]]@(1,0,0), [int]1)), 0)
    $dialog = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), $axes, $true))
    try {
        $dialog.Show()
        [Windows.Forms.Application]::DoEvents()
        $dialog.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
            [string[]]@('BOLT'), [string[]]@('ALL'), $axes)
        $tabs = Field $dialog 'gmshPreviewTabs'
        $panel = Field $dialog 'gmshPreviewOptions'
        $general = Field $dialog 'gmshPreviewEmptyTab'
        $additional = Field $dialog 'gmshHolesTab'
        $transfer = Field $dialog 'gmshSapTab'
        (Field $dialog 'generate').Enabled = $false
        $cases = @(
            @{ slug='gmsh'; page=$general; controls=@((Field $panel 'maxSize'), (Field $panel 'algorithm'),
                (Field $panel 'smoothingSteps'), (Field $panel 'recombinationMethod'), (Field $panel 'optimizer')) },
            @{ slug='additional'; page=$additional; controls=@((Field $panel 'favorRegularGrid'),
                (Field $dialog 'gmshPreserveSelectedEdges'), (Field $dialog 'symmetric'),
                (Field $dialog 'generateCircularHoles'), (Field $dialog 'generateHoles')) },
            @{ slug='transfer'; page=$transfer; controls=@((Field $dialog 'fastMode'), (Field $dialog 'createBackup')) }
        )
        for ($language = 0; $language -lt 2; $language++) {
            (Field $dialog 'language').SelectedIndex = $language
            foreach ($item in $cases) {
                if ($CaptureTabs -notcontains $item.slug) { continue }
                $tabs.SelectedTab = $item.page
                [Windows.Forms.Application]::DoEvents()
                if ($item.page -eq $additional -and $additional.VerticalScroll.Visible) {
                    throw 'Additional Options does not fit the standard dialog.'
                }
                $name = 'interface-' + $ImageVersion + '-' + $(if ($language) { 'it-' } else { 'en-' }) + $item.slug + '.png'
                $destination = Join-Path $Assets $name
                if (Test-Path -LiteralPath $destination) { throw "Refusing to overwrite UI evidence: $destination" }
                $bitmap = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
                try {
                    $dialog.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                    $graphics = [Drawing.Graphics]::FromImage($bitmap)
                    $font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
                    $format = [Drawing.StringFormat]::new()
                    $format.Alignment = [Drawing.StringAlignment]::Center
                    $format.LineAlignment = [Drawing.StringAlignment]::Center
                    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(22, 83, 181))
                    try {
                        for ($index = 0; $index -lt $item.controls.Count; $index++) {
                            $control = $item.controls[$index]
                            $y = $control.PointToScreen([Drawing.Point]::Empty).Y - $dialog.Location.Y + 8
                            $bounds = [Drawing.RectangleF]::new(526, [single]$y, 22, 22)
                            $graphics.FillEllipse($brush, $bounds)
                            $graphics.DrawString([string]($index + 1), $font, [Drawing.Brushes]::White, $bounds, $format)
                        }
                        $bitmap.Save($destination, [Drawing.Imaging.ImageFormat]::Png)
                    }
                    finally {
                        $brush.Dispose(); $format.Dispose(); $font.Dispose(); $graphics.Dispose()
                    }
                }
                finally { $bitmap.Dispose() }
                "HELP_SCREENSHOT=$destination"
            }
        }
    }
    finally { $dialog.Close(); $dialog.Dispose() }
}
finally {
    $env:SAP_QUAD_ATOMIC_SIZE = $oldSize
    $env:SAP_GMSH_V2_SETTINGS_PATH = $oldSettings
    $env:SAP_QUAD_TEST_UPDATE_MANIFEST_JSON = $oldUpdate
}
