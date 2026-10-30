# Complete 36-case Generate matrix on each host, with no Undo wave.
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds=480,
      [switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$package=Split-Path -Parent $PluginPath
$backend=Join-Path $package 'mesher\SapQuadMesherBackend.exe'
if(-not(Test-Path -LiteralPath $PluginPath -PathType Leaf) -or
   -not(Test-Path -LiteralPath $backend -PathType Leaf) -or
   (Get-Item -LiteralPath $PluginPath).VersionInfo.FileVersion -ne '3.0.7.0' -or
   (Get-Item -LiteralPath $backend).VersionInfo.FileVersion -ne '3.0.7.0'){
    throw 'The complete MESHing 3.0.7 package is required.'
}
& (Join-Path $PSScriptRoot 'RunMESHingV303Matrix.ps1') -PluginPath $PluginPath `
    -ResultsRoot $ResultsRoot -CaseTimeoutSeconds $CaseTimeoutSeconds `
    -ConcurrentHosts -ApplyOnly -AllowExistingHosts -VerifyOnly:([bool]$VerifyOnly) -VersionLabel '3.0.7'
foreach($hostKind in @('sap','bridge')){
    for($group=1;$group -le 3;$group++){
        $path=if($hostKind -eq 'sap'){
            Join-Path $ResultsRoot "sap-apply\group-$group.out.log"
        }else{
            Join-Path $ResultsRoot "bridge-apply-group-$group.out.log"
        }
        $log=Get-Content -LiteralPath $path -Raw
        $fast=[regex]::Matches($log,'(?m)^mesh-insertion=fast areas=\d+\s*$').Count
        $direct=[regex]::Matches($log,'(?m)^mesh-direct-commit=complete\s*$').Count
        $standard=[regex]::Matches($log,'(?m)^mesh-insertion=standard\b').Count
        $expected=if($group -eq 2){12}else{11} # nTEST003/035 request Standard explicitly.
        if($fast -ne $expected -or $direct -ne $expected -or $standard){
            throw "Insertion mode mismatch: $hostKind group $group fast=$fast direct=$direct unexpectedStandard=$standard expected=$expected"
        }
        "INSERTION_PASS=$hostKind GROUP=$group FAST=$fast EXPLICIT_STANDARD=$([int](12-$expected))"
    }
}
