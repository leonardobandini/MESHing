# Exercise the actual installer host scanner on isolated 27/28/29/31 profiles.
param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
$parent=Join-Path $env:TEMP 'opencode'
if(-not(Test-Path -LiteralPath $parent -PathType Container) -or
    -not(Test-Path -LiteralPath $Installer -PathType Leaf)){throw 'Missing test parent or installer.'}
    $trial=Join-Path $parent ('meshing-host-discovery-'+[guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $trial){throw 'Test destination already exists.'}
New-Item -ItemType Directory -Path $trial|Out-Null
try{
    $installs=Join-Path $trial 'installed-hosts'
    $profiles=Join-Path $trial 'host-profiles'
    New-Item -ItemType Directory -Path $installs,$profiles|Out-Null
    $original=@{}
    foreach($product in @('SAP2000','CSiBridge')){
        $live=Join-Path $env:LOCALAPPDATA ('Computers and Structures\'+$product+' 27')
        foreach($suffix in @('ini','settings')){
            $file=$product+'v27.'+$suffix
            if(-not(Test-Path -LiteralPath (Join-Path $live $file) -PathType Leaf)){
                throw 'A real 27 profile is needed as a disposable menu fixture: '+$file
            }
            $original[(Join-Path $live $file)]=(Get-FileHash -LiteralPath (Join-Path $live $file) -Algorithm SHA256).Hash
        }
        foreach($version in @(26,27,28,29,31)){
            $hostName=$product+' '+$version
            $binary=Join-Path $installs $hostName
            $profile=Join-Path $profiles $hostName
            New-Item -ItemType Directory -Path $binary,$profile|Out-Null
            [IO.File]::WriteAllBytes((Join-Path $binary ($product+'.exe')),[byte[]]@(1))
            [IO.File]::WriteAllBytes((Join-Path $binary 'CSiAPIv1.dll'),[byte[]]@(1))
            foreach($suffix in @('ini','settings')){
                Copy-Item -LiteralPath (Join-Path $live ($product+'v27.'+$suffix)) -Destination (Join-Path $profile ($product+'v'+$version+'.'+$suffix))
            }
        }
    }
    $etabs=Join-Path $installs 'ETABS 28'
    $etabsProfile=Join-Path $profiles 'ETABS 28'
    New-Item -ItemType Directory -Path $etabs,$etabsProfile|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $etabs 'ETABS.exe'),[byte[]]@(1))
    [IO.File]::WriteAllBytes((Join-Path $etabs 'CSiAPIv1.dll'),[byte[]]@(1))
    $sentinel=Join-Path $etabsProfile 'untouched.ini'
    [IO.File]::WriteAllText($sentinel,'Do not register ETABS.')
    $uninitialized=Join-Path $installs 'SAP2000 32'
    New-Item -ItemType Directory -Path $uninitialized|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $uninitialized 'SAP2000.exe'),[byte[]]@(1))
    [IO.File]::WriteAllBytes((Join-Path $uninitialized 'CSiAPIv1.dll'),[byte[]]@(1))
    $under27=@{}
    foreach($product in @('SAP2000','CSiBridge')){
        $path=Join-Path $profiles ($product+' 26')
        $under27[$path]=@(Get-ChildItem -LiteralPath $path -File|ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        })
    }
    foreach($pass in 1..2){
        $output=& $Installer test-hosts $installs $profiles
        if($LASTEXITCODE -ne 0){throw "Host discovery failed on pass $pass."}
        foreach($product in @('SAP2000','CSiBridge')){
            foreach($version in @(27,28,29,31)){
                if(-not(@($output | Where-Object {$_ -match ([regex]::Escape($product+' '+$version)+' Tools plugin menu ') }).Count)){
                    throw "Installer did not process $product $version on pass $pass."
                }
            }
        }
    }
    foreach($product in @('SAP2000','CSiBridge')){
        foreach($version in @(27,28,29,31)){
            $profile=Join-Path $profiles ($product+' '+$version)
            $stem=$product+'v'+$version
            $ini=[IO.File]::ReadAllText((Join-Path $profile ($stem+'.ini')))
            $section=[regex]::Match($ini,'(?ms)^\[PlugIn\]\r?\n.*?(?=^\[|\z)').Value
            $names=[regex]::Matches($section,'(?m)^\s*PlugInName=([^\r\n]*)')
            $labels=[regex]::Matches($section,'(?m)^\s*PlugInMenuText=([^\r\n]*)')
            $count=[regex]::Match($section,'(?m)^\s*NumberPlugIns=(\d+)').Groups[1].Value
            $indices=@(0..($names.Count-1)|Where-Object {$names[$_].Groups[1].Value.Trim() -eq 'SapQuadTransitionPlugin'})
            if([int]$count -ne $names.Count -or $names.Count -ne $labels.Count -or
                $indices.Count -ne 1 -or $labels[$indices[0]].Groups[1].Value.Trim() -ne 'MESHing'){
                throw "Incorrect $product $version INI menu after repeat installation."
            }
            [xml]$settings=Get-Content -LiteralPath (Join-Path $profile ($stem+'.settings')) -Raw
            [xml]$entries=$settings.Settings.PlugIn
            $plugin=@($entries.ArrayOfString.string|ForEach-Object {
                [xml]$record=$_.TrimStart([char]0xfeff,[char]' ',[char]13,[char]10)
                if($record.PlugIn.m_name -eq 'SapQuadTransitionPlugin'){$record.PlugIn.m_Text}
            })
            if($plugin.Count -ne 1 -or $plugin[0] -ne 'MESHing'){
                throw "Missing $product $version settings menu."
            }
            "MESHING_HOST_MENU=$product $version PASS TWO_INSTALLS=YES"
        }
    }
    foreach($file in $original.Keys){
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $original[$file]){
            throw 'A live 27 host menu changed during isolated test.'
        }
    }
    foreach($path in $under27.Keys){
        $current=@(Get-ChildItem -LiteralPath $path -File|ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        })
        if(@(Compare-Object $under27[$path] $current).Count){throw 'An unsupported 26 profile was modified.'}
    }
    if([IO.File]::ReadAllText($sentinel) -ne 'Do not register ETABS.' -or
        @(Get-ChildItem -LiteralPath $etabsProfile -File).Count -ne 1){throw 'ETABS profile was modified.'}
    if(Test-Path -LiteralPath (Join-Path $profiles 'SAP2000 32')){
        throw 'Installer created an uninitialized future host profile.'
    }
    'MESHING_HOST_DISCOVERY=PASS FUTURE=28,29,31 SAP_BRIDGE=YES ETABS=UNTOUCHED'
}finally{
    if(Test-Path -LiteralPath $trial){Remove-Item -LiteralPath $trial -Recurse -Force}
}
