param([string]$Installer=(Join-Path $PSScriptRoot 'bin\Release\net48\SQMPluginInstaller.exe'))
$ErrorActionPreference='Stop'
$parent=Join-Path $env:TEMP 'opencode'
if(-not(Test-Path -LiteralPath $parent -PathType Container) -or
   -not(Test-Path -LiteralPath $Installer -PathType Leaf)){throw 'Installer or test parent missing.'}
$profiles=@(
    @{ Host='SAP2000'; Name='SAP2000 27'; Ini='SAP2000v27.ini'; Settings='SAP2000v27.settings'; Command='test-menu' },
    @{ Host='CSiBridge'; Name='CSiBridge 27'; Ini='CSiBridgev27.ini'; Settings='CSiBridgev27.settings'; Command='test-menu-bridge' }
)
foreach($item in $profiles){
    $profile=Join-Path $env:LOCALAPPDATA ('Computers and Structures\'+$item.Name)
    $trial=Join-Path $parent ('meshing-menu-'+$item.Host+'-'+[guid]::NewGuid().ToString('N'))
    if(-not(Test-Path -LiteralPath (Join-Path $profile $item.Ini) -PathType Leaf) -or
       -not(Test-Path -LiteralPath (Join-Path $profile $item.Settings) -PathType Leaf) -or
       (Test-Path -LiteralPath $trial)){throw "Missing or unsafe $($item.Host) profile."}
    New-Item -ItemType Directory -Path $trial|Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $profile $item.Ini) -Destination $trial
        Copy-Item -LiteralPath (Join-Path $profile $item.Settings) -Destination $trial
        foreach($iteration in 1..2){
            & $Installer $item.Command $trial
            if($LASTEXITCODE -ne 0){throw "$($item.Host) menu migration failed."}
        }
        $ini=[IO.File]::ReadAllText((Join-Path $trial $item.Ini))
        $section=[regex]::Match($ini,'(?ms)^\[PlugIn\]\r?\n.*?(?=^\[|\z)').Value
        $names=[regex]::Matches($section,'(?m)^\s*PlugInName=([^\r\n]*)')
        $labels=[regex]::Matches($section,'(?m)^\s*PlugInMenuText=([^\r\n]*)')
        $count=[regex]::Match($section,'(?m)^\s*NumberPlugIns=(\d+)').Groups[1].Value
        $plugin=@($names | Where-Object { $_.Groups[1].Value.Trim() -eq 'SapQuadTransitionPlugin' })
        if([int]$count -ne $names.Count -or $names.Count -ne $labels.Count -or
            $plugin.Count -ne 1 -or $labels[[array]::IndexOf(@($names),$plugin[0])].Groups[1].Value.Trim() -ne 'MESHing'){
            throw "$($item.Host) INI label or count differs."
        }
        [xml]$settings=Get-Content -LiteralPath (Join-Path $trial $item.Settings) -Raw
        [xml]$entries=$settings.Settings.PlugIn
        $meshing=@($entries.ArrayOfString.string | ForEach-Object {
            [xml]$record=$_.TrimStart([char]0xfeff,[char]' ',[char]13,[char]10)
            if($record.PlugIn.m_name -eq 'SapQuadTransitionPlugin'){$record.PlugIn.m_Text}
        })
        if($meshing.Count -ne 1 -or $meshing[0] -ne 'MESHing'){
            throw "$($item.Host) settings lack MESHing entry."
        }
        "MESHING_MENU=$($item.Host) PASS TWO_INSTALLS=YES"
    } finally {
        if(Test-Path -LiteralPath $trial){Remove-Item -LiteralPath $trial -Recurse -Force}
    }
}
