# Run all 36 V2 SAP atomic fixtures in disjoint, owned SAP2000 processes.
param([Parameter(Mandatory=$true)][string]$PluginPath,
       [Parameter(Mandatory=$true)][string]$ResultsRoot,
       [int]$CaseTimeoutSeconds = 480,
       [ValidateRange(1,36)][int]$WorkerCount = 3,
       [string]$SapPids = '',
       [switch]$VerifyOnly,
       [string]$InputModelDirectory = '', [string]$OutputSuffix = '',
       [string]$ExpectedSapVersion = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $ResultsRoot.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    ((Test-Path -LiteralPath $ResultsRoot) -ne [bool]$VerifyOnly) -or
    -not (Test-Path -LiteralPath $PluginPath -PathType Leaf)) {
    throw 'The plugin or new temporary regression destination is unavailable.'
}
$manifest = @(Get-Content -LiteralPath (Join-Path $root 'examples\atomic\manifest.json') -Raw | ConvertFrom-Json)
$expected = @(1..36 | ForEach-Object { 'nTEST{0:D3}' -f $_ })
$ids = @($manifest | ForEach-Object { $_.id })
if ($ids.Count -ne 36 -or (@(Compare-Object $expected $ids)).Count) {
    throw 'The V2 catalog does not contain precisely nTEST001 through nTEST036.'
}
if (36 % $WorkerCount -ne 0) { throw 'Split the 36 cases evenly across the requested SAP processes.' }
$groupSize = [int](36 / $WorkerCount)
if ($VerifyOnly) {
    $all = @()
    for ($index = 0; $index -lt $WorkerCount; $index++) {
        $folder = Join-Path $ResultsRoot ('group-' + ($index + 1))
        $items = @(Import-Csv -LiteralPath (Join-Path $folder 'summary.csv'))
        $part = @($ids[($index * $groupSize)..($index * $groupSize + $groupSize - 1)])
        if ($items.Count -ne $groupSize -or @(Compare-Object $part @($items | ForEach-Object id)).Count -or
            @($items | Where-Object status -ne 'PASS').Count) { throw "Incomplete group: $folder" }
        $all += $items
        'GROUP_PASS={0} COUNT={1}' -f $folder,$groupSize
    }
    $all | Export-Csv -LiteralPath (Join-Path $ResultsRoot 'summary.csv') -NoTypeInformation -Encoding UTF8
    'V2_FULL_REGRESSION=PASS COUNT=36 WORKERS={0}' -f $WorkerCount
    return
}
& dotnet build (Join-Path $root 'RunTest01Fast.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Atomic runner build failed.' }
$before = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$attached = @()
if ($SapPids) {
    $attached = @($SapPids.Split(',') | ForEach-Object { [int]$_.Trim() })
    if ($attached.Count -ne $WorkerCount -or @($attached | Select-Object -Unique).Count -ne $WorkerCount -or
        @($attached | Where-Object { $_ -notin $before }).Count) {
        throw 'Supply exactly one distinct, already running SAP2000 PID per worker.'
    }
    foreach ($sapPid in $attached) {
        if ((Get-Process -Id $sapPid -ErrorAction Stop).ProcessName -ne 'SAP2000') {
            throw "Attached process is not SAP2000: $sapPid"
        }
    }
}
New-Item -ItemType Directory -Path $ResultsRoot | Out-Null
$script = Join-Path $PSScriptRoot 'RunGmshV2Regression.ps1'
$workers = @()
try {
    for ($index = 0; $index -lt $WorkerCount; $index++) {
        $part = @($ids[($index * $groupSize)..($index * $groupSize + $groupSize - 1)])
        $folder = Join-Path $ResultsRoot ('group-' + ($index + 1))
        $output = Join-Path $ResultsRoot ('group-' + ($index + 1) + '.out.log')
        $errorLog = Join-Path $ResultsRoot ('group-' + ($index + 1) + '.err.log')
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Ids "{1}" -PluginPath "{2}" -RunDirectory "{3}" -CaseTimeoutSeconds {4}' -f `
            $script, ($part -join ','), (Resolve-Path -LiteralPath $PluginPath).Path, $folder, $CaseTimeoutSeconds
        if ($attached.Count) { $arguments += ' -SapPid ' + $attached[$index] }
        if ($InputModelDirectory) {
            $arguments += ' -InputModelDirectory "' + (Resolve-Path -LiteralPath $InputModelDirectory).Path + '"'
        }
        if ($OutputSuffix) { $arguments += ' -OutputSuffix "' + $OutputSuffix + '"' }
        if ($ExpectedSapVersion) { $arguments += ' -ExpectedSapVersion "' + $ExpectedSapVersion + '"' }
        $process = Start-Process powershell.exe -ArgumentList $arguments -PassThru `
            -RedirectStandardOutput $output -RedirectStandardError $errorLog
        $workers += [pscustomobject]@{ Process = $process; Folder = $folder; Ids = $part; Output = $output; Error = $errorLog }
        'GROUP_STARTED={0} PID={1} CASES={2}' -f ($index + 1), $process.Id, ($part -join ',')
    }
    foreach ($worker in $workers) {
        if (-not $worker.Process.WaitForExit(($CaseTimeoutSeconds * $groupSize + 180) * 1000)) {
            throw "Regression worker timed out: PID=$($worker.Process.Id). Its SAP process was left for inspection."
        }
        $summary = Join-Path $worker.Folder 'summary.csv'
        if (-not (Test-Path -LiteralPath $summary -PathType Leaf)) { throw "Missing summary: $summary" }
        $items = @(Import-Csv -LiteralPath $summary)
        if ($items.Count -ne $groupSize -or (@(Compare-Object $worker.Ids @($items | ForEach-Object id))).Count -or
            @($items | Where-Object status -ne 'PASS').Count) {
            throw "Incomplete or failing group: $summary"
        }
        if ($worker.Process.ExitCode -ne 0) {
            # SAP2000 27.2 writes missing optional .Y_ buffer diagnostics to
            # stderr. Windows PowerShell can return code 1 despite all twelve
            # independently verified models completing successfully.
            $otherErrors = @(Get-Content -LiteralPath $worker.Error | Where-Object {
                $_.Trim() -and $_ -notmatch '^Cannot open file$' -and
                $_ -notmatch '^BufferFileIn::BufferFileIn\(\):\s*$' -and
                $_ -notmatch '^fileName = .+\.Y_$'
            })
            if ($otherErrors.Count) {
                throw "Regression worker exited abnormally: PID=$($worker.Process.Id) log=$($worker.Error)"
            }
            Write-Warning "Worker $($worker.Process.Id) emitted only SAP2000 optional .Y_ diagnostics; all $groupSize cases passed."
        }
        'GROUP_PASS={0} COUNT={1}' -f $worker.Folder,$groupSize
    }
    $all = @($workers | ForEach-Object { Import-Csv -LiteralPath (Join-Path $_.Folder 'summary.csv') })
    $all | Export-Csv -LiteralPath (Join-Path $ResultsRoot 'summary.csv') -NoTypeInformation -Encoding UTF8
    if ($all.Count -ne 36) { throw 'The combined regression does not contain 36 cases.' }
    'V2_FULL_REGRESSION=PASS COUNT=36 WORKERS={0}' -f $WorkerCount
}
finally {
    foreach ($worker in $workers) { $worker.Process.Dispose() }
    $after = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $new = @($after | Where-Object { $_ -notin $before })
    if ($new.Count) { Write-Warning "SAP instances added by the regression are still open: $($new -join ','). Inspect before closing." }
}
