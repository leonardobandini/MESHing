# Assemble the versioned DLL and backend for the two-host Generate matrix.
param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temp=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'opencode')).TrimEnd('\')+'\'
$target=[IO.Path]::GetFullPath($Destination)
if(-not $target.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path -LiteralPath $target) -or
   -not (Test-Path -LiteralPath (Split-Path -Parent $target) -PathType Container)){
    throw 'Choose a new stage directory under TEMP\opencode.'
}
& dotnet build (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Plugin build failed'}
$pluginBuild=Join-Path $root 'SapQuadTransitionPlugin\bin\Release\net48'
$backend=Join-Path $root 'packaging\dist-v2\SapQuadMesherBackend'
if(-not(Test-Path -LiteralPath (Join-Path $backend 'SapQuadMesherBackend.exe') -PathType Leaf) -or
   (Get-Item -LiteralPath (Join-Path $backend 'SapQuadMesherBackend.exe')).VersionInfo.FileVersion -ne '3.0.7.0'){
    throw 'The 3.0.7 backend was not built.'
}
New-Item -ItemType Directory -Path $target|Out-Null
foreach($name in @('SapQuadTransitionPlugin.dll','sap_gmsh_mesher.py')){
    Copy-Item -LiteralPath (Join-Path $pluginBuild $name) -Destination $target
}
foreach($name in @('docs','img')){
    Copy-Item -LiteralPath (Join-Path $pluginBuild $name) -Destination $target -Recurse
}
Copy-Item -LiteralPath $backend -Destination (Join-Path $target 'mesher') -Recurse
$plugin=Join-Path $target 'SapQuadTransitionPlugin.dll'
if((Get-Item -LiteralPath $plugin).VersionInfo.FileVersion -ne '3.0.7.0' -or
    ((& (Join-Path $target 'mesher\SapQuadMesherBackend.exe') --version) -ne 'MESHing backend 3.0.7')){
    throw 'The staged plugin and backend are not both 3.0.7.'
}
"MATRIX_PLUGIN=$plugin"
