# 3 SAP2000 + 3 CSiBridge sessions concurrently per 36-case Apply/Undo wave.
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds = 480,
      [switch]$VerifyOnly,
      [switch]$Resume)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'RunMESHingV303Matrix.ps1') -PluginPath $PluginPath `
    -ResultsRoot $ResultsRoot -CaseTimeoutSeconds $CaseTimeoutSeconds `
    -ConcurrentHosts -VerifyOnly:([bool]$VerifyOnly) -Resume:([bool]$Resume) -VersionLabel '3.0.5'
