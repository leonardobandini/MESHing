# Inspect SAP2000's table-selection state on disposable copies, in the user's order.
param([Parameter(Mandatory=$true)][string]$First,
      [Parameter(Mandatory=$true)][string]$Second,
      [Parameter(Mandatory=$true)][string]$Plugin,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [switch]$SkipFirstMesh, [switch]$InspectDisplayMenu, [switch]$InspectTablesDialog,
      [switch]$ExerciseFilteredGroup, [switch]$MeshSecond, [switch]$SelectJointTable,
      [ValidateRange(60,600)][int]$MeshTimeoutSeconds=240)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$parent=Join-Path $env:TEMP 'opencode'
if(-not(Test-Path -LiteralPath $parent -PathType Container) -or
   -not $ResultsRoot.StartsWith(($parent.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path -LiteralPath $ResultsRoot) -or
   -not(Test-Path -LiteralPath $First -PathType Leaf) -or
   -not(Test-Path -LiteralPath $Second -PathType Leaf) -or
   -not(Test-Path -LiteralPath $Plugin -PathType Leaf) -or
   @(Get-Process SAP2000 -ErrorAction SilentlyContinue).Count){throw 'Unsafe BUG_02 trial input.'}
$originalHashes=@((Get-FileHash -LiteralPath $First -Algorithm SHA256).Hash,
                  (Get-FileHash -LiteralPath $Second -Algorithm SHA256).Hash)
New-Item -ItemType Directory -Path $ResultsRoot|Out-Null
$firstCopy=Join-Path $ResultsRoot 'BUG_02_1-input.sdb'
$secondCopy=Join-Path $ResultsRoot 'BUG_02_2-input.sdb'
Copy-Item -LiteralPath $First -Destination $firstCopy
Copy-Item -LiteralPath $Second -Destination $secondCopy
if($InspectDisplayMenu -or $InspectTablesDialog -or $SelectJointTable){Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes -ErrorAction Stop}
if($InspectTablesDialog -or $SelectJointTable){Add-Type -AssemblyName System.Drawing,System.Windows.Forms;Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FemeshTableWindow {
    public delegate bool Visit(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Visit visit, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, Visit visit, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
}
Add-Type -Path 'C:\Program Files\Computers and Structures\SAP2000 27\CSiAPIv1.dll'
[Reflection.Assembly]::LoadFrom((Join-Path $root 'bin\Release\net48\RunTest01Fast.dll'))|Out-Null
$old=@{}
foreach($key in @('SAP_GMSH_V2_ATOMIC_EXAMPLES','SAP_GMSH_V2_ATOMIC_REGULAR_GRID',
    'SAP_GMSH_V2_SETTINGS_PATH','SAP_QUAD_ATOMIC_UNDO','SAP_QUAD_ATOMIC_BACKUP',
    'SAP_QUAD_ATOMIC_WORKERS','SAP_QUAD_ATOMIC_ALLOW_UNCONNECTED_SELECTED_POINTS')){
    $old[$key]=[Environment]::GetEnvironmentVariable($key)
}
$started=$false;$ownedPid=0
try{
    [RunTest01Fast]::StartIsolatedPersistentSap();$started=$true
    $ownedPid=[int][RunTest01Fast].GetField('persistentSapPid',
        [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    [IO.File]::WriteAllText((Join-Path $ResultsRoot 'owned-sap.pid'),[string]$ownedPid)
    $sap=[RunTest01Fast].GetField('persistentSap',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $model=$sap.SapModel
    if($model.File.OpenFile($firstCopy) -ne 0){throw 'Cannot open first disposable model.'}
    $areas=0;$areaNames=$null
    if($model.AreaObj.GetNameList([ref]$areas,[ref]$areaNames) -ne 0 -or $areas -lt 1){throw 'First model has no areas.'}
    $areaNames=[string[]]$areaNames
    "FIRST_AREAS=$areas FIRST_AREA_NAMES=$($areaNames -join ',')"
    $points=0;$pointNames=$null;$frames=0;$frameNames=$null
    if($model.PointObj.GetNameList([ref]$points,[ref]$pointNames) -ne 0 -or
       $model.FrameObj.GetNameList([ref]$frames,[ref]$frameNames) -ne 0){throw 'First model object inventory failed.'}
    if($model.SelectObj.All($true) -ne 0){throw 'Cannot select all in first model.'}
    $env:SAP_GMSH_V2_ATOMIC_EXAMPLES='1'
    $env:SAP_GMSH_V2_ATOMIC_REGULAR_GRID='0'
    $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $ResultsRoot 'isolated-settings.json'
    $env:SAP_QUAD_ATOMIC_UNDO=$null
    $env:SAP_QUAD_ATOMIC_BACKUP='0'
    $env:SAP_QUAD_ATOMIC_WORKERS='1'
    $selectedAreas=[string[]]@($areaNames | Where-Object { $_ })
    $selectedPoints=[string[]]@($pointNames | Where-Object { $_ })
    $selectedFrames=[string[]]@($frameNames | Where-Object { $_ })
    "FIRST_REQUEST_AREAS=$($selectedAreas.Count) POINTS=$($selectedPoints.Count) FRAMES=$($selectedFrames.Count)"
    if(-not $SkipFirstMesh){
        [RunTest01Fast]::RunAtomicExampleWithTimeout($firstCopy,
            (Join-Path $ResultsRoot 'BUG_02_1-applied.sdb'),$Plugin,
            $selectedAreas,$selectedPoints,$selectedFrames,'0.3m','','0.05m','',
            $false,$true,'General Purpose','xy','',$MeshTimeoutSeconds)
    }
    if($model.SelectObj.All($true) -ne 0){throw 'Cannot select generated mesh.'}
    $v=0;$fields=$null;$records=0;$values=$null;$groups=$null
    $code=$model.DatabaseTables.GetTableForDisplayArray('Joint Coordinates',[ref]$groups,'',
        [ref]$v,[ref]$fields,[ref]$records,[ref]$values)
    "FIRST_SELECTED_JOINT_DISPLAY_CODE=$code RECORDS=$records FIELDS=$($fields -join '|') GROUPS=$($groups -join ',')"
    if($InspectDisplayMenu -or $InspectTablesDialog -or $SelectJointTable){
        [RunTest01Fast]::DetachPersistentSap()
        $model=$null;$sap=$null
        [GC]::Collect();[GC]::WaitForPendingFinalizers()
        $hostProcess=Get-Process -Id $ownedPid
        if($hostProcess.MainWindowHandle -eq [IntPtr]::Zero){throw 'SAP2000 has no main window for menu inspection.'}
        $window=[Windows.Automation.AutomationElement]::FromHandle($hostProcess.MainWindowHandle)
        if($SelectJointTable){
            $views=$window.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::NameProperty,'3-D View'))
            $view=@($views|Where-Object {
                $_.Current.BoundingRectangle.Width -gt 200 -and
                $_.Current.BoundingRectangle.Height -gt 200
            })
            if(-not $view.Count){throw 'SAP viewport unavailable for GUI Select All.'}
            $viewBounds=$view[0].Current.BoundingRectangle
            if(-not [FemeshTableWindow]::SetForegroundWindow($hostProcess.MainWindowHandle)){
                throw 'Cannot focus the owned SAP2000 window.'
            }
            $null=[FemeshTableWindow]::SetCursorPos(
                [int]($viewBounds.Left+$viewBounds.Width/2),
                [int]($viewBounds.Top+$viewBounds.Height/2))
            [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
            [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
            [Windows.Forms.SendKeys]::SendWait('^a')
            Start-Sleep -Milliseconds 300
            'GUI_SELECT_ALL=INVOKED'
        }
        $menus=$window.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition)
        $pattern=$null
        foreach($menu in $menus){
            if($menu.Current.Name -eq 'Display' -or $menu.Current.Name -match 'Show Tables'){
                "DISPLAY_MENU_CONTROL=$($menu.Current.ControlType.ProgrammaticName) NAME=$($menu.Current.Name)"
                if($menu.Current.Name -eq 'Display' -and $menu.TryGetCurrentPattern(
                    [Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$pattern)){
                    $pattern.Expand()
                    Start-Sleep -Milliseconds 150
                    $popup=[Windows.Automation.AutomationElement]::RootElement.FindAll(
                        [Windows.Automation.TreeScope]::Descendants,
                        [Windows.Automation.PropertyCondition]::new(
                            [Windows.Automation.AutomationElement]::ProcessIdProperty,$ownedPid))
                    foreach($item in $popup){
                        if($item.Current.ControlType -eq [Windows.Automation.ControlType]::MenuItem){
                            "DISPLAY_ITEM=$($item.Current.Name)"
                            if(($InspectTablesDialog -or $SelectJointTable) -and
                                $item.Current.Name -eq 'Show Tables...'){
                                $bounds=$item.Current.BoundingRectangle
                                if($bounds.Width -lt 30 -or $bounds.Height -lt 10 -or
                                    $bounds.Left -lt 0 -or $bounds.Top -lt 0){
                                    throw 'Show Tables UI item has unsafe bounds.'
                                }
                                $clickX=[int]($bounds.Left+$bounds.Width/2)
                                $clickY=[int]($bounds.Top+$bounds.Height/2)
                                "SHOW_TABLES_CLICK=$clickX,$clickY"
                                if(-not [FemeshTableWindow]::SetCursorPos($clickX,$clickY)){
                                    throw 'Cannot position cursor on Show Tables menu item.'
                                }
                                [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                Start-Sleep -Milliseconds 600
                                $windows=[Collections.Generic.List[object]]::new()
                                $visit=[FemeshTableWindow+Visit]{param($hwnd,$unused)
                                    [uint32]$pidValue=0
                                    $null=[FemeshTableWindow]::GetWindowThreadProcessId($hwnd,[ref]$pidValue)
                                    if($pidValue -ne $ownedPid -or -not [FemeshTableWindow]::IsWindowVisible($hwnd)){
                                        return $true
                                    }
                                    $title=[Text.StringBuilder]::new(400)
                                    $class=[Text.StringBuilder]::new(100)
                                    $null=[FemeshTableWindow]::GetWindowText($hwnd,$title,$title.Capacity)
                                    $null=[FemeshTableWindow]::GetClassName($hwnd,$class,$class.Capacity)
                                    $windows.Add([pscustomobject]@{Handle=$hwnd;Title=$title.ToString();Class=$class.ToString()})
                                    return $true
                                }
                                $null=[FemeshTableWindow]::EnumWindows($visit,[IntPtr]::Zero)
                                foreach($top in $windows){
                                    "TABLE_WINDOW=$($top.Title) CLASS=$($top.Class) HANDLE=$($top.Handle)"
                                    $children=[Collections.Generic.List[string]]::new()
                                    $script:selectionOnlyHandle=[IntPtr]::Zero
                                    $script:tableOkHandle=[IntPtr]::Zero
                                    $script:tableTreeHandle=[IntPtr]::Zero
                                    $childVisit=[FemeshTableWindow+Visit]{param($hwnd,$unused)
                                        $title=[Text.StringBuilder]::new(300)
                                        $class=[Text.StringBuilder]::new(100)
                                        $null=[FemeshTableWindow]::GetWindowText($hwnd,$title,$title.Capacity)
                                        $null=[FemeshTableWindow]::GetClassName($hwnd,$class,$class.Capacity)
                                        if($class.ToString() -match 'TreeView'){$script:tableTreeHandle=$hwnd}
                                        if($title.Length){
                                            $children.Add($class.ToString()+' '+$title.ToString())
                                            if($title.ToString() -eq 'Selection Only'){
                                                $script:selectionOnlyHandle=$hwnd
                                                $checked=[FemeshTableWindow]::SendMessage($hwnd,0xF0,
                                                    [IntPtr]::Zero,[IntPtr]::Zero).ToInt64()
                                                $children.Add('SELECTION_ONLY_CHECKED='+$checked)
                                            }
                                            if($title.ToString() -eq 'OK'){$script:tableOkHandle=$hwnd}
                                        }
                                        return $true
                                    }
                                    $null=[FemeshTableWindow]::EnumChildWindows($top.Handle,$childVisit,[IntPtr]::Zero)
                                    foreach($child in $children){"TABLE_CONTROL=$child"}
                                    if($top.Title -like 'Choose Tables*'){
                                        try{
                                            $popupElement=[Windows.Automation.AutomationElement]::FromHandle($top.Handle)
                                            $tree=$popupElement.FindAll([Windows.Automation.TreeScope]::Descendants,
                                                [Windows.Automation.Condition]::TrueCondition)
                                            foreach($control in $tree){
                                                if($control.Current.Name -match 'Joint|Model Definition|Point|Coordinates|Tree|Selection Only'){
                                                    "TABLE_TREE=$($control.Current.ControlType.ProgrammaticName) NAME=$($control.Current.Name)"
                                                }
                                            }
                                            $modelDefinition=@($tree|Where-Object {
                                                $_.Current.ControlType -eq [Windows.Automation.ControlType]::TreeItem -and
                                                $_.Current.Name -eq 'MODEL DEFINITION'
                                            })
                                            if($modelDefinition.Count -eq 1){
                                                $nodeBounds=$modelDefinition[0].Current.BoundingRectangle
                                                $treeX=[int]($nodeBounds.Left+8)
                                                $treeY=[int]($nodeBounds.Top+$nodeBounds.Height/2)
                                                "TABLE_TREE_EXPAND_CLICK=$treeX,$treeY"
                                                if(-not [FemeshTableWindow]::SetCursorPos($treeX,$treeY)){
                                                    throw 'Cannot reach the model definition tree toggle.'
                                                }
                                                [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                                [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                                Start-Sleep -Milliseconds 500
                                                $expanded=$popupElement.FindAll([Windows.Automation.TreeScope]::Descendants,
                                                    [Windows.Automation.PropertyCondition]::new(
                                                        [Windows.Automation.AutomationElement]::ControlTypeProperty,
                                                        [Windows.Automation.ControlType]::TreeItem))
                                                foreach($control in $expanded){"TABLE_TREE_EXPANDED=$($control.Current.Name)"}
                                                $connectivity=@($expanded|Where-Object {$_.Current.Name -eq 'Connectivity Data'})
                                                if($connectivity.Count -eq 1){
                                                    $nodeBounds=$connectivity[0].Current.BoundingRectangle
                                                    $collapse=$null
                                                    if($connectivity[0].TryGetCurrentPattern(
                                                        [Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$collapse)){
                                                        "CONNECTIVITY_BEFORE=$($collapse.Current.ExpandCollapseState) BOUNDS=$nodeBounds"
                                                    }
                                                    $treeX=[int]($nodeBounds.Left-8)
                                                    $treeY=[int]($nodeBounds.Top+$nodeBounds.Height/2)
                                                    "CONNECTIVITY_EXPAND_CLICK=$treeX,$treeY"
                                                    $null=[FemeshTableWindow]::SetCursorPos($treeX,$treeY)
                                                    [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                                    [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                                    Start-Sleep -Milliseconds 500
                                                    if($collapse){"CONNECTIVITY_AFTER=$($collapse.Current.ExpandCollapseState)"}
                                                    if($collapse -and $collapse.Current.ExpandCollapseState -eq
                                                        [Windows.Automation.ExpandCollapseState]::Collapsed){
                                                        $treeX=[int]($nodeBounds.Left+$nodeBounds.Width/2)
                                                        $null=[FemeshTableWindow]::SetCursorPos($treeX,$treeY)
                                                        foreach($click in 1..2){
                                                            [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                                            [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                                        }
                                                        Start-Sleep -Milliseconds 500
                                                        "CONNECTIVITY_AFTER_DOUBLE_CLICK=$($collapse.Current.ExpandCollapseState)"
                                                    }
                                                    $expanded=$popupElement.FindAll([Windows.Automation.TreeScope]::Descendants,
                                                        [Windows.Automation.PropertyCondition]::new(
                                                            [Windows.Automation.AutomationElement]::ControlTypeProperty,
                                                            [Windows.Automation.ControlType]::TreeItem))
                                                    foreach($control in $expanded){"CONNECTIVITY_TREE=$($control.Current.Name)"}
                                                    $jointCoordinates=@($expanded|Where-Object {$_.Current.Name -eq 'Joint Coordinates'})
                                                    if($jointCoordinates.Count -eq 1){
                                                        $toggle=$null
                                                        if($jointCoordinates[0].TryGetCurrentPattern(
                                                            [Windows.Automation.TogglePattern]::Pattern,[ref]$toggle)){
                                                            "JOINT_COORDINATES_TOGGLE=$($toggle.Current.ToggleState) BOUNDS=$($jointCoordinates[0].Current.BoundingRectangle)"
                                                        }else{
                                                            "JOINT_COORDINATES_BOUNDS=$($jointCoordinates[0].Current.BoundingRectangle)"
                                                        }
                                                        if($SelectJointTable){
                                                            if($script:selectionOnlyHandle -eq [IntPtr]::Zero -or
                                                                $script:tableOkHandle -eq [IntPtr]::Zero){
                                                                throw 'Missing verified Selection Only/OK controls in SAP dialog.'
                                                            }
                                                            $jointBounds=$jointCoordinates[0].Current.BoundingRectangle
                                                            $jointX=[int]($jointBounds.Left-8)
                                                            $jointY=[int]($jointBounds.Top+$jointBounds.Height/2)
                                                            "JOINT_COORDINATES_CLICK=$jointX,$jointY"
                                                            $null=[FemeshTableWindow]::SetCursorPos($jointX,$jointY)
                                                            [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                                            [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                                            Start-Sleep -Milliseconds 300
                                                            "JOINT_COORDINATES_ENABLED=$($jointCoordinates[0].Current.IsEnabled) TREE_HANDLE=$script:tableTreeHandle"
                                                            $selectionPattern=$null
                                                            if($jointCoordinates[0].TryGetCurrentPattern(
                                                                [Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selectionPattern)){
                                                                "JOINT_COORDINATES_SELECTED=$($selectionPattern.Current.IsSelected)"
                                                            }
                                                            if($script:tableTreeHandle -ne [IntPtr]::Zero){
                                                                $jointCoordinates[0].SetFocus()
                                                                $selectedItem=[FemeshTableWindow]::SendMessage($script:tableTreeHandle,
                                                                    0x110A,[IntPtr]::new(9),[IntPtr]::Zero)
                                                                $state=[FemeshTableWindow]::SendMessage($script:tableTreeHandle,
                                                                    0x1127,$selectedItem,[IntPtr]::new(0xF000)).ToInt64()
                                                                "JOINT_TREE_ITEM=$selectedItem STATE_IMAGE_BEFORE=$($state -shr 12)"
                                                                $null=[FemeshTableWindow]::PostMessage($script:tableTreeHandle,
                                                                    0x100,[IntPtr]::new(0x20),[IntPtr]::Zero)
                                                                $null=[FemeshTableWindow]::PostMessage($script:tableTreeHandle,
                                                                    0x101,[IntPtr]::new(0x20),[IntPtr]::Zero)
                                                                Start-Sleep -Milliseconds 200
                                                                $state=[FemeshTableWindow]::SendMessage($script:tableTreeHandle,
                                                                    0x1127,$selectedItem,[IntPtr]::new(0xF000)).ToInt64()
                                                                "JOINT_TREE_STATE_IMAGE_AFTER=$($state -shr 12)"
                                                            }
                                                            $checked=[FemeshTableWindow]::SendMessage($script:selectionOnlyHandle,
                                                                0xF0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()
                                                            if($checked -ne 1){
                                                                $option=@($tree|Where-Object {
                                                                    $_.Current.ControlType -eq [Windows.Automation.ControlType]::CheckBox -and
                                                                    $_.Current.Name -eq 'Selection Only'
                                                                })
                                                                if($option.Count -ne 1){throw 'Missing selected-only checkbox in SAP UIA tree.'}
                                                                $optionBounds=$option[0].Current.BoundingRectangle
                                                                "GUI_SELECTION_ONLY_BOUNDS=$optionBounds ENABLED=$($option[0].Current.IsEnabled)"
                                                                $null=[FemeshTableWindow]::SetCursorPos(
                                                                    [int]($optionBounds.Left+10),
                                                                    [int]($optionBounds.Top+$optionBounds.Height/2))
                                                                [FemeshTableWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
                                                                [FemeshTableWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
                                                                Start-Sleep -Milliseconds 350
                                                            }
                                                            $checked=[FemeshTableWindow]::SendMessage($script:selectionOnlyHandle,
                                                                0xF0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()
                                                            if($checked -ne 1){
                                                                $option=@($tree|Where-Object {
                                                                    $_.Current.ControlType -eq [Windows.Automation.ControlType]::CheckBox -and
                                                                    $_.Current.Name -eq 'Selection Only'
                                                                })
                                                                $optionPattern=$null
                                                                if($option.Count -eq 1 -and $option[0].TryGetCurrentPattern(
                                                                    [Windows.Automation.TogglePattern]::Pattern,[ref]$optionPattern)){
                                                                    "GUI_SELECTION_ONLY_UIA_BEFORE=$($optionPattern.Current.ToggleState)"
                                                                    $optionPattern.Toggle()
                                                                    "GUI_SELECTION_ONLY_UIA_AFTER=$($optionPattern.Current.ToggleState)"
                                                                    $checked=[FemeshTableWindow]::SendMessage($script:selectionOnlyHandle,
                                                                        0xF0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()
                                                                }
                                                            }
                                                            if($checked -ne 1 -and $optionPattern -and
                                                                $option[0].Current.IsEnabled -and
                                                                $optionPattern.Current.ToggleState -eq
                                                                    [Windows.Automation.ToggleState]::On){
                                                                # SAP exposes this WinForms checkbox through UIA even when
                                                                # BM_GETCHECK is not wired to the owner-drawn state.
                                                                $checked=1
                                                            }
                                                            if($checked -ne 1){
                                                                $screen=$popupElement.Current.BoundingRectangle
                                                                $bitmap=[Drawing.Bitmap]::new([int]$screen.Width,[int]$screen.Height)
                                                                try{
                                                                    $graphics=[Drawing.Graphics]::FromImage($bitmap)
                                                                    try{$graphics.CopyFromScreen([int]$screen.Left,[int]$screen.Top,
                                                                        0,0,$bitmap.Size)}finally{$graphics.Dispose()}
                                                                    $bitmap.Save((Join-Path $ResultsRoot 'table-dialog-before-filter.png'),
                                                                        [Drawing.Imaging.ImageFormat]::Png)
                                                                }finally{$bitmap.Dispose()}
                                                                throw 'Selected-only SAP table filter was not enabled.'
                                                            }
                                                            'GUI_SELECTION_ONLY=ON'
                                                            $null=[FemeshTableWindow]::PostMessage($script:tableOkHandle,
                                                                0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
                                                            Start-Sleep -Seconds 2
                                                            $viewWindows=[Collections.Generic.List[string]]::new()
                                                            $viewVisit=[FemeshTableWindow+Visit]{param($hwnd,$unused)
                                                                [uint32]$ownerPid=0
                                                                $null=[FemeshTableWindow]::GetWindowThreadProcessId($hwnd,[ref]$ownerPid)
                                                                if($ownerPid -eq $ownedPid -and [FemeshTableWindow]::IsWindowVisible($hwnd)){
                                                                    $title=[Text.StringBuilder]::new(350)
                                                                    $null=[FemeshTableWindow]::GetWindowText($hwnd,$title,$title.Capacity)
                                                                    if($title.Length){$viewWindows.Add($title.ToString())}
                                                                }
                                                                return $true
                                                            }
                                                            $null=[FemeshTableWindow]::EnumWindows($viewVisit,[IntPtr]::Zero)
                                                            foreach($title in $viewWindows){"AFTER_GUI_TABLE_WINDOW=$title"}
                                                            if(-not @($viewWindows|Where-Object {$_ -match '^Joint Coordinates(?:\b|$)'}).Count){
                                                                throw 'SAP did not actually display the Joint Coordinates table.'
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }catch{
                                            if($SelectJointTable){throw}
                                            "TABLE_TREE_INSPECTION_ERROR=$($_.Exception.Message)"
                                        }
                                    }
                                }
                                return
                            }
                        }
                    }
                    $pattern.Collapse()
                }
            }
        }
        return
    }
    # File.OpenFile discards the in-memory state without saving it into the original file.
    if($model.File.OpenFile($secondCopy) -ne 0){throw 'Cannot open second disposable model.'}
    if($model.SelectObj.All($true) -ne 0){throw 'Cannot select all in second model.'}
    $areaCount=0;$liveAreas=$null;$editVersion=0;$editFields=$null;$editRows=0;$editData=$null
    if($model.AreaObj.GetNameList([ref]$areaCount,[ref]$liveAreas) -ne 0){throw 'Second model area inventory failed.'}
    $edit=$model.DatabaseTables.GetTableForEditingArray('Connectivity - Area','',
        [ref]$editVersion,[ref]$editFields,[ref]$editRows,[ref]$editData)
    "SECOND_AREAS=$areaCount EDIT_CONNECTIVITY_CODE=$edit EDIT_ROWS=$editRows EDIT_FIELDS=$($editFields -join '|')"
    foreach($group in @('All','ALL')){
        $gv=0;$gf=$null;$gr=0;$gd=$null
        $groupCode=$model.DatabaseTables.GetTableForEditingArray('Connectivity - Area',$group,
            [ref]$gv,[ref]$gf,[ref]$gr,[ref]$gd)
        "SECOND_GROUP=$group EDIT_CODE=$groupCode EDIT_ROWS=$gr FIELDS=$($gf -join '|')"
    }
    if($ExerciseFilteredGroup){
        $subset='FEMESH_SNAPSHOT_FILTER_PROBE'
        $setGroup=[SAP2000v1.cGroup].GetMethod('SetGroup')
        $groupArgs=[object[]]@($subset)
        $groupArgs+=@($setGroup.GetParameters() | Select-Object -Skip 1 |
            ForEach-Object { $_.DefaultValue })
        if([int]$setGroup.Invoke($model.GroupDef.PSObject.BaseObject,$groupArgs) -ne 0 -or
           $model.AreaObj.SetGroupAssign($liveAreas[0],$subset,$false,
               [SAP2000v1.eItemType]::Objects) -ne 0){throw 'Cannot set up disposable one-area group.'}
        $sv=0;$sf=$null;$sr=0;$sd=$null
        $subsetCode=$model.DatabaseTables.GetTableForEditingArray('Connectivity - Area',$subset,
            [ref]$sv,[ref]$sf,[ref]$sr,[ref]$sd)
        if($subsetCode -ne 0 -or $sr -lt 1 -or $sr -ge $editRows){
            throw "A group-scoped editing table did not filter the model ($subsetCode/$sr/$editRows)."
        }
        "FILTERED_GROUP_ROWS=$sr FULL_MODEL_ROWS=$editRows"
    }
    $snapshotType=[Reflection.Assembly]::LoadFrom($Plugin).GetType('SapQuadTransitionPlugin.ModelCsvSnapshot',$true)
    $m=(New-Object CSiAPIv1.Helper).GetObjectProcess('CSI.SAP2000.API.SapObject',
        [int][RunTest01Fast].GetField('persistentSapPid',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)).SapModel
    $capture=$snapshotType.GetMethod('Capture',[Reflection.BindingFlags]'Static,NonPublic')
    try{
        $snapshot=$capture.Invoke($null,[object[]]@($m,$null))
        'SECOND_SNAPSHOT=PASS'
        $snapshot.GetType().GetMethod('Delete',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($snapshot,[object[]]@())|Out-Null
    }catch{
        $error=if($_.Exception.InnerException){$_.Exception.InnerException}else{$_.Exception}
        "SECOND_SNAPSHOT_ERROR=$($error.Message)"
    }
    if($MeshSecond){
        if($model.PointObj.GetNameList([ref]$points,[ref]$pointNames) -ne 0){throw 'Cannot select second model joints.'}
        $secondAreas=[string[]]@($liveAreas | Where-Object { $_ })
        $secondPoints=[string[]]@($pointNames | Where-Object { $_ })
        $secondFrames=[string[]]@()
        $env:SAP_QUAD_ATOMIC_ALLOW_UNCONNECTED_SELECTED_POINTS='1'
        $env:SAP_GMSH_V2_SETTINGS_PATH=Join-Path $ResultsRoot 'isolated-second-options.json'
        [RunTest01Fast]::RunAtomicExampleWithTimeout($secondCopy,
            (Join-Path $ResultsRoot 'BUG_02_2-applied.sdb'),$Plugin,
            $secondAreas,$secondPoints,$secondFrames,
            '0.3m','','0.05m','',$false,$true,'General Purpose','xy','',$MeshTimeoutSeconds)
        'SECOND_MESH=PASS SIZE=0.3m'
    }
}finally{
    foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}
    if($started){[RunTest01Fast]::StopIsolatedPersistentSap()}
    if($ownedPid){
        $owned=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if($owned -and $owned.ProcessName -eq 'SAP2000'){
            Stop-Process -Id $ownedPid -Force -ErrorAction SilentlyContinue
        }
    }
    if((Get-FileHash -LiteralPath $First -Algorithm SHA256).Hash -ne $originalHashes[0] -or
       (Get-FileHash -LiteralPath $Second -Algorithm SHA256).Hash -ne $originalHashes[1]){
        throw 'An original BUG_02 SDB changed.'
    }
}
