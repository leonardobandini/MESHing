# End-to-end Generate/Apply/file-switch/Generate/Undo in one modeless V2 dialog.
param([Parameter(Mandatory=$true)][string]$Candidate,
      [Parameter(Mandatory=$true)][string]$Trial,
      [switch]$SameModel, [switch]$DifferentUnits)
$ErrorActionPreference = 'Stop'
$parent = Join-Path $env:TEMP 'opencode'
$root = Split-Path -Parent $PSScriptRoot
$installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not $Trial.StartsWith(($parent.TrimEnd('\') + '\'),[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $Trial) -or
    -not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $installed 'mesher\SapQuadMesherBackend.exe'))) {
    throw 'Unsafe trial folder or missing candidate/Gmsh runtime.'
}
$first = Join-Path $root 'examples\atomic\models\nTEST001.sdb'
$second = Join-Path $root 'examples\atomic\models\nTEST002.sdb'
$hashes = @((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash,
            (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash)
New-Item -ItemType Directory -Path $Trial | Out-Null
$firstCopy = Join-Path $Trial 'first.sdb'
$secondCopy = Join-Path $Trial 'second.sdb'
Copy-Item -LiteralPath $first -Destination $firstCopy
Copy-Item -LiteralPath $second -Destination $secondCopy
$package = Join-Path $Trial 'plugin'
Copy-Item -LiteralPath $installed -Destination $package -Recurse
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $package 'SapQuadTransitionPlugin.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py') -Destination $package -Force
$runner = Join-Path $Trial 'runner'
& dotnet build (Join-Path $PSScriptRoot 'TestV2ModelSwitch.csproj') -c Release -o $runner --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Model-switch integration runner build failed.' }
$before = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
try {
    if ($SameModel) {
        & (Join-Path $runner 'TestV2ModelSwitch.exe') $firstCopy $secondCopy (Join-Path $package 'SapQuadTransitionPlugin.dll') '--same-model'
    }
    elseif ($DifferentUnits) {
        & (Join-Path $runner 'TestV2ModelSwitch.exe') $firstCopy $secondCopy (Join-Path $package 'SapQuadTransitionPlugin.dll') '--different-units'
    }
    else {
        & (Join-Path $runner 'TestV2ModelSwitch.exe') $firstCopy $secondCopy (Join-Path $package 'SapQuadTransitionPlugin.dll')
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cross-model same-dialog regression failed.' }
}
finally {
    if ((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -ne $hashes[0] -or
        (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash -ne $hashes[1]) {
        throw 'An original atomic fixture changed.'
    }
    $new = @(Get-Process SAP2000 -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $before })
    if ($new.Count) { Write-Warning "Owned SAP worker still open: $($new.Id -join ','). Inspect before closing." }
}
