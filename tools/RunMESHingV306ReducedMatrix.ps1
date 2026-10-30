# Six representative Generate cases, two per worker, six hosts at once.
# No Undo wave: all results and untouched source models are verified.
param([Parameter(Mandatory=$true)][string]$PluginPath,
      [Parameter(Mandatory=$true)][string]$ResultsRoot,
      [int]$CaseTimeoutSeconds = 480,
      [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
$groups = @(
    @('nTEST006', 'nTEST020'), # multi-area circular hole / five-plane framed opening
    @('nTEST024', 'nTEST025'), # four O-grids / dense symmetric edge constraints
    @('nTEST028', 'nTEST033')  # skew framed opening / dense immutable edge mesh
)
$ids = @($groups | ForEach-Object { $_ })
$models = Join-Path $root 'examples\atomic\models'
$manifest = Join-Path $root 'examples\atomic\manifest.json'
$package = Split-Path -Parent $PluginPath
$backend = Join-Path $package 'mesher\SapQuadMesherBackend.exe'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not [IO.Path]::GetFullPath($ResultsRoot).StartsWith(
        [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $ResultsRoot) -ne [bool]$VerifyOnly -or
    -not (Test-Path -LiteralPath $PluginPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $backend -PathType Leaf) -or
    (Get-Item -LiteralPath $PluginPath).VersionInfo.FileVersion -ne '3.0.6.0' -or
    (Get-Item -LiteralPath $backend).VersionInfo.FileVersion -ne '3.0.6.0' -or
    $CaseTimeoutSeconds -lt 60) {
    throw 'Expected a new isolated result path and complete 3.0.6 package.'
}
$catalog = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($ids.Count -ne 6 -or @($ids | Select-Object -Unique).Count -ne 6 -or
    @($ids | Where-Object { $_ -notin @($catalog | ForEach-Object id) }).Count) {
    throw 'The six regression cases are missing or repeated.'
}
$hashes = @{}
foreach ($id in $ids) {
    $file = Join-Path $models ($id + '.sdb')
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing source model $id" }
    $hashes[$id] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
function Verify-Originals {
    foreach ($id in $ids) {
        if ((Get-FileHash -LiteralPath (Join-Path $models ($id + '.sdb')) -Algorithm SHA256).Hash -ne
            $hashes[$id]) { throw "Original model changed: $id" }
    }
}
if (-not $VerifyOnly) {
    if (@(Get-Process SAP2000,CSiBridge -ErrorAction SilentlyContinue).Count) {
        throw 'Close all SAP2000 and CSiBridge sessions before starting the owned six-host matrix.'
    }
    & dotnet build (Join-Path $root 'RunTest01Fast.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Atomic runner build failed.' }
    New-Item -ItemType Directory -Path $ResultsRoot | Out-Null
}
$workers = @()
try {
    for ($index = 0; $index -lt 3; $index++) {
        foreach ($hostName in @('sap','bridge')) {
            $part = @($groups[$index])
            $folder = Join-Path $ResultsRoot ($hostName + '-group-' + ($index + 1))
            $out = $folder + '.out.log'
            $err = $folder + '.err.log'
            $process = $null
            if (-not $VerifyOnly) {
                $args = if ($hostName -eq 'sap') {
                    '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Ids "{1}" -PluginPath "{2}" -RunDirectory "{3}" -CaseTimeoutSeconds {4}' -f `
                        (Join-Path $PSScriptRoot 'RunGmshV2Regression.ps1'), ($part -join ','),
                        (Resolve-Path -LiteralPath $PluginPath).Path, $folder, $CaseTimeoutSeconds
                } else {
                    '-NoProfile -ExecutionPolicy Bypass -File "{0}" -HostKind CSiBridge -Plugin "{1}" -ResultsRoot "{2}" -Ids "{3}" -AllAtomic -InputModelDirectory "{4}" -ManifestPath "{5}"' -f `
                        (Join-Path $PSScriptRoot 'TestFEMeshHosts.ps1'),
                        (Resolve-Path -LiteralPath $PluginPath).Path, $folder,
                        ($part -join ','), $models, $manifest
                }
                $process = Start-Process powershell.exe -ArgumentList $args -PassThru `
                    -RedirectStandardOutput $out -RedirectStandardError $err
                "WORKER_STARTED HOST=$hostName GROUP=$($index + 1) PID=$($process.Id) CASES=$($part -join ',')"
            }
            $workers += [pscustomobject]@{ Host=$hostName; Cases=$part; Folder=$folder;
                Out=$out; Err=$err; Process=$process }
        }
    }
    foreach ($worker in $workers) {
        if (-not $VerifyOnly -and -not $worker.Process.WaitForExit(
            ($CaseTimeoutSeconds * 2 + 180) * 1000)) {
            throw "Worker timed out: $($worker.Host) $($worker.Folder) PID=$($worker.Process.Id)"
        }
        if (-not (Test-Path -LiteralPath $worker.Out -PathType Leaf)) {
            throw "Missing worker log: $($worker.Out)"
        }
        $log = Get-Content -LiteralPath $worker.Out -Raw
        $unexpected = @(Get-Content -LiteralPath $worker.Err -ErrorAction SilentlyContinue |
            Where-Object { $_.Trim() -and $_ -notmatch '^Cannot open file$' -and
                $_ -notmatch '^BufferFileIn::BufferFileIn\(\):\s*$' -and
                $_ -notmatch '^fileName = .+\.Y_$' })
        if ($unexpected.Count) { throw "Unexpected errors in $($worker.Err)" }
        if ($worker.Host -eq 'sap') {
            $summary = @(Import-Csv -LiteralPath (Join-Path $worker.Folder 'summary.csv'))
            if ($summary.Count -ne 2 -or
                @(Compare-Object $worker.Cases @($summary | ForEach-Object id)).Count -or
                @($summary | Where-Object { $_.status -ne 'PASS' -or $_.mode -ne 'APPLY' -or
                    $_.backendJson -eq '0' -or $_.model -ne 'True' }).Count) {
                throw "SAP Generate results failed: $($worker.Folder)"
            }
        } else {
            $passed = @([regex]::Matches($log, '(?m)^CSiBridge/(nTEST\d{3}) PASS MODE=APPLY\b') |
                ForEach-Object { $_.Groups[1].Value })
            if ($passed.Count -ne 2 -or @(Compare-Object $worker.Cases $passed).Count -or
                $log -notmatch 'CSiBridge MATRIX=PASS COUNT=2') {
                throw "Bridge Generate results failed: $($worker.Folder)"
            }
        }
        foreach ($id in $worker.Cases) {
            $case = Join-Path $worker.Folder $id
            if ($worker.Host -eq 'bridge' -and
                ((Get-Content -LiteralPath (Join-Path $case 'input.sha256') -Raw).Trim() -ne $hashes[$id] -or
                 -not (Test-Path -LiteralPath (Join-Path $case ($id + '.bdb')) -PathType Leaf))) {
                throw "Bridge source/output verification failed: $id"
            }
            if (Test-Path -LiteralPath (Join-Path $case 'plugin-error.txt')) {
                throw "Plugin failed on $($worker.Host)/$id"
            }
        }
        if (-not $VerifyOnly -and $worker.Process.ExitCode -ne 0) {
            Write-Warning "$($worker.Host) worker emitted only optional .Y_ diagnostics; both cases passed."
        }
        "WORKER_PASS HOST=$($worker.Host) CASES=$($worker.Cases -join ',')"
    }
    Verify-Originals
    'MESHING_3_0_6_REDUCED_MATRIX=PASS SAP_GENERATE=6 BRIDGE_GENERATE=6 HOSTS=6 TOTAL=12 UNDO=NOT_RUN'
} finally {
    foreach ($worker in $workers) {
        if ($worker.Process -ne $null) { $worker.Process.Dispose() }
    }
}
