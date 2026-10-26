# SPDX-License-Identifier: GPL-2.0-or-later
# Install the same extracted ZIP that users download; no build-tree dependencies.
param([string]$Zip = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Sap2000QuadMesher-2.0.46.zip'),
      [string]$Backup = '')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name SAP2000 -ErrorAction SilentlyContinue) {
    throw 'Close SAP2000 (saving your open model) before replacing the installed plugin.'
}
$hashFile = "$Zip.sha256"
$match = [regex]::Match([IO.Path]::GetFileName($Zip), '^(?:Sap2000QuadMesher|FEMesher|FEMesh)-(2\.\d+\.\d+)\.zip$')
if (-not $match.Success) { throw 'Expected a versioned FEMesher or legacy-compatible V2 ZIP.' }
$version = $match.Groups[1].Value
if (-not (Test-Path -LiteralPath $Zip -PathType Leaf) -or -not (Test-Path -LiteralPath $hashFile -PathType Leaf)) {
    throw 'ZIP or corresponding checksum missing.'
}
$expectedHash = ([IO.File]::ReadAllText($hashFile) -split '\s+')[0]
if ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash -ne $expectedHash) { throw 'ZIP checksum mismatch.' }
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Missing temporary parent.' }
$trial = Join-Path $parent ('sqm-v2-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trial | Out-Null
try {
    Expand-Archive -LiteralPath $Zip -DestinationPath $trial
    $installer = Join-Path $trial 'SQMPluginInstaller.exe'
    & $installer verify $trial
    if ($LASTEXITCODE -ne 0) { throw 'Extracted V2 package verification failed.' }
    if ($Backup) {
        $installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
        if (-not (Test-Path -LiteralPath $installed -PathType Container) -or
            -not (Test-Path -LiteralPath (Split-Path -Parent $Backup) -PathType Container) -or
            (Test-Path -LiteralPath $Backup)) { throw 'Rollback source/parent missing or rollback destination exists.' }
        $previous = Join-Path $installed 'SapQuadTransitionPlugin.dll'
        $previousHash = (Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $installed -Destination $Backup -Recurse
        & (Join-Path $Backup 'SQMPluginInstaller.exe') verify $Backup
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $Backup 'SapQuadTransitionPlugin.dll') -Algorithm SHA256).Hash -ne $previousHash) {
            throw 'Rollback copy verification failed; installed version was not replaced.'
        }
        "ROLLBACK_COPY=$Backup"
    }
    & $installer install $trial
    if ($LASTEXITCODE -ne 0) { throw 'V2 installation failed; inspect the installer rollback message.' }
    $dll = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher\SapQuadTransitionPlugin.dll'
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $trial 'SapQuadTransitionPlugin.dll') -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $sourceHash -or
        (Get-Item $dll).VersionInfo.FileVersion -ne "$version.0") { throw 'Installed V2 DLL does not match the ZIP.' }
    $registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\CLSID\{D64E6254-E9B2-46C8-8C0E-C8A5C78D94AA}\InprocServer32'
    if (([uri]$registered.CodeBase).LocalPath -ne $dll) { throw 'V1 Tools COM identity does not point to installed V2 DLL.' }
    "V2_INSTALLED=$dll"
    "V2_VERSION=$((Get-Item $dll).VersionInfo.FileVersion)"
} finally {
    if (Test-Path -LiteralPath $trial) { Remove-Item -LiteralPath $trial -Recurse -Force }
}
