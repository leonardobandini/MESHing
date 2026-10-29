# Run four 36-case matrices with three SAP2000 and three CSiBridge processes
# active at the same time during each Apply or Undo wave.
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds = 480,
      [switch]$VerifyOnly,
      [switch]$Resume)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'RunMESHingV303Matrix.ps1') -PluginPath $PluginPath `
    -ResultsRoot $ResultsRoot -CaseTimeoutSeconds $CaseTimeoutSeconds `
    -ConcurrentHosts -VerifyOnly:([bool]$VerifyOnly) -Resume:([bool]$Resume) -VersionLabel '3.0.4'
