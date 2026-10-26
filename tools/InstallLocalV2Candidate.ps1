# Install a locally built V2 candidate without modifying the published package.
param([string]$Version = '2.0.42')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $env:TEMP 'opencode'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\SAP2000QuadMesher'
$dll = Join-Path $root 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'
$backend = Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py'
$id = [Guid]::NewGuid().ToString('N')
$backup = Join-Path $parent ('sqm-v2-before-local-' + $Version + '-' + $id)
$stage = Join-Path $parent ('sqm-v2-local-candidate-' + $Version + '-' + $id)
if (@(Get-Process SAP2000 -ErrorAction SilentlyContinue).Count) {
    throw 'Close SAP2000 before replacing its plugin; save open models first.'
}
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or
    -not (Test-Path -LiteralPath $installed -PathType Container) -or
    -not (Test-Path -LiteralPath $dll -PathType Leaf) -or
    -not (Test-Path -LiteralPath $backend -PathType Leaf) -or
    (Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $stage) -or
    (Get-Item -LiteralPath $dll).VersionInfo.FileVersion -ne "$Version.0") {
    throw 'Missing installation inputs, unsafe rollback destination, or incorrect candidate version.'
}
$installer = Join-Path $installed 'SQMPluginInstaller.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installed V2 installer is missing.' }
$oldDllHash = (Get-FileHash -LiteralPath (Join-Path $installed 'SapQuadTransitionPlugin.dll') -Algorithm SHA256).Hash
$oldBackendHash = (Get-FileHash -LiteralPath (Join-Path $installed 'sap_gmsh_mesher.py') -Algorithm SHA256).Hash
Copy-Item -LiteralPath $installed -Destination $backup -Recurse
& (Join-Path $backup 'SQMPluginInstaller.exe') verify $backup
if ($LASTEXITCODE -ne 0 -or
    (Get-FileHash -LiteralPath (Join-Path $backup 'SapQuadTransitionPlugin.dll') -Algorithm SHA256).Hash -ne $oldDllHash -or
    (Get-FileHash -LiteralPath (Join-Path $backup 'sap_gmsh_mesher.py') -Algorithm SHA256).Hash -ne $oldBackendHash) {
    throw "Rollback verification failed: $backup"
}
Copy-Item -LiteralPath $backup -Destination $stage -Recurse
try {
    Copy-Item -LiteralPath $dll -Destination (Join-Path $stage 'SapQuadTransitionPlugin.dll') -Force
    Copy-Item -LiteralPath $backend -Destination (Join-Path $stage 'sap_gmsh_mesher.py') -Force
    & (Join-Path $stage 'SQMPluginInstaller.exe') verify $stage
    if ($LASTEXITCODE -ne 0) { throw "Candidate verification failed; rollback remains at $backup" }
    if (@(Get-Process SAP2000 -ErrorAction SilentlyContinue).Count) {
        throw 'SAP2000 was reopened while staging. Installation stopped.'
    }
    & (Join-Path $stage 'SQMPluginInstaller.exe') install $stage
    if ($LASTEXITCODE -ne 0) { throw "Installation failed; rollback remains at $backup" }
    $installedDll = Join-Path $installed 'SapQuadTransitionPlugin.dll'
    $actualDllHash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
    $actualBackendHash = (Get-FileHash -LiteralPath (Join-Path $installed 'sap_gmsh_mesher.py') -Algorithm SHA256).Hash
    $registration = 'HKCU:\Software\Classes\CLSID\{D64E6254-E9B2-46C8-8C0E-C8A5C78D94AA}\InprocServer32'
    if ((Get-Item -LiteralPath $installedDll).VersionInfo.FileVersion -ne "$Version.0" -or
        $actualDllHash -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -or
        $actualBackendHash -ne (Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash -or
        ([uri](Get-ItemProperty -LiteralPath $registration).CodeBase).LocalPath -ne $installedDll) {
        throw "Installed candidate or COM registration differs; rollback remains at $backup"
    }
    "LOCAL_INSTALLED=$installed VERSION=$Version.0"
    "DLL_SHA256=$actualDllHash BACKEND_SHA256=$actualBackendHash"
    "ROLLBACK_COPY=$backup COM_REGISTRATION=PASS"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
