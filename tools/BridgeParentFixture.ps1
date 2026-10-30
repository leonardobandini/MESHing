# CSiBridge xAPI fixture for all readable parent-to-child shell assignments.
# Dot-source only from TestFEMeshHosts.ps1 with -TransferFixture.
function Assert-BridgeApi([int]$code,[string]$action){
    if($code -ne 0){throw "CSiBridge parent-child fixture: $action returned $code"}
}
function BridgePoint([object]$model,[string]$name){
    $x=0.0;$y=0.0;$z=0.0
    Assert-BridgeApi $model.PointObj.GetCoordCartesian($name,[ref]$x,[ref]$y,[ref]$z,'Global') "point $name"
    return [double[]]@($x,$y,$z)
}
function BridgeLength([double[]]$a,[double[]]$b){
    return [Math]::Sqrt([Math]::Pow($a[0]-$b[0],2)+[Math]::Pow($a[1]-$b[1],2)+[Math]::Pow($a[2]-$b[2],2))
}
function BridgeEdgeLength([object]$model,[string]$area,[int]$face){
    $count=0;$points=$null
    Assert-BridgeApi $model.AreaObj.GetPoints($area,[ref]$count,[ref]$points) "edge points $area"
    if($face -lt 1 -or $face -gt $count){throw "Invalid child face $face"}
    $first=BridgePoint $model $points[$face-1]
    $second=BridgePoint $model $points[$face%$count]
    return BridgeLength $first $second
}
function Set-BridgeParentFixture([object]$model,[string]$parent){
    $script:BridgeFixtureGroup='SQM_PARENT_FIXTURE_GROUP'
    $script:BridgeFixtureLoad='SQM_PARENT_FIXTURE_LOAD'
    $section=''
    Assert-BridgeApi $model.AreaObj.GetProperty($parent,[ref]$section) 'parent section'
    $script:BridgeFixtureSection=$section
    Assert-BridgeApi $model.GroupDef.SetGroup($script:BridgeFixtureGroup,-1,
        $true,$true,$true,$true,$true,$true,$true,$true,$false,$false,$true) 'group'
    Assert-BridgeApi $model.AreaObj.SetGroupAssign($parent,$script:BridgeFixtureGroup,$false,[CSiAPIv1.eItemType]::Objects) 'group assignment'
    Assert-BridgeApi $model.LoadPatterns.Add($script:BridgeFixtureLoad,[CSiAPIv1.eLoadPatternType]::Other,0,$true) 'load pattern'
    Assert-BridgeApi $model.AreaObj.SetLoadUniform($parent,$script:BridgeFixtureLoad,3.75,6,$true,'Global',[CSiAPIv1.eItemType]::Objects) 'uniform load'
    Assert-BridgeApi $model.AreaObj.SetLoadSurfacePressure($parent,$script:BridgeFixtureLoad,-1,1.5,'',$true,[CSiAPIv1.eItemType]::Objects) 'bottom pressure'
    $vector=[double[]]@(0,0,0)
    Assert-BridgeApi $model.AreaObj.SetSpring($parent,1,725,1,'',-1,1,3,$true,[ref]$vector,0,$true,'Local',[CSiAPIv1.eItemType]::Objects) 'surface spring'
    $modifiers=$null
    Assert-BridgeApi $model.AreaObj.GetModifiers($parent,[ref]$modifiers) 'parent modifiers'
    if($modifiers.Count -ne 10){throw 'CSiBridge returned an incomplete area modifier vector.'}
    $modifiers[0]=0.72
    $script:BridgeFixtureModifiers=@($modifiers)
    Assert-BridgeApi $model.AreaObj.SetModifiers($parent,[ref]$modifiers,[CSiAPIv1.eItemType]::Objects) 'parent modifiers'
    Assert-BridgeApi $model.AreaObj.SetLocalAxes($parent,12.5,[CSiAPIv1.eItemType]::Objects) 'parent axes'
    Assert-BridgeApi $model.AreaObj.SetMass($parent,23,$true,[CSiAPIv1.eItemType]::Objects) 'parent mass'
    Assert-BridgeApi $model.AreaObj.SetEdgeConstraint($parent,$true,[CSiAPIv1.eItemType]::Objects) 'parent edge constraint'
    $pointCount=0;$points=$null
    Assert-BridgeApi $model.AreaObj.GetPoints($parent,[ref]$pointCount,[ref]$points) 'parent edge'
    $script:BridgeFixtureEdge=[double[][]]@((BridgePoint $model $points[0]),(BridgePoint $model $points[1]))
    $vector=[double[]]@(0,0,0)
    Assert-BridgeApi $model.AreaObj.SetSpring($parent,1,455,1,'',1,1,3,$true,[ref]$vector,0,$false,'Local',[CSiAPIv1.eItemType]::Objects) 'edge spring'
    Assert-BridgeApi $model.AreaObj.SetLoadSurfacePressure($parent,$script:BridgeFixtureLoad,1,2.25,'',$false,[CSiAPIv1.eItemType]::Objects) 'edge pressure'
    $materialCount=0;$materials=$null
    Assert-BridgeApi $model.PropMaterial.GetNameList([ref]$materialCount,[ref]$materials,
        [Enum]::ToObject([CSiAPIv1.eMatType],0)) 'materials'
    if($materialCount -lt 1){throw 'No Bridge fixture material.'}
    $script:BridgeFixtureMaterial=$materials[0]
    Assert-BridgeApi $model.AreaObj.SetMaterialOverwrite($parent,$script:BridgeFixtureMaterial,[CSiAPIv1.eItemType]::Objects) 'material overwrite'
    Assert-BridgeApi $model.AreaObj.SetMatTemp($parent,26.5,'',[CSiAPIv1.eItemType]::Objects) 'temperature'
    "CSiBridge PARENT_FIXTURE_ASSIGNED=$parent"
}
function Assert-BridgeParentFixture([object]$model,[string[]]$areas){
    if($areas.Count -lt 2){throw 'Fixture generated no child shell areas.'}
    $springLength=0.0;$pressureLength=0.0
    foreach($area in $areas){
        $groupCount=0;$groups=$null
        Assert-BridgeApi $model.AreaObj.GetGroupAssign($area,[ref]$groupCount,[ref]$groups) "groups $area"
        $modifiers=$null
        Assert-BridgeApi $model.AreaObj.GetModifiers($area,[ref]$modifiers) "modifiers $area"
        $section=''
        Assert-BridgeApi $model.AreaObj.GetProperty($area,[ref]$section) "section $area"
        $angle=0.0;$advanced=$false;$mass=0.0;$edge=$false
        Assert-BridgeApi $model.AreaObj.GetLocalAxes($area,[ref]$angle,[ref]$advanced) "axes $area"
        Assert-BridgeApi $model.AreaObj.GetMass($area,[ref]$mass) "mass $area"
        Assert-BridgeApi $model.AreaObj.GetEdgeConstraint($area,[ref]$edge) "edge constraint $area"
        $material='';$temp=0.0;$tempPattern=''
        Assert-BridgeApi $model.AreaObj.GetMaterialOverwrite($area,[ref]$material) "material overwrite $area"
        Assert-BridgeApi $model.AreaObj.GetMatTemp($area,[ref]$temp,[ref]$tempPattern) "temperature $area"
        $uniformCount=0;$objects=$null;$patterns=$null;$systems=$null;$directions=$null;$values=$null
        Assert-BridgeApi $model.AreaObj.GetLoadUniform($area,[ref]$uniformCount,[ref]$objects,
            [ref]$patterns,[ref]$systems,[ref]$directions,[ref]$values,[CSiAPIv1.eItemType]::Objects) "uniform load $area"
        $pressureCount=0;$pressureObjects=$null;$pressurePatterns=$null;$pressureFaces=$null;$pressureValues=$null;$pressurePatternNames=$null
        Assert-BridgeApi $model.AreaObj.GetLoadSurfacePressure($area,[ref]$pressureCount,[ref]$pressureObjects,
            [ref]$pressurePatterns,[ref]$pressureFaces,[ref]$pressureValues,[ref]$pressurePatternNames,[CSiAPIv1.eItemType]::Objects) "pressure $area"
        $springCount=0;$springTypes=$null;$stiffness=$null;$simpleTypes=$null;$links=$null;$faces=$null
        $axisTypes=$null;$springDirections=$null;$outward=$null;$vecX=$null;$vecY=$null;$vecZ=$null
        $springSystems=$null;$springAngles=$null
        Assert-BridgeApi $model.AreaObj.GetSpring($area,[ref]$springCount,[ref]$springTypes,
            [ref]$stiffness,[ref]$simpleTypes,[ref]$links,[ref]$faces,[ref]$axisTypes,
            [ref]$springDirections,[ref]$outward,[ref]$vecX,[ref]$vecY,[ref]$vecZ,
            [ref]$springSystems,[ref]$springAngles) "springs $area"
        $hasSurfaceSpring=$false;$hasSurfacePressure=$false
        for($i=0;$i -lt $springCount;$i++){
            if($faces[$i] -eq -1 -and [Math]::Abs($stiffness[$i]-725) -lt 1e-7){$hasSurfaceSpring=$true}
            if($faces[$i] -gt 0 -and [Math]::Abs($stiffness[$i]-455) -lt 1e-7){
                $springLength+=BridgeEdgeLength $model $area $faces[$i]
            }
        }
        for($i=0;$i -lt $pressureCount;$i++){
            if($pressureFaces[$i] -eq -1 -and [Math]::Abs($pressureValues[$i]-1.5) -lt 1e-7 -and
               $pressurePatterns[$i] -eq $script:BridgeFixtureLoad){$hasSurfacePressure=$true}
            if($pressureFaces[$i] -gt 0 -and [Math]::Abs($pressureValues[$i]-2.25) -lt 1e-7 -and
               $pressurePatterns[$i] -eq $script:BridgeFixtureLoad){
                $pressureLength+=BridgeEdgeLength $model $area $pressureFaces[$i]
            }
        }
        if($script:BridgeFixtureGroup -notin $groups -or $modifiers.Count -ne $script:BridgeFixtureModifiers.Count -or
           @((0..($modifiers.Count-1))|Where-Object {[Math]::Abs($modifiers[$_]-$script:BridgeFixtureModifiers[$_]) -gt 1e-7}).Count -or
           $section -ne $script:BridgeFixtureSection -or [Math]::Abs($angle-12.5) -gt 1e-7 -or
           [Math]::Abs($mass-23) -gt 1e-7 -or -not $edge -or $material -ne $script:BridgeFixtureMaterial -or
           [Math]::Abs($temp-26.5) -gt 1e-7 -or $uniformCount -ne 1 -or
           $patterns[0] -ne $script:BridgeFixtureLoad -or [Math]::Abs($values[0]-3.75) -gt 1e-7 -or
           -not $hasSurfaceSpring -or -not $hasSurfacePressure){
            throw "CSiBridge parent-child assignments changed on $area"
        }
    }
    $expected=BridgeLength $script:BridgeFixtureEdge[0] $script:BridgeFixtureEdge[1]
    if([Math]::Abs($springLength-$expected) -gt 1e-5*$expected -or
       [Math]::Abs($pressureLength-$expected) -gt 1e-5*$expected){
        throw "CSiBridge parent edge assignments incomplete: spring=$springLength pressure=$pressureLength expected=$expected"
    }
    "CSiBridge PARENT_EDGE_ASSIGNMENTS_PASS length=$expected spring=$springLength pressure=$pressureLength"
    "CSiBridge PARENT_CHILD_ASSIGNMENTS_PASS shells=$($areas.Count)"
}
