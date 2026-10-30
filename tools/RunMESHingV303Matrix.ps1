# Four independent 36-case matrices: Apply/Undo on SAP2000 and CSiBridge.
# Each matrix uses three owned, persistent host processes (12 cases each).
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds = 480,
      [switch]$VerifyOnly,
      [switch]$Resume,
       [switch]$ConcurrentHosts,
       [switch]$ApplyOnly,
       [switch]$AllowExistingHosts,
      [string]$VersionLabel = '3.0.3')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $ResultsRoot.StartsWith(($parent.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    ((Test-Path -LiteralPath $ResultsRoot) -ne [bool]($VerifyOnly -or $Resume)) -or
    ($VerifyOnly -and $Resume) -or
    -not (Test-Path -LiteralPath $PluginPath -PathType Leaf)) {
    throw 'Unsafe or existing matrix destination, missing plugin, or wrong VerifyOnly mode.'
}
$modelDirectory = Join-Path $root 'examples\atomic\models'
$manifestPath = Join-Path $root 'examples\atomic\manifest.json'
[object[]]$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$ids = @(1..36 | ForEach-Object { 'nTEST{0:D3}' -f $_ })
if ($manifest.Count -ne 36 -or @(Compare-Object $ids @($manifest | ForEach-Object id)).Count -or
    @(Get-ChildItem -LiteralPath $modelDirectory -Filter 'nTEST*.sdb' -File).Count -ne 36) {
    throw 'The complete 36-case input catalog is missing.'
}
$originalHashes = @{}
foreach ($id in $ids) {
    $originalHashes[$id] = (Get-FileHash -LiteralPath (Join-Path $modelDirectory ($id + '.sdb')) -Algorithm SHA256).Hash
}
function Verify-Inputs {
    foreach ($id in $ids) {
        if ((Get-FileHash -LiteralPath (Join-Path $modelDirectory ($id + '.sdb')) -Algorithm SHA256).Hash -ne
            $originalHashes[$id]) { throw "Original regression input changed: $id" }
    }
}
function Verify-Sap([string]$mode, [bool]$existingResults = $false) {
    $path = Join-Path $ResultsRoot ('sap-' + $mode)
    & (Join-Path $PSScriptRoot 'RunGmshV2FullRegression.ps1') -PluginPath $PluginPath `
        -ResultsRoot $path -WorkerCount 3 -CaseTimeoutSeconds $CaseTimeoutSeconds `
        -VerifyOnly:([bool]($VerifyOnly -or $existingResults -or ($Resume -and $mode -eq 'apply'))) -Undo:($mode -eq 'undo')
    $rows = @(Import-Csv -LiteralPath (Join-Path $path 'summary.csv'))
    if ($rows.Count -ne 36 -or @(Compare-Object $ids @($rows | ForEach-Object id)).Count -or
        @($rows | Where-Object { $_.status -ne 'PASS' -or $_.mode -ne $mode.ToUpperInvariant() -or
            $_.backendJson -eq '0' -or $_.model -ne 'True' }).Count) {
        throw "Incomplete SAP $mode matrix."
    }
    "SAP_$($mode.ToUpperInvariant())=36/36"
}
function Verify-Bridge([string]$mode) {
    $processes = @()
    $existing = $VerifyOnly -or ($Resume -and $mode -eq 'apply')
    try {
        for ($index = 0; $index -lt 3; $index++) {
            $part = @($ids[($index * 12)..($index * 12 + 11)])
            $folder = Join-Path $ResultsRoot ('bridge-' + $mode + '-group-' + ($index + 1))
            $stdout = $folder + '.out.log'
            $stderr = $folder + '.err.log'
            if (-not $existing) {
                $args = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -HostKind CSiBridge -Plugin "{1}" -ResultsRoot "{2}" -Ids "{3}" -AllAtomic -InputModelDirectory "{4}" -ManifestPath "{5}"' -f `
                    (Join-Path $PSScriptRoot 'TestFEMeshHosts.ps1'), $PluginPath, $folder,
                    ($part -join ','), $modelDirectory, $manifestPath
                if ($mode -eq 'undo') { $args += ' -Undo' }
                $process = Start-Process powershell.exe -ArgumentList $args -PassThru `
                    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                $processes += $process
                "BRIDGE_GROUP_STARTED=$($index + 1) MODE=$mode PID=$($process.Id)"
            }
        }
        for ($index = 0; $index -lt 3; $index++) {
            $part = @($ids[($index * 12)..($index * 12 + 11)])
            $folder = Join-Path $ResultsRoot ('bridge-' + $mode + '-group-' + ($index + 1))
            $stdout = $folder + '.out.log'
            $stderr = $folder + '.err.log'
            if (-not $existing -and -not $processes[$index].WaitForExit(($CaseTimeoutSeconds * 12 + 180) * 1000)) {
                throw "Bridge $mode worker timed out: PID=$($processes[$index].Id) (left for inspection)."
            }
            if (-not (Test-Path -LiteralPath $stdout -PathType Leaf)) { throw "Missing Bridge log: $stdout" }
            $text = Get-Content -LiteralPath $stdout -Raw
            $seen = @([regex]::Matches($text, '(?m)^CSiBridge/(nTEST\d{3}) PASS MODE=(APPLY|UNDO)\b'))
            if ($seen.Count -ne 12 -or @(Compare-Object $part @($seen | ForEach-Object { $_.Groups[1].Value })).Count -or
                @($seen | Where-Object { $_.Groups[2].Value -ne $mode.ToUpperInvariant() }).Count -or
                $text -notmatch 'CSiBridge MATRIX=PASS COUNT=12') { throw "Incomplete Bridge $mode matrix: $stdout" }
            $errors = @(Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue | Where-Object {
                $_.Trim() -and $_ -notmatch '^Cannot open file$' -and
                $_ -notmatch '^BufferFileIn::BufferFileIn\(\):\s*$' -and
                $_ -notmatch '^fileName = .+\.Y_$'
            })
            if ($errors.Count) {
                throw "Bridge $mode worker returned errors: $stderr"
            }
            if (-not $existing -and $processes[$index].ExitCode -ne 0) {
                Write-Warning "Bridge worker $($processes[$index].Id) emitted only optional .Y_ diagnostics; all 12 cases passed."
            }
            foreach ($id in $part) {
                $case = Join-Path $folder $id
                if ((Get-Content -LiteralPath (Join-Path $case 'input.sha256') -Raw).Trim() -ne $originalHashes[$id] -or
                    -not (Test-Path -LiteralPath (Join-Path $case ($id + '.bdb')) -PathType Leaf) -or
                    (Test-Path -LiteralPath (Join-Path $case 'plugin-error.txt'))) {
                    throw "Bridge $mode case failed input/output validation: $id"
                }
            }
            "BRIDGE_GROUP_PASS=$($index + 1) MODE=$mode COUNT=12"
        }
    } finally {
        foreach ($process in $processes) { $process.Dispose() }
    }
    "BRIDGE_$($mode.ToUpperInvariant())=36/36"
}
if (-not $VerifyOnly) {
    if (-not $AllowExistingHosts -and @(Get-Process SAP2000,CSiBridge -ErrorAction SilentlyContinue).Count) {
        throw 'Close SAP2000 and CSiBridge before starting owned regression sessions.'
    }
    if (-not $Resume) { $null = New-Item -ItemType Directory -Path $ResultsRoot }
}
foreach ($mode in $(if ($ApplyOnly) { @('apply') } else { @('apply','undo') })) {
    if ($ConcurrentHosts -and -not $VerifyOnly -and -not ($Resume -and $mode -eq 'apply')) {
        $sapPath = Join-Path $ResultsRoot ('sap-' + $mode)
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -PluginPath "{1}" -ResultsRoot "{2}" -WorkerCount 3 -CaseTimeoutSeconds {3}' -f `
            (Join-Path $PSScriptRoot 'RunGmshV2FullRegression.ps1'), $PluginPath, $sapPath, $CaseTimeoutSeconds
        if ($mode -eq 'undo') { $arguments += ' -Undo' }
        $sapWorker = Start-Process powershell.exe -ArgumentList $arguments -PassThru `
            -RedirectStandardOutput ($sapPath + '.out.log') -RedirectStandardError ($sapPath + '.err.log')
        try {
            'SAP_MATRIX_STARTED=MODE={0} PID={1} WORKERS=3' -f $mode,$sapWorker.Id
            Verify-Bridge $mode
            if (-not $sapWorker.WaitForExit(($CaseTimeoutSeconds * 12 + 180) * 1000)) {
                throw "SAP $mode matrix timed out; owned SAP processes were left for inspection."
            }
            $sapErrors = @(Get-Content -LiteralPath ($sapPath + '.err.log') -ErrorAction SilentlyContinue |
                Where-Object { $_.Trim() })
            if ($sapErrors.Count) {
                throw "SAP $mode matrix exited with errors: ${sapPath}.err.log"
            }
            Verify-Sap $mode $true
            if ($sapWorker.ExitCode -ne 0) {
                Write-Warning "SAP $mode coordinator exited nonzero with only optional .Y_ diagnostics; all 36 cases passed."
            }
        } finally { $sapWorker.Dispose() }
    }
    else {
        Verify-Sap $mode
        Verify-Inputs
        Verify-Bridge $mode
    }
    Verify-Inputs
}
if ($ApplyOnly) {
    ('MESHING_{0}_MATRIX=PASS SAP_APPLY=36 BRIDGE_APPLY=36 TOTAL=72 UNDO=NOT_RUN' -f $VersionLabel.Replace('.', '_'))
} else {
    ('MESHING_{0}_MATRIX=PASS SAP_APPLY=36 SAP_UNDO=36 BRIDGE_APPLY=36 BRIDGE_UNDO=36 TOTAL=144' -f $VersionLabel.Replace('.', '_'))
}
