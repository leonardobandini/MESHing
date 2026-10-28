param([Parameter(Mandatory=$true)][string]$Ids,
      [Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$InputModelDirectory,
      [Parameter(Mandatory=$true)][string]$ManifestPath,
      [Parameter(Mandatory=$true)][string]$RunDirectory,
      [int]$CaseTimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $RunDirectory.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $RunDirectory) -or
    -not (Test-Path -LiteralPath $PluginPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $InputModelDirectory -PathType Container) -or
    -not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'Unsafe regression paths.' }
$count = @($Ids.Split(',') | Where-Object { $_ }).Count
if ($count -lt 1 -or $count -gt 12) { throw 'Expected 1 to 12 IDs per persistent SAP worker.' }
$script = Join-Path $PSScriptRoot 'RunGmshV2Regression.ps1'
$stdout = $RunDirectory + '.stdout.log'
$stderr = $RunDirectory + '.stderr.log'
$existing = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Ids "{1}" -PluginPath "{2}" -RunDirectory "{3}" -InputModelDirectory "{4}" -ManifestPath "{5}" -ExpectedSapVersion 27.1 -CaseTimeoutSeconds {6}' -f `
    $script,$Ids,$PluginPath,$RunDirectory,$InputModelDirectory,$ManifestPath,$CaseTimeoutSeconds
$worker = Start-Process powershell.exe -ArgumentList $arguments -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    if (-not $worker.WaitForExit(($CaseTimeoutSeconds * $count + 90) * 1000)) {
        Stop-Process -Id $worker.Id -Force -ErrorAction SilentlyContinue
        throw "Regression worker timed out: $RunDirectory (logs: $stdout / $stderr)"
    }
    $summary = Join-Path $RunDirectory 'summary.csv'
    if (-not (Test-Path -LiteralPath $summary -PathType Leaf)) {
        throw "No summary: $RunDirectory (logs: $stdout / $stderr)"
    }
    $results = @(Import-Csv -LiteralPath $summary)
    $fail = @($results | Where-Object { $_.status -ne 'PASS' -or $_.model -ne 'True' })
    if ($results.Count -ne $count -or $fail.Count) {
        throw "Regression failed: $($results.Count)/$count records; failing IDs: $($fail.id -join ','); exit=$($worker.ExitCode); logs: $stdout"
    }
    if ($worker.ExitCode -ne 0) {
        $other = @(Get-Content -LiteralPath $stderr | Where-Object {
            $_.Trim() -and $_ -notmatch '^Cannot open file$' -and
            $_ -notmatch '^BufferFileIn::BufferFileIn\(\):\s*$' -and
            $_ -notmatch '^fileName = .+\.Y_$'
        })
        if ($other.Count) { throw "Worker exited with errors: $stderr" }
    }
    "REGRESSION_PASS=$count DIR=$RunDirectory"
} finally {
    $worker.Dispose()
    $pidFile = Join-Path $RunDirectory 'sap.pid'
    if (Test-Path -LiteralPath $pidFile) {
        $owned = [int](Get-Content -LiteralPath $pidFile -Raw)
        if ($owned -notin $existing -and (Get-Process -Id $owned -ErrorAction SilentlyContinue)) {
            Stop-Process -Id $owned -Force -ErrorAction SilentlyContinue
            Write-Warning "Stopped only owned SAP worker PID $owned."
        }
    }
}
