# Start the complete apply-only 3.0.7 matrix and leave its logs available for verification.
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds=600)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ResultsRoot)
$temp=[IO.Path]::GetFullPath((Join-Path $env:TEMP 'opencode')).TrimEnd('\')+'\'
if(-not $root.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
   (Test-Path -LiteralPath $root) -or
   -not(Test-Path -LiteralPath (Split-Path -Parent $root) -PathType Container)){
    throw 'Expected a new results directory under TEMP\opencode.'
}
$args='-NoProfile -ExecutionPolicy Bypass -File "{0}" -PluginPath "{1}" -ResultsRoot "{2}" -CaseTimeoutSeconds {3}' -f `
    (Join-Path $PSScriptRoot 'RunMESHingV307Matrix.ps1'),
    (Resolve-Path -LiteralPath $PluginPath).Path,$root,$CaseTimeoutSeconds
$p=Start-Process powershell.exe -ArgumentList $args -PassThru `
    -RedirectStandardOutput ($root+'.out.log') -RedirectStandardError ($root+'.err.log')
"MATRIX_COORDINATOR_PID=$($p.Id) RESULTS=$root"
$p.Dispose()
