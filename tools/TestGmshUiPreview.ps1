# Validate the shipped UI, size persistence and option reset behavior.
param([string]$Plugin = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'),
       [string]$ScreenshotPath = '', [string]$GmshScreenshotPath = '',
       [string]$ItalianScreenshotPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
$oldSize = $env:SAP_QUAD_ATOMIC_SIZE
$oldSettings = $env:SAP_GMSH_V2_SETTINGS_PATH
$oldCloseUndo = $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED
$oldCloseApply = $env:SAP_QUAD_ATOMIC_CLOSE_APPLY
$oldUiCulture = [Threading.Thread]::CurrentThread.CurrentUICulture
[Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('en-US')
$env:SAP_QUAD_ATOMIC_SIZE = '0.55m'
$env:SAP_GMSH_V2_SETTINGS_PATH = Join-Path $env:TEMP ('opencode\sqm-v2-ui-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
    $type = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog', $true)
    $axis = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+SymmetryAxis', $true)
    $ctor = @($type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
        Where-Object { $_.GetParameters().Count -eq 6 })[0]
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    function Field($dialog, $name) { $type.GetField($name, $flags).GetValue($dialog) }
    function Assert($condition, $message) { if (-not $condition) { throw $message } }
    $dialog = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        $dialog.Show()
        [Windows.Forms.Application]::DoEvents()
        $subtitle = Field $dialog 'headerSubtitle'
        $headerTitle = Field $dialog 'headerTitle'
        $languageChoice = Field $dialog 'language'
        $companyLogo = @($dialog.Controls | Where-Object {
            $_ -is [Windows.Forms.PictureBox] -and $_.Left -eq 20 -and $_.Top -eq 708
        })
        $sourceLink = @($dialog.Controls | Where-Object {
            $_ -is [Windows.Forms.LinkLabel] -and $_.Tag -eq 'license-link'
        })
        Assert ($dialog.Text -eq 'MESHing 3.0.5' -and $headerTitle.Text -eq 'MESHing' -and
            $headerTitle.Left -eq 22 -and $headerTitle.Top -eq 13 -and
            $dialog.ClientSize.Width -eq 570 -and $dialog.ClientSize.Height -eq 762 -and
            $companyLogo.Count -eq 1 -and $companyLogo[0].Width -eq 46 -and
            $companyLogo[0].Height -eq 42 -and $companyLogo[0].Cursor -eq [Windows.Forms.Cursors]::Hand -and
            $sourceLink.Count -eq 1 -and $sourceLink[0].Left -eq 78 -and
            $sourceLink[0].Width -eq 96) `
            'The MESHing title, retained layout, CSI Italia logo or GPL / Source link changed.'
        Assert ($subtitle.Text -eq 'Advanced 2D mesh generation' -and
            $subtitle.Bottom -le $subtitle.Parent.Height -and
            $languageChoice.Visible -and $languageChoice.Parent -eq $subtitle.Parent -and
            $subtitle.Right + 10 -le $languageChoice.Left -and
            [Windows.Forms.TextRenderer]::MeasureText($subtitle.Text, $subtitle.Font,
                [Drawing.Size]::new($subtitle.Width, 100),
                [Windows.Forms.TextFormatFlags]::WordBreak).Height -le $subtitle.Height + 5) `
            'MESHing subtitle is missing or clipped.'
        $tabs = Field $dialog 'gmshPreviewTabs'
        $page = Field $dialog 'gmshPreviewEmptyTab'
        $panel = Field $dialog 'gmshPreviewOptions'
        $panelType = $panel.GetType()
        function Preview($name) { $panelType.GetField($name, $flags).GetValue($panel) }
        Assert ($type.GetField('gmshTabs', $flags) -eq $null -and
            $type.GetField('gmshOptionTree', $flags) -eq $null -and
            $type.GetField('gmshStructureTab', $flags) -eq $null -and
            $tabs.Visible -and $tabs.TabPages.Count -eq 3 -and
            (@($tabs.TabPages | ForEach-Object Text) -join ',') -eq
            'Gmsh,Additional options,Transfer options' -and
            -not $tabs.TabPages.Contains((Field $dialog 'gmshAdvancedTab'))) `
            'The shipped interface must have three tabs and no debug JSON tab.'
        foreach ($name in @('profiles','size','edgeTolerance','strictMaximumSize',
            'preferRegular','stressRefinement','structuredSectorBands','useMaximumCores','workerLimit')) {
            Assert ((Field $dialog $name).Parent -eq $null) "Hidden legacy control $name is still attached to the window."
        }
        Assert (-not $type.GetField('edgeToleranceLabel', $flags) -and
            -not (Field $dialog 'measurementToolTip').GetToolTip((Field $dialog 'edgeTolerance'))) `
            'Legacy tolerance still has an active label or tooltip.'
        $additional = Field $dialog 'gmshHolesTab'
        $tabs.SelectedTab = $additional
        $additional.AutoScrollPosition = [Drawing.Point]::Empty
        [Windows.Forms.Application]::DoEvents()
        $viewport = $additional.RectangleToScreen($additional.ClientRectangle)
        $symmetry = Field $dialog 'symmetric'
        $axisChoice = Field $dialog 'symmetryAxis'
        $hint = Field $dialog 'gmshSymmetryHint'
        $regular = Preview 'favorRegularGrid'
        $preserveEdges = Field $dialog 'gmshPreserveSelectedEdges'
        $axisLabel = Field $dialog 'gmshSymmetryAxisLabel'
        $openingGroup = Field $dialog 'openingGroup'
        $holeGroup = Field $dialog 'holeGroup'
        $circularHoles = Field $dialog 'generateCircularHoles'
        $openings = Field $dialog 'generateHoles'
        $oGrid = Field $dialog 'gmshOGrid'
        foreach ($check in @($regular,$preserveEdges,$symmetry,$circularHoles,$oGrid,$openings,
            (Field $dialog 'fastMode'),(Field $dialog 'createBackup'))) {
            Assert ($check.FlatStyle -eq [Windows.Forms.FlatStyle]::Standard) `
                "Checkbox $($check.Name) does not use the regular-grid native blue check style."
        }
        Assert ($additional.AutoScroll -and -not $additional.VerticalScroll.Visible -and
            -not $additional.HorizontalScroll.Visible -and
            $regular.Parent -eq $additional -and -not $regular.Checked -and
            $preserveEdges.Parent -eq $additional -and -not $preserveEdges.Checked -and
            $regular.Top -lt $preserveEdges.Top -and $preserveEdges.Top -lt $symmetry.Top -and
            $symmetry.Parent -eq $additional -and $axisChoice.Parent -eq $additional -and
            $regular.Top -lt $symmetry.Top -and
            $axisLabel.Parent -eq $additional -and $axisLabel.Top -lt $axisChoice.Top + 5 -and
            $axisChoice.Left -eq $holeGroup.Left -and $axisChoice.Width -eq $holeGroup.Width -and
            $axisChoice.Left -eq $openingGroup.Left -and $axisChoice.Width -eq $openingGroup.Width -and
            $symmetry.Top -lt (Field $dialog 'generateCircularHoles').Top -and
            $viewport.Contains($regular.RectangleToScreen($regular.ClientRectangle)) -and
            $viewport.Contains($preserveEdges.RectangleToScreen($preserveEdges.ClientRectangle)) -and
            $viewport.Contains($symmetry.RectangleToScreen($symmetry.ClientRectangle)) -and
            $viewport.Contains($axisChoice.RectangleToScreen($axisChoice.ClientRectangle)) -and
            $viewport.Contains($hint.RectangleToScreen($hint.ClientRectangle)) -and
            $viewport.Contains($openingGroup.RectangleToScreen($openingGroup.ClientRectangle)) -and
            -not $symmetry.Enabled -and $hint.Text -match 'No axis of simmetry') `
            'All Additional Options must fit without scrolling, including regular grid and symmetry.'
        Assert ($regular.Text -eq 'Prefer regular (grid-aligned) mesh when possible' -and
            $symmetry.Text -eq 'Enforce mesh symmetry about the following axes' -and
            $axisLabel.Text -eq 'Axes of simmetry' -and
            (Field $dialog 'generateCircularHoles').Text -eq 'Create circular holes around this group of joints' -and
            (Field $dialog 'holeGroupLabel').Text -eq 'Group' -and
            (Field $dialog 'holeDiameterLabel').Text -eq 'Diameter (mm)' -and
            (Field $dialog 'gmshOGrid').Text -eq 'Generate a structured O-Grid mesh' -and
            (Field $dialog 'generateHoles').Text -eq 'Create openings around the following group of frames and/or shells edges' -and
            (Field $dialog 'openingGroupLabel').Text -eq 'Group' -and
            -not $type.GetField('gmshHoleHint', $flags) -and
            -not $type.GetField('gmshOpeningHint', $flags) -and
            -not $type.GetField('gmshSapHint', $flags)) `
            'Reference tab wording or removal of blue callout text is incomplete.'
        $tabs.SelectedTab = (Field $dialog 'gmshSapTab')
        $backupBox = Field $dialog 'createBackup'
        $backupLabel = Field $dialog 'createBackupDescription'
        Assert ((Field $dialog 'fastMode').Text -eq 'Use interactive database to generate elements in SAP2000/CSiBridge (fast mode)' -and
            $backupLabel.Text -eq ('Before every Generate, copy the previously saved SDB/BDB to a numbered backup, ' +
                'then save the current model to its SDB/BDB and capture the Undo snapshot. ' +
                'The backup does not include edits that were still unsaved before Generate.') -and
            $backupBox.Text -eq '' -and $backupBox.AccessibleName -eq $backupLabel.Text -and
            (Field $dialog 'fastMode').Height -ge 44 -and
            (Field $dialog 'fastMode').Bottom -le $backupBox.Top -and
            (Field $dialog 'fastMode').CheckAlign -eq [Drawing.ContentAlignment]::TopLeft -and
            (Field $dialog 'fastMode').TextAlign -eq $backupLabel.TextAlign -and
            $backupLabel.Top -eq $backupBox.Top + 5 -and
            $backupLabel.Left -ge $backupBox.Right -and
            $backupLabel.Bottom -lt $tabs.SelectedTab.Height -and
            $backupLabel.TextAlign -eq [Drawing.ContentAlignment]::TopLeft) `
            'Transfer options wording differs from the supplied layout.'
        $backupBox.Checked = $false
        $backupLabel.GetType().GetMethod('OnClick',[Reflection.BindingFlags]'Instance,NonPublic').Invoke(
            $backupLabel,[object[]]@([EventArgs]::Empty)) | Out-Null
        Assert $backupBox.Checked 'Clicking the backup explanation must toggle its checkbox.'
        $tabs.SelectedTab = $additional
        $axisCtor = @($axis.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') |
            Where-Object { $_.GetParameters().Count -eq 3 -and $_.GetParameters()[2].ParameterType -eq [int] })[0]
        $compatibleAxis = $axisCtor.Invoke([object[]]@([double[]]@(0,0,0), [double[]]@(1,0,0), [int]1))
        $axes = [Array]::CreateInstance($axis, 1)
        $axes.SetValue($compatibleAxis, 0)
        $dialog.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), $axes)
        $additional.AutoScrollPosition = [Drawing.Point]::Empty
        [Windows.Forms.Application]::DoEvents()
        Assert ($symmetry.Enabled -and $axisChoice.Items.Count -eq 1 -and
            $hint.Text -match 'Select one or two perpendicular' -and
            $viewport.Contains($symmetry.RectangleToScreen($symmetry.ClientRectangle))) `
            'Symmetry did not become visible and available for a compatible selection.'
        Assert ($circularHoles.Enabled -and $openings.Enabled -and
            $holeGroup.Items.Count -eq 0 -and $openingGroup.Items.Count -eq 0 -and
            -not $holeGroup.Enabled -and -not $openingGroup.Enabled) `
            'Top-level hole checkboxes must remain selectable without groups; their selectors must not.'
        $circularHoles.Checked = $true
        Assert ($oGrid.Enabled -and $circularHoles.Checked) `
            'The circular O-grid checkbox should become selectable when circular holes are chosen.'
        $oGrid.Checked = $true
        $circularHoles.Checked = $false
        Assert (-not $oGrid.Checked -and -not $oGrid.Enabled) `
            'Clearing circular holes must clear the dependent O-grid checkbox.'
        $openings.Checked = $true
        Assert ($openings.Checked -and -not $openingGroup.Enabled) `
            'Opening preference should be selectable, but the absent group cannot be edited.'
        $openings.Checked = $false
        if ($ScreenshotPath) {
            Assert ((Test-Path (Split-Path -Parent $ScreenshotPath) -PathType Container) -and
                -not (Test-Path $ScreenshotPath)) 'Unsafe Additional Options screenshot destination.'
            $dialog.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
                [string[]]@('BOLT'), [string[]]@('ALL'), $axes)
            $regular.Checked = $true
            $circularHoles.Checked = $true
            $oGrid.Checked = $true
            $openings.Checked = $true
            [Windows.Forms.Application]::DoEvents()
            $image = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
            try {
                $dialog.DrawToBitmap($image, [Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
                $image.Save($ScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $image.Dispose()
                $regular.Checked = $false
                $circularHoles.Checked = $false
                $openings.Checked = $false
                $dialog.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
                    [string[]]@(), [string[]]@(), $axes)
            }
        }
        $tabs.SelectedTab = $page
        $grid = Preview 'grid'
        $groups = @('general','generation','recombination','optimization') | ForEach-Object { Preview $_ }
        Assert ($grid.ColumnCount -eq 1 -and $groups.Count -eq 4 -and
            -not $page.Controls[0].VerticalScroll.Visible) 'Gmsh controls do not fit the single-column layout.'
        foreach ($i in 0..3) { Assert ($grid.GetPositionFromControl($groups[$i]).Row -eq $i) "Group $i is out of order." }
        $algorithm = Preview 'algorithm'
        $smoothing = Preview 'smoothingSteps'
        $smoothingLabel = Preview 'smoothingLabel'
        $fallback = Preview 'switchOnFailure'
        Assert ($smoothing -is [Windows.Forms.TextBox] -and $smoothing.Text -eq '1' -and
            $smoothing.RectangleToScreen($smoothing.ClientRectangle).Top -gt
                $algorithm.RectangleToScreen($algorithm.ClientRectangle).Top -and
            $fallback.RectangleToScreen($fallback.ClientRectangle).Top -gt
                $smoothing.RectangleToScreen($smoothing.ClientRectangle).Top -and
            -not $page.Controls[0].VerticalScroll.Visible) `
            'Smoothing must be visible under the algorithm, before fallback, without scrolling.'
        $smoothing.Text = '0'
        Assert (($dialog.GmshSettingsJson | ConvertFrom-Json).smoothing -eq 0) `
            'Zero smoothing was not sent to Gmsh.'
        $savedSmoothing = $panelType.GetMethod('Capture', $flags).Invoke($panel, [object[]]@(0.001))
        $smoothing.Text = '1'
        $panelType.GetMethod('Restore', $flags).Invoke($panel, [object[]]@($savedSmoothing, $false))
        Assert ($smoothing.Text -eq '0' -and ($dialog.GmshSettingsJson | ConvertFrom-Json).smoothing -eq 0) `
            'Zero smoothing was not restored from saved preferences.'
        $smoothing.Text = '1'
        Assert (($dialog.GmshSettingsJson | ConvertFrom-Json).smoothing -eq 1) `
            'One smoothing pass was not sent to Gmsh.'
        foreach ($invalid in @('-1', '101', '1.5', 'abc')) {
            $smoothing.Text = $invalid
            $rejected = $false
            $reason = ''
            Assert ($smoothing.Text -eq $invalid) 'Smoothing input was unexpectedly changed.'
            try { $panelType.GetMethod('CheckValues', $flags).Invoke($panel, [object[]]@()) | Out-Null }
            catch { $reason = $_.Exception.ToString(); $rejected = $reason -match 'Invalid value for|Valore non valido per' }
            Assert $rejected "Smoothing value '$invalid' must be rejected. $reason"
        }
        $smoothing.Text = '1'
        Assert ($algorithm.Items.Count -eq 7 -and $algorithm.SelectedIndex -eq 5 -and
            -not @($algorithm.Items | Where-Object { $_ -match '^9\b' }).Count) 'Unsupported algorithm is selectable.'
        Assert ($regular.Parent -eq $additional -and -not $regular.Checked) `
            'Regular grid preference must be in Additional Options and off by default.'
        $regular.Checked = $true
        Assert (($dialog.GmshSettingsJson | ConvertFrom-Json).structured.mode -eq 'automatic') `
            'Enabling regular grid must activate Gmsh automatic transfinite meshing.'
        $preserveEdges.Checked = $true
        Assert ($dialog.PreserveSelectedBoundaryEdges -and
            ($dialog.GmshSettingsJson | ConvertFrom-Json).preserveSelectedEdges) `
            'Preserving selected boundary intervals must activate the backend constraint.'
        $topologySteps = Preview 'topologySteps'
        $topologySteps.Text = '0'
        Assert (($dialog.GmshSettingsJson | ConvertFrom-Json).options.'Mesh.RecombineOptimizeTopology' -eq 0) `
            'Recombination topology steps must allow zero.'
        if ($GmshScreenshotPath) {
            Assert ((Test-Path (Split-Path -Parent $GmshScreenshotPath) -PathType Container) -and
                -not (Test-Path $GmshScreenshotPath)) 'Unsafe Gmsh screenshot destination.'
            $image = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
            try {
                $dialog.DrawToBitmap($image, [Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
                $image.Save($GmshScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $image.Dispose() }
        }
        $maxSize = Preview 'maxSize'
        $minSize = Preview 'minSize'
        $maxSize.Text = '40mm'; $minSize.Text = '5mm'
        $algorithm.SelectedIndex = 0
        $panelType.GetField('refineSteps', $flags).SetValue($panel, '0')
        Assert (($dialog.GmshSettingsJson | ConvertFrom-Json).options.'Mesh.RefineSteps' -eq 0) `
            'MeshAdapt must pass zero refinement steps to Gmsh.'
        (Preview 'switchOnFailure').Checked = $false
        (Preview 'quality').Text = '0.6'
        (Field $dialog 'gmshExtra').Text = 'Mesh.RandomFactor = 0.123'
        Assert (-not ($dialog.GmshSettingsJson | ConvertFrom-Json).options.PSObject.Properties['Mesh.RandomFactor']) `
            'Previously saved hidden debug options must not affect the mesh.'
        $smoothing.Text = '0'
        (Field $dialog 'gmshOGrid').Checked = $true
        (Field $dialog 'generateCircularHoles').Checked = $true
        (Field $dialog 'fastMode').Checked = $false
        (Field $dialog 'createBackup').Checked = $false
        $reset = Field $dialog 'gmshResetDefault'
        $commands = @((Field $dialog 'generate'), (Field $dialog 'details'), (Field $dialog 'undo'), $reset)
        Assert ($reset.Parent -eq $dialog -and
            @($commands | Select-Object -ExpandProperty Width | Select-Object -Unique).Count -eq 1 -and
            @($commands | Select-Object -ExpandProperty Top | Select-Object -Unique).Count -eq 1 -and
            $reset.Right -le $dialog.ClientSize.Width) 'Four bottom commands are not aligned.'
        $captured = $panelType.GetMethod('Capture', $flags).Invoke($panel, [object[]]@([double]0.001))
        $reset.PerformClick()
        Assert (-not $regular.Checked -and -not $preserveEdges.Checked) `
            'Reset must deselect the regular grid and boundary-preservation preferences.'
        $panelType.GetMethod('Restore', $flags).Invoke($panel, [object[]]@($captured, $false)) | Out-Null
        Assert ($regular.Checked -and $topologySteps.Text -eq '0' -and
            ($dialog.GmshSettingsJson | ConvertFrom-Json).structured.mode -eq 'automatic') `
            'The regular grid preference was not restored from saved V2 settings.'
        $reset.PerformClick()
        $settings = $dialog.GmshSettingsJson | ConvertFrom-Json
        Assert ($maxSize.Text -eq '40mm' -and $minSize.Text -eq '5mm' -and
            $settings.previewMode -and $settings.algorithm -eq 8 -and $settings.optimize -and
            $smoothing.Text -eq '1' -and $settings.smoothing -eq 1 -and
            $settings.options.'Mesh.MeshSizeMin' -eq 5 -and
            $settings.options.'Mesh.RecombineMinimumQuality' -eq .01 -and
            $settings.options.'Mesh.RecombineOptimizeTopology' -eq 5 -and
            $settings.options.'Mesh.AlgorithmSwitchOnFailure' -eq 1 -and
            -not $settings.options.PSObject.Properties['Mesh.RandomFactor'] -and
            -not $settings.oGrid -and -not $settings.preserveSelectedEdges -and
            -not $dialog.PreserveSelectedBoundaryEdges -and
            -not $regular.Checked -and $settings.structured.mode -eq 'unstructured' -and
            (Field $dialog 'fastMode').Checked -and (Field $dialog 'createBackup').Checked) 'Reset did not restore defaults or preserve sizes.'
        $dialog.BeginWork('Preparing geometry...')
        $controller = Field $dialog 'cancellationController'
        $controllerFlags = [Reflection.BindingFlags]'Instance,NonPublic'
        function ProgressState($name) { $controller.GetType().GetField($name, $controllerFlags).GetValue($controller) }
        Assert (-not (Field $dialog 'undo').Enabled -and
            (Field $dialog 'undo').Text -eq 'Undo mesh' -and
            -not $type.GetField('progress', $flags)) 'Cancel or progress is still exposed in the main window.'
        $dialog.SetIndeterminate('Saving the model before meshing...')
        $dialog.SetIndeterminate('Capturing the model snapshot for Undo...')
        Assert ((Field $dialog 'status').Text -eq 'Capturing the model snapshot for Undo...' -and
            (ProgressState 'progressMessage') -eq 'Capturing the model snapshot for Undo...') `
            'Snapshot capture must replace the stale saving message in both progress windows.'
        $dialog.SetProgress('Creating SAP2000 points...', 2, 4)
        Assert ((ProgressState 'progressMessage') -match 'Phase progress: 2/4 \(50%\)' -and
            (ProgressState 'progressPercent') -eq 50) 'Point insertion did not identify its phase progress.'
        $dialog.SetProgress('Creating shell elements and transferring loads...', 1, 4)
        Assert ((ProgressState 'progressMessage') -match 'Phase progress: 1/4 \(25%\)' -and
            (ProgressState 'progressPercent') -eq 25 -and
            (ProgressState 'progressMessage') -notmatch 'area 1/') `
            'Bulk insertion must not display a stale per-area counter.'
        $dialog.SetMeshingAreas(0, 8)
        $dialog.SetMeshingAreas(1, 8)
        $dialog.SetIndeterminate('Generating the mesh...')
        Assert ((ProgressState 'progressArea') -match 'Area 2/8' -and
            (ProgressState 'progressPercent') -gt 0) 'Generation did not advance to the second area.'
        $dialog.SetProgress('Creating shell elements and transferring loads...', 2, 4)
        Assert ((ProgressState 'progressArea') -match 'Area 2/8') `
            'Area count disappeared when the phase progress changed.'
        $dialog.ShowBatchAreas(8)
        Assert ((ProgressState 'progressArea') -match 'Gmsh: areas 1.8/8 together') `
            'A conforming Gmsh batch must identify all concurrently meshed areas.'
        $dialog.ShowBatchAreas(8, $true)
        Assert ((ProgressState 'progressArea') -match 'SAP2000 import: areas 1.8/8 together') `
            'A bulk SAP import must not claim that only area 1 is active.'
        $dialog.SetMeshingAreas(2, 8)
        Assert ((ProgressState 'progressArea') -match 'Area 3/8') `
            'Per-area preparation did not advance to the third area.'
        $dialog.Cancelled()
        $dialog.SetSelectionCounts(1449, 1163, 286, 0, 176, 0, 1380)
        $summary = Field $dialog 'selectionSummary'
        $areas = Field $dialog 'areaSelection'
        $objects = Field $dialog 'objectSelection'
        Assert ($summary.Height -ge 64 -and $areas.Height -ge 19 -and
            $objects.Height -ge 19 -and $objects.Bottom -lt $summary.Height) 'Top summary clips its two rows.'
        foreach ($label in @($areas, $objects)) {
            Assert ($label.Width -ge [Windows.Forms.TextRenderer]::MeasureText($label.Text, $label.Font).Width) `
                "Top summary text is wider than its available space: $($label.Text)"
        }
        (Field $dialog 'generate').PerformClick()
        Assert ($dialog.StartRequested -and [Math]::Abs($dialog.MaxSize - 40) -lt 1e-9) 'Generate did not read the visible maximum.'
        (Field $dialog 'language').SelectedIndex = 1
        [Windows.Forms.Application]::DoEvents()
        Assert ($subtitle.Text -eq 'Advanced 2D mesh generation' -and
            [Windows.Forms.TextRenderer]::MeasureText($subtitle.Text, $subtitle.Font,
                [Drawing.Size]::new($subtitle.Width, 100),
                [Windows.Forms.TextFormatFlags]::WordBreak).Height -le $subtitle.Height + 5) `
            'MESHing subtitle changed after switching to Italian.'
        Assert ((@($tabs.TabPages | ForEach-Object Text) -join ',') -eq
            'Gmsh,Opzioni aggiuntive,Opzioni di trasferimento' -and
            $reset.Text -eq 'Ripristina default') 'Italian controls changed.'
        $tabs.SelectedTab = $page
        Assert ($regular.Text -match 'regolari allineate' -and
            [Windows.Forms.TextRenderer]::MeasureText($regular.Text, $regular.Font).Width -le $regular.Width) `
            'Italian regular-grid control is clipped.'
        Assert ($smoothingLabel.Text -eq 'Passaggi di smoothing' -and
            $fallback.RectangleToScreen($fallback.ClientRectangle).Top -gt
                $smoothing.RectangleToScreen($smoothing.ClientRectangle).Top -and
            -not $page.Controls[0].VerticalScroll.Visible) `
            'Italian smoothing label or fallback layout is incorrect.'
        Assert ($preserveEdges.Text -match 'nodi interni selezionati' -and
            [Windows.Forms.TextRenderer]::MeasureText($preserveEdges.Text, $preserveEdges.Font).Width -le $preserveEdges.Width) `
            'Italian protected-edge control is clipped.'
        $tabs.SelectedTab = $additional
        $additional.AutoScrollPosition = [Drawing.Point]::Empty
        [Windows.Forms.Application]::DoEvents()
        $viewport = $additional.RectangleToScreen($additional.ClientRectangle)
        Assert ($symmetry.Text -match 'Imponi simmetria' -and
            -not $additional.VerticalScroll.Visible -and
            $viewport.Contains($regular.RectangleToScreen($regular.ClientRectangle)) -and
            $viewport.Contains($preserveEdges.RectangleToScreen($preserveEdges.ClientRectangle)) -and
            $viewport.Contains($symmetry.RectangleToScreen($symmetry.ClientRectangle)) -and
            $viewport.Contains($openingGroup.RectangleToScreen($openingGroup.ClientRectangle))) `
            'Italian additional options require scrolling or clip a control.'
        $tabs.SelectedTab = (Field $dialog 'gmshSapTab')
        $italianFast = Field $dialog 'fastMode'
        $fastTextHeight = [Windows.Forms.TextRenderer]::MeasureText($italianFast.Text, $italianFast.Font,
            [Drawing.Size]::new($italianFast.Width - 28, 500),
            [Windows.Forms.TextFormatFlags]::WordBreak).Height
        Assert ($italianFast.Height -ge $fastTextHeight -and
            $italianFast.TextAlign -eq [Drawing.ContentAlignment]::TopLeft -and
            $italianFast.CheckAlign -eq [Drawing.ContentAlignment]::TopLeft) `
            'Italian FAST text must fit below its top-aligned checkbox.'
        $backupLabel = Field $dialog 'createBackupDescription'
        $backupHeight = [Windows.Forms.TextRenderer]::MeasureText($backupLabel.Text, $backupLabel.Font,
            [Drawing.Size]::new($backupLabel.Width - 4, 500),
            [Windows.Forms.TextFormatFlags]::WordBreak).Height
        Assert ($backupLabel.Text -match '^Prima di ogni Genera, copia l.SDB/BDB' -and
            $backupLabel.Text -match 'Il backup non include le modifiche' -and
            $backupLabel.Height -ge $backupHeight -and
            $backupLabel.Bottom -lt $tabs.SelectedTab.Height) `
            "The Italian backup explanation is invalid or clipped: text=$($backupLabel.Text) required=$backupHeight actual=$($backupLabel.Height) bottom=$($backupLabel.Bottom) tabHeight=$($tabs.SelectedTab.Height)."
        $tabs.SelectedTab = $additional
        if ($ItalianScreenshotPath) {
            Assert ((Test-Path (Split-Path -Parent $ItalianScreenshotPath) -PathType Container) -and
                -not (Test-Path $ItalianScreenshotPath)) 'Unsafe Italian screenshot destination.'
            $image = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
            try {
                $dialog.DrawToBitmap($image, [Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
                $image.Save($ItalianScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $image.Dispose() }
        }
        Write-Host 'V2_UI=PASS CLASSIC_ABSENT=PASS FOUR_EQUAL_COMMANDS=PASS RESET_KEEP_SIZES=PASS EN_IT_LAYOUT=PASS'
    } finally { $dialog.Close(); $dialog.Dispose() }
    $reportType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshQualityReport', $true)
    $inventoryType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshInventory', $true)
    foreach ($applyFirst in @($false, $true)) {
        $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED = if ($applyFirst) { $null } else { '1' }
        $env:SAP_QUAD_ATOMIC_CLOSE_APPLY = if ($applyFirst) { '1' } else { $null }
        $closing = $ctor.Invoke([object[]]@('mm', $false, [string[]]@(),
            [string[]]@(), [Array]::CreateInstance($axis, 0), $true))
        $script:closeUndoCount = 0
        $script:closeApplyCount = 0
        $script:closeProgressVisible = 0
        try {
            $closing.Show()
            $report = [Activator]::CreateInstance($reportType, [object[]]@($true, 'Accepted.'))
            $inventory = $inventoryType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')[0].Invoke(
                [object[]]@(5, 5, 0, 0, 0, 0, 8))
            $closing.BeginWork('Preparing geometry...')
            $closing.Complete($report, 5, $inventory, $null, $null,
                [Action]{ $script:closeUndoCount++; $script:closeProgressVisible = @([Windows.Forms.Application]::OpenForms |
                    Where-Object { $_.Text -match 'Closing mesh preview|Chiusura anteprima mesh' }).Count },
                [Action]{ $script:closeApplyCount++; $script:closeProgressVisible = @([Windows.Forms.Application]::OpenForms |
                    Where-Object { $_.Text -match 'Closing mesh preview|Chiusura anteprima mesh' }).Count },
                [Action]{}, 'snapshot.csv')
            $closing.Close()
            Assert (-not $closing.Visible -and -not $closing.UndoFailure -and
                $script:closeProgressVisible -eq 1 -and
                $script:closeUndoCount -eq $(if ($applyFirst) { 0 } else { 1 }) -and
                $script:closeApplyCount -eq $(if ($applyFirst) { 1 } else { 0 }) -and
                $closing.PreviewRestoredOnClose -eq (-not $applyFirst)) `
                "The X must offer Apply confirmation or Undo progress. applyChoice=$applyFirst visible=$($closing.Visible) failure=$($closing.UndoFailure) progress=$script:closeProgressVisible undo=$script:closeUndoCount apply=$script:closeApplyCount restored=$($closing.PreviewRestoredOnClose)"
        } finally { $closing.Dispose() }
    }
    Write-Host 'CLOSE_UNAPPLIED_UNDO=PASS CLOSE_APPLIED_RETAIN=PASS'
    $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED = $null
    $env:SAP_QUAD_ATOMIC_CLOSE_APPLY = $null
    $promptDialog = $ctor.Invoke([object[]]@('mm', $false, [string[]]@(),
        [string[]]@(), [Array]::CreateInstance($axis, 0), $true))
    $script:choiceSeen = $false
    $script:choiceStayedOpen = $false
    $script:choiceButtons = 0
    $script:choiceNoX = $false
    $script:promptUndoCount = 0
    $choiceTimer = [Windows.Forms.Timer]::new()
    $choiceTimer.Interval = 40
    $choiceTimer.Add_Tick({
        $choice = @([Windows.Forms.Application]::OpenForms | Where-Object {
            $_.Text -match 'Close mesh preview|Chiudere l.anteprima mesh' }) | Select-Object -First 1
        if (-not $choice) { return }
        $choiceTimer.Stop()
        $script:choiceSeen = $true
        $script:choiceButtons = @($choice.Controls | Where-Object { $_ -is [Windows.Forms.Button] }).Count
        $script:choiceNoX = -not $choice.ControlBox
        $choice.Close()
        $script:choiceStayedOpen = $choice.Visible
        $restore = @($choice.Controls | Where-Object { $_ -is [Windows.Forms.Button] -and
            $_.DialogResult -eq [Windows.Forms.DialogResult]::No })[0]
        $restore.PerformClick()
    })
    try {
        $promptDialog.Show()
        $report = [Activator]::CreateInstance($reportType, [object[]]@($true, 'Accepted.'))
        $inventory = $inventoryType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')[0].Invoke(
            [object[]]@(5, 5, 0, 0, 0, 0, 8))
        $promptDialog.BeginWork('Preparing geometry...')
        $promptDialog.Complete($report, 5, $inventory, $null, $null,
            [Action]{ $script:promptUndoCount++ }, [Action]{}, [Action]{}, 'snapshot.csv')
        $choiceTimer.Start()
        $promptDialog.Close()
        Assert ($script:choiceSeen -and $script:choiceNoX -and $script:choiceButtons -eq 2 -and
            $script:choiceStayedOpen -and $script:promptUndoCount -eq 1 -and
            $promptDialog.PreviewRestoredOnClose -and -not $promptDialog.Visible) `
            'The close prompt must expose only Apply and Undo and reject closing without a choice.'
        Write-Host 'CLOSE_PROMPT_TWO_CHOICES_ONLY=PASS'
    } finally { $choiceTimer.Stop(); $choiceTimer.Dispose(); $promptDialog.Dispose() }
    $empty = $ctor.Invoke([object[]]@('mm', $false, [string[]]@(),
        [string[]]@(), [Array]::CreateInstance($axis, 0), $false))
    try {
        $empty.Show()
        [Windows.Forms.Application]::DoEvents()
        $generate = Field $empty 'generate'
        $emptyFast = Field $empty 'fastMode'
        $emptyBackup = Field $empty 'createBackup'
        $emptyBackupLabel = Field $empty 'createBackupDescription'
        Assert (-not $generate.Enabled -and (Field $empty 'status').Text -match 'aggiorna automaticamente|updates automatically') `
            "Opening without a selection must wait for SAP objects: enabled=$($generate.Enabled) status=$((Field $empty 'status').Text)"
        Assert (-not $emptyFast.Enabled -and -not $emptyBackup.Enabled -and
            -not $emptyBackupLabel.Enabled -and $emptyFast.Checked -and $emptyBackup.Checked) `
            'No selection must disable, not clear, the FAST and backup choices.'
        $emptyHoles = Field $empty 'generateCircularHoles'
        $emptyOpenings = Field $empty 'generateHoles'
        Assert ($emptyHoles.Enabled -and $emptyOpenings.Enabled -and
            $emptyHoles.FlatStyle -eq [Windows.Forms.FlatStyle]::Standard -and
            $emptyOpenings.FlatStyle -eq [Windows.Forms.FlatStyle]::Standard) `
            'Hole preferences should be selectable and consistently styled before choosing an area.'
        $emptyHoles.Checked = $true
        Assert ($emptyHoles.Checked -and -not $generate.Enabled) `
            'A hole preference must not enable generation without any selected SAP area.'
        $emptyHoles.Checked = $false
        $empty.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), [Array]::CreateInstance($axis, 0))
        Assert ($generate.Enabled -and $emptyFast.Enabled -and $emptyBackup.Enabled -and
            $emptyBackupLabel.Enabled -and $emptyFast.Checked -and $emptyBackup.Checked -and
            (Field $empty 'areaSelection').Text -match 'Areas: 1|Aree: 1') `
            'Selecting an area after opening did not enable Generate.'
        $empty.UpdateLiveSelection(0, 0, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), [Array]::CreateInstance($axis, 0))
        Assert (-not $generate.Enabled -and -not $emptyFast.Enabled -and -not $emptyBackup.Enabled -and
            $emptyFast.Checked -and $emptyBackup.Checked -and
            (Field $empty 'areaSelection').Text -match 'Areas: 0|Aree: 0') `
            'Clearing the SAP selection did not disable Generate.'
        $empty.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), [Array]::CreateInstance($axis, 0))
        $generate.PerformClick()
        Assert ($empty.StartRequested) 'Generate still uses the selection from dialog opening.'
        Write-Host 'LIVE_SELECTION_EMPTY_TO_AREA_TO_EMPTY_TO_GENERATE=PASS'
    } finally { $empty.Close(); $empty.Dispose() }
    $rearmed = $ctor.Invoke([object[]]@('mm', $false, [string[]]@(),
        [string[]]@(), [Array]::CreateInstance($axis, 0), $true))
    try {
        $rearmed.Show()
        [Windows.Forms.Application]::DoEvents()
        $reportType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshQualityReport', $true)
        $inventoryType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin+MeshInventory', $true)
        $report = [Activator]::CreateInstance($reportType, [object[]]@($true, 'Quality accepted.'))
        $inventory = $inventoryType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')[0].Invoke(
            [object[]]@(5, 5, 0, 0, 0, 0, 8))
        $rearmed.BeginWork('Preparing geometry...')
        $rearmed.Complete($report, 5, $inventory, $null, $null, [Action]{},
            [Action]{}, [Action]{}, 'prior-model-snapshot.csv')
        Assert (-not $rearmed.CanRefreshSelection) 'An uncommitted preview must keep its captured selection.'
        $rearmed.ApplyForAutomation()
        Assert ($rearmed.Applied -and $rearmed.CanRefreshSelection -and
            -not (Field $rearmed 'undo').Enabled -and
            (Field $rearmed 'snapshotPath') -eq $null) `
            'Applying the mesh did not complete the transaction or release its Undo snapshot.'
        $rearmed.RearmAppliedForNewSelection()
        Assert (-not $rearmed.Applied -and $rearmed.RetryRequested -and
            (Field $rearmed 'qualityReport') -eq $null -and
            -not (Field $rearmed 'undo').Enabled -and
            -not (Field $rearmed 'generate').Enabled) 'Previous-model Undo remained active before snapshot cleanup.'
        $rearmed.FinishRearm()
        Assert (-not $rearmed.RetryRequested -and (Field $rearmed 'generate').Enabled) `
            'Generation did not re-enable after releasing the previous snapshot.'
        $rearmed.UpdateLiveSelection(0, 0, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), [Array]::CreateInstance($axis, 0))
        Assert (-not (Field $rearmed 'generate').Enabled) 'Generate stayed enabled without selected areas.'
        $rearmed.ChangeLengthUnit('m')
        $newPanel = Field $rearmed 'gmshPreviewOptions'
        $maxText = $newPanel.GetType().GetField('maxSize', $flags).GetValue($newPanel).Text
        Assert ($maxText -eq '0.1' -and (Field $rearmed 'holeDiameter').Text -eq '0.05') `
            'Changing SAP model units did not convert visible length values.'
        $rearmed.UpdateLiveSelection(1, 1, 0, 0, 0, 0, 0, $false,
            [string[]]@(), [string[]]@(), [Array]::CreateInstance($axis, 0))
        Assert ((Field $rearmed 'generate').Enabled) 'New SAP selection did not enable Generate.'
        Write-Host 'AFTER_APPLY_REFRESH=PASS OLD_UNDO_DISABLED=PASS MODEL_UNITS_CONVERTED=PASS'
    } finally { $rearmed.Close(); $rearmed.Dispose() }
    $env:SAP_QUAD_ATOMIC_SIZE = $null
    [IO.File]::WriteAllText($env:SAP_GMSH_V2_SETTINGS_PATH,
        '{"SizeMeters":0.04,"Fast":false,"Backup":false,"PreserveSelectedEdges":true,"GmshPreview":{"MaxMeters":0.04,"MinMeters":0.005,"Algorithm":0,"Recombine":false,"Optimize":false}}')
    $migrated = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        $migrated.Show()
        [Windows.Forms.Application]::DoEvents()
        $v2 = Field $migrated 'gmshPreviewOptions'
        $restoredSettings = $migrated.GmshSettingsJson | ConvertFrom-Json
        Assert (-not $migrated.StrictMaximumSize -and $migrated.EdgeTolerancePercent -eq 20 -and
            -not $migrated.PreferRegularMesh -and -not $migrated.StressRefinement -and
            $migrated.StructuredSectorBands -and (Field $migrated 'useMaximumCores').Checked -and
            (Field $migrated 'profiles').SelectedIndex -eq 2 -and
            $v2.GetType().GetField('maxSize',$flags).GetValue($v2).Text -eq '40' -and
            $v2.GetType().GetField('minSize',$flags).GetValue($v2).Text -eq '5' -and
            -not (Field $migrated 'gmshPreserveSelectedEdges').Checked -and
            $restoredSettings.preserveSelectedEdges -eq $false -and
            (Field $migrated 'fastMode').Checked -and (Field $migrated 'createBackup').Checked -and
            $restoredSettings.algorithm -eq 8 -and $restoredSettings.recombine -and $restoredSettings.optimize) `
            'A new window must restore only mesh sizes and reset every other option.'
        $migrated.Close()
        $savedSizes = Get-Content -LiteralPath $env:SAP_GMSH_V2_SETTINGS_PATH -Raw | ConvertFrom-Json
        Assert ($savedSizes.SizeMeters -eq 0.04 -and $savedSizes.MinMeters -eq 0.005 -and
            $savedSizes.Language -eq 'en' -and @($savedSizes.PSObject.Properties).Count -eq 3) `
            "The settings file must contain only two sizes and language: $($savedSizes | ConvertTo-Json -Compress)"
        Write-Host 'LEGACY_SIZES_RESTORED=PASS OPTIONS_RESET=PASS ONLY_SIZES_AND_LANGUAGE_SAVED=PASS'
    } finally { $migrated.Close(); $migrated.Dispose() }
    $edited = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        $edited.Show()
        $panel = Field $edited 'gmshPreviewOptions'
        $panel.GetType().GetField('maxSize',$flags).GetValue($panel).Text = '60'
        $panel.GetType().GetField('minSize',$flags).GetValue($panel).Text = '6'
        (Field $edited 'language').SelectedIndex = 1
        (Field $edited 'gmshPreserveSelectedEdges').Checked = $true
        Assert ((Field $edited 'gmshPreserveSelectedEdges').Checked) 'Options must remain set within an open window.'
    } finally { $edited.Close(); $edited.Dispose() }
    $reopened = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        $reopened.Show()
        $panel = Field $reopened 'gmshPreviewOptions'
        Assert ($panel.GetType().GetField('maxSize',$flags).GetValue($panel).Text -eq '60' -and
            $panel.GetType().GetField('minSize',$flags).GetValue($panel).Text -eq '6' -and
            (Field $reopened 'language').SelectedIndex -eq 1 -and
            -not (Field $reopened 'gmshPreserveSelectedEdges').Checked) `
            'Reopening must retain edited sizes and the selected language, but reset other options.'
        Write-Host 'CLOSE_WITHOUT_GENERATE_SAVES_SIZES=PASS REOPEN_RETAINS_LANGUAGE_RESETS_OPTIONS=PASS'
    } finally { $reopened.Close(); $reopened.Dispose() }
    [IO.File]::WriteAllText($env:SAP_GMSH_V2_SETTINGS_PATH, '{"SizeMeters":0.04,"MinMeters":0.005}')
    [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('it-IT')
    $systemLanguage = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        Assert ((Field $systemLanguage 'language').SelectedIndex -eq 1) `
            'Without a saved language, the system UI language must be used.'
    } finally { $systemLanguage.Dispose() }
    [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('en-US')
    Write-Host 'SYSTEM_UI_LANGUAGE_FALLBACK=PASS'
    $regionGuard = $ctor.Invoke([object[]]@('mm', $true, [string[]]@('BOLT'),
        [string[]]@('ALL'), [Array]::CreateInstance($axis, 0), $true))
    try {
        $regionGuard.BeginRegionalTransaction()
        Assert (-not $regionGuard.DetectRegionalChange()) 'Unchanged Windows settings must permit generation.'
        $regionField = $type.GetField('regionalSignature', $flags)
        $originalSignature = $regionField.GetValue($regionGuard)
        $regionField.SetValue($regionGuard, 'changed-regional-settings')
        Assert ($regionGuard.DetectRegionalChange() -and $regionGuard.RegionalUnsafe -and
            -not $regionGuard.RegionalModelModified) 'Regional change before model modification was not detected.'
        $regionField.SetValue($regionGuard, $originalSignature)
        Assert ($regionGuard.DetectRegionalChange()) 'A transient regional change must remain unsafe after reverting.'
        $regionGuard.MarkRegionalModelModified()
        Assert ($regionGuard.RegionalModelModified) 'The recovery warning must distinguish an altered model.'
        Write-Host 'REGIONAL_CHANGE_DETECTED=PASS TRANSIENT_CHANGE_STAYS_UNSAFE=PASS'
    } finally { $regionGuard.Dispose() }
    $pluginType = $assembly.GetType('SapQuadTransitionPlugin.cPlugin', $true)
    $staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $hostCheck = $pluginType.GetMethod('HostRegionalSettingsChanged', $staticFlags)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    for ($probe = 0; $probe -lt 200; $probe++) { $null = $hostCheck.Invoke($null, [object[]]@()) }
    $clock.Stop()
    Assert ($clock.Elapsed.TotalSeconds -lt 2) `
        'Repeated regional checkpoints must not re-query the process name on every snapshot record.'
    $hostWarning = $pluginType.GetMethod('HostRegionalWarningMessage', $staticFlags)
    $bridgeWarning = $hostWarning.Invoke($null, [object[]]@('CSiBridge'))
    Assert ($bridgeWarning.Contains('CSiBridge') -and -not $bridgeWarning.Contains('SAP2000')) `
        'The regional warning must mention only the host that launched MESHing.'
    $laterThanHost = $pluginType.GetMethod('RegionalWriteAfterHostStart', $staticFlags)
    $started = [DateTime]::UtcNow.AddHours(-1)
    Assert (-not $laterThanHost.Invoke($null, [object[]]@($started, $started.AddTicks(-1))) -and
        $laterThanHost.Invoke($null, [object[]]@($started, $started.AddTicks(1)))) `
        'Windows regional changes must be compared to the current host process start time.'
    $regionalKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Control Panel\International')
    try {
        $lastWrite = $pluginType.GetMethod('RegistryLastWriteUtc', $staticFlags).Invoke(
            $null, [object[]]@($regionalKey))
        Assert ($lastWrite.Kind -eq [DateTimeKind]::Utc -and $lastWrite -le [DateTime]::UtcNow) `
            'Windows regional settings last-write time was not read in UTC.'
    } finally { $regionalKey.Dispose() }
    Write-Host 'HOST_SPECIFIC_WARNING=PASS HOST_START_REGIONAL_GUARD=PASS REGISTRY_TIMESTAMP_UTC=PASS HOST_CHECK_FAST=PASS'
} finally {
    $env:SAP_QUAD_ATOMIC_SIZE = $oldSize
    $env:SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED = $oldCloseUndo
    $env:SAP_QUAD_ATOMIC_CLOSE_APPLY = $oldCloseApply
    if (Test-Path -LiteralPath $env:SAP_GMSH_V2_SETTINGS_PATH) { Remove-Item -LiteralPath $env:SAP_GMSH_V2_SETTINGS_PATH -Force }
    $env:SAP_GMSH_V2_SETTINGS_PATH = $oldSettings
    [Threading.Thread]::CurrentThread.CurrentUICulture = $oldUiCulture
}
