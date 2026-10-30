# SPDX-License-Identifier: GPL-2.0-or-later
# Build a standalone Gmsh V2 ZIP which upgrades the existing V1 COM registration.
param([switch]$SkipBackendBuild, [switch]$RebuildExisting)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) { throw "Artifact directory missing: $artifacts" }
[xml]$project = Get-Content (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj')
$version = $project.Project.PropertyGroup.Version
$stage = Join-Path $artifacts "Sap2000QuadMesher-$version"
$zipPath = Join-Path $artifacts "MESHing-$version.zip"
$sourceZip = Join-Path $artifacts "MESHing-$version-source.zip"
if ($RebuildExisting) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'sap_gmsh_mesher.py') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $stage 'SQMPluginInstaller.exe') -PathType Leaf)) {
        throw 'Refusing to replace a directory not recognized as a generated V2 package.'
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
    foreach ($file in @($zipPath, "$zipPath.sha256", $sourceZip, "$sourceZip.sha256")) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
}
$build = Join-Path $env:TEMP ('opencode\sqm-v2-release-build-' + [guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath (Split-Path -Parent $build) -PathType Container)) { throw 'Missing temporary build parent.' }
New-Item -ItemType Directory -Path $build | Out-Null
try {
    $pluginBuild = Join-Path $build 'plugin'
    $installerBuild = Join-Path $build 'installer'
    $setupBuild = Join-Path $build 'setup'
    # Avoid embedding this machine's absolute PDB path in distributed binaries.
    & dotnet build (Join-Path $root 'SapQuadTransitionPlugin\SapGmshReplacement.csproj') -c Release --no-incremental -p:DebugType=none -p:DebugSymbols=false -o $pluginBuild
    if ($LASTEXITCODE -ne 0) { throw 'V2 replacement plugin build failed.' }
    & dotnet build (Join-Path $root 'packaging\SQMV2Installer.csproj') -c Release --no-incremental -p:DebugType=none -p:DebugSymbols=false -o $installerBuild
    if ($LASTEXITCODE -ne 0) { throw 'V2 installer build failed.' }
    & dotnet build (Join-Path $root 'packaging\MESHingSetup.csproj') -c Release --no-incremental -p:DebugType=none -p:DebugSymbols=false -o $setupBuild
    if ($LASTEXITCODE -ne 0) { throw 'V2 graphical installer build failed.' }
    if (-not $SkipBackendBuild) {
        & python -m PyInstaller --noconfirm --clean --distpath (Join-Path $PSScriptRoot 'dist-v2') --workpath (Join-Path $PSScriptRoot 'build-v2') (Join-Path $PSScriptRoot 'SapGmshBackend.spec')
        if ($LASTEXITCODE -ne 0) { throw 'Frozen Gmsh V2 backend build failed.' }
    }
    $backend = Join-Path $PSScriptRoot 'dist-v2\SapQuadMesherBackend'
    if (-not (Test-Path -LiteralPath (Join-Path $backend 'SapQuadMesherBackend.exe'))) { throw 'V2 backend is missing.' }
    $source = Join-Path $PSScriptRoot 'cache\gmsh-4.15.2-source.tgz'
    if (-not (Test-Path -LiteralPath $source)) { throw 'Corresponding Gmsh sources missing.' }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne 'be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e') { throw 'Gmsh source hash mismatch.' }
    if (Test-Path -LiteralPath $stage) { throw "Release destination already exists: $stage" }
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($name in @('SapQuadTransitionPlugin.dll','sap_gmsh_mesher.py')) {
        Copy-Item -LiteralPath (Join-Path $pluginBuild $name) -Destination $stage
    }
    Copy-Item -LiteralPath (Join-Path $installerBuild 'SQMPluginInstaller.exe') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $setupBuild 'MESHingSetup.exe') -Destination $stage
    foreach ($name in @('install_MESHing.cmd','uninstall_MESHing.cmd','README-GMSH-V2.md')) {
        Copy-Item -LiteralPath (Join-Path $root "SapQuadTransitionPlugin\$name") -Destination $stage
    }
    Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\README-GMSH-V2.md') -Destination (Join-Path $stage 'README.md')
    foreach ($name in @('LICENSE','GPL-3.0.txt','LICENSE-APPLICATION.md','SAP2000-LINKING-EXCEPTION.md','THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native-gmsh-sources.json') -Destination (Join-Path $stage 'NATIVE-SOURCES.json')
    $sourceGuide = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SOURCE.template.md')).Replace('{VERSION}', [string]$version)
    [IO.File]::WriteAllText((Join-Path $stage 'SOURCE.md'), $sourceGuide, [Text.Encoding]::UTF8)
    if ($sourceGuide -notmatch [regex]::Escape("https://github.com/leonardobandini/MESHing/releases/tag/v$version")) {
        throw 'The packaged corresponding-source address does not match this release.'
    }
    Copy-Item -LiteralPath (Join-Path $pluginBuild 'docs') -Destination $stage -Recurse
    foreach ($language in @('it','en')) {
        $historicalHelp = Join-Path $stage "docs\help-gmsh-v2\legacy-$language.html"
        if (Test-Path -LiteralPath $historicalHelp -PathType Leaf) {
            Remove-Item -LiteralPath $historicalHelp
        }
    }
    Copy-Item -LiteralPath (Join-Path $pluginBuild 'img') -Destination $stage -Recurse
    Copy-Item -LiteralPath $backend -Destination (Join-Path $stage 'mesher') -Recurse
    $backendExe = Join-Path $stage 'mesher\SapQuadMesherBackend.exe'
    if ((Get-Item $backendExe).VersionInfo.FileVersion -ne "$version.0") {
        throw "Wrong backend file version: $((Get-Item $backendExe).VersionInfo.FileVersion)"
    }
    $reportedBackendVersion = & $backendExe --version
    if ($LASTEXITCODE -ne 0 -or $reportedBackendVersion -ne "MESHing backend $version") {
        throw "Wrong backend-reported version: $reportedBackendVersion"
    }
    $licenses = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    $gmshLicense = Join-Path $PSScriptRoot 'cache\gmsh-4.15.2-source\LICENSE.txt'
    if (-not (Test-Path -LiteralPath $gmshLicense)) { & tar -xzf $source -C (Join-Path $PSScriptRoot 'cache') 'gmsh-4.15.2-source/LICENSE.txt' }
    Copy-Item -LiteralPath $gmshLicense -Destination (Join-Path $licenses 'GMSH-LICENSE.txt')
    $pythonRoot = & python -c 'import sys; print(sys.base_prefix)'
    Copy-Item -LiteralPath (Join-Path $pythonRoot 'LICENSE.txt') -Destination (Join-Path $licenses 'PYTHON-LICENSE.txt')
    & python (Join-Path $PSScriptRoot 'verify-native-sources.py') $stage
    if ($LASTEXITCODE -ne 0) { throw 'Pinned native Gmsh dependency sources or licenses are missing.' }
    $fileVersion = (Get-Item (Join-Path $stage 'SapQuadTransitionPlugin.dll')).VersionInfo.FileVersion
    if ($fileVersion -ne "$version.0") { throw "Wrong V2 DLL version: $fileVersion" }
    foreach ($installerName in @('SQMPluginInstaller.exe','MESHingSetup.exe')) {
        $installerVersion = (Get-Item (Join-Path $stage $installerName)).VersionInfo.FileVersion
        if ($installerVersion -ne "$version.0") { throw "Wrong $installerName version: $installerVersion" }
    }
    & (Join-Path $stage 'SQMPluginInstaller.exe') verify $stage
    if ($LASTEXITCODE -ne 0) { throw 'Standalone installer verification failed.' }
    & python (Join-Path $root 'tools\VerifyGmshV2Help.py')
    if ($LASTEXITCODE -ne 0) { throw 'Bilingual help validation failed.' }
    $trial = Join-Path $build 'upgrade-trial'
    $old = Join-Path $artifacts 'Sap2000QuadMesher-1.0.120'
    if (Test-Path -LiteralPath (Join-Path $old 'SQMPluginInstaller.exe') -PathType Leaf) {
        & (Join-Path $old 'SQMPluginInstaller.exe') test-install $trial
        if ($LASTEXITCODE -ne 0) { throw 'V1 fixture installation failed.' }
        & (Join-Path $stage 'SQMPluginInstaller.exe') test-install $trial
        if ($LASTEXITCODE -ne 0) { throw 'V1 to V2 replacement failed.' }
        if ((Get-Item (Join-Path $trial 'SapQuadTransitionPlugin.dll')).VersionInfo.FileVersion -ne "$version.0" -or
            -not (Test-Path -LiteralPath (Join-Path $trial 'docs\help-gmsh-v2\index-it.html')) -or
            (Test-Path -LiteralPath (Join-Path $trial 'docs\help\index-it.html'))) { throw 'V1 files survived replacement or V2 files are missing.' }
    }
    $previous = if ($version -eq '2.0.12') { '2.0.11' } elseif ($version -eq '2.0.13') { '2.0.12' }
        elseif ($version -eq '2.0.23') { '2.0.22' } elseif ($version -eq '2.0.24') { '2.0.23' }
        elseif ($version -eq '2.0.26') { '2.0.24' }
        elseif ($version -eq '2.0.28') { '2.0.26' }
        elseif ($version -eq '2.0.29') { '2.0.28' }
        elseif ($version -eq '2.0.30') { '2.0.29' }
        elseif ($version -eq '2.0.32') { '2.0.30' }
        elseif ($version -eq '2.0.36') { '2.0.32' }
        elseif ($version -eq '2.0.37') { '2.0.36' }
        elseif ($version -eq '2.0.38') { '2.0.37' }
        elseif ($version -eq '2.0.39') { '2.0.38' }
        elseif ($version -eq '2.0.40') { '2.0.39' }
        elseif ($version -eq '2.0.41') { '2.0.40' }
        elseif ($version -eq '2.0.42') { '2.0.41' }
        elseif ($version -eq '2.0.43') { '2.0.42' }
        elseif ($version -eq '2.0.44') { '2.0.43' }
        elseif ($version -eq '2.0.45') { '2.0.44' }
        elseif ($version -eq '2.0.46') { '2.0.45' }
        elseif ($version -eq '2.0.50') { '2.0.46' }
        elseif ($version -eq '2.0.52') { '2.0.50' }
        elseif ($version -eq '2.0.53') { '2.0.52' }
        elseif ($version -eq '2.0.54') { '2.0.53' }
        elseif ($version -eq '2.0.57') { '2.0.54' }
        elseif ($version -eq '2.0.58') { '2.0.57' }
        elseif ($version -eq '2.0.59') { '2.0.58' }
        elseif ($version -eq '2.0.60') { '2.0.59' }
        elseif ($version -eq '2.0.61') { '2.0.59' }
        elseif ($version -eq '2.0.62') { '2.0.61' }
        elseif ($version -eq '2.0.63') { '2.0.62' }
        elseif ($version -eq '2.0.64') { '2.0.63' }
        elseif ($version -eq '2.0.65') { '2.0.64' }
        elseif ($version -eq '2.0.66') { '2.0.65' }
        elseif ($version -eq '2.0.67') { '2.0.66' } else { '2.0.13' }
    # Use the newest locally available previous build for the upgrade fixture;
    # an extracted source ZIP without previous builds remains self-contained.
    $availablePrevious = @(Get-ChildItem -LiteralPath $artifacts -Directory | ForEach-Object {
         if ($_.Name -match '^Sap2000QuadMesher-((?:2|3)[.]0[.]\d+)$' -and
            [version]$Matches[1] -lt [version]$version -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'SQMPluginInstaller.exe') -PathType Leaf)) {
            [pscustomobject]@{ Name = $Matches[1]; Version = [version]$Matches[1] }
        }
    } | Sort-Object Version -Descending)
    if ($availablePrevious.Count) { $previous = $availablePrevious[0].Name }
    $oldV2 = Join-Path $artifacts "Sap2000QuadMesher-$previous"
    if (Test-Path -LiteralPath (Join-Path $oldV2 'SQMPluginInstaller.exe') -PathType Leaf) {
        $trialV2 = Join-Path $build 'v2-upgrade-trial'
        & (Join-Path $oldV2 'SQMPluginInstaller.exe') test-install $trialV2
        if ($LASTEXITCODE -ne 0) { throw "V2 $previous fixture installation failed." }
        & (Join-Path $stage 'SQMPluginInstaller.exe') test-install $trialV2
        if ($LASTEXITCODE -ne 0 -or
            (Get-Item (Join-Path $trialV2 'SapQuadTransitionPlugin.dll')).VersionInfo.FileVersion -ne "$version.0") {
            throw "V2 $previous to $version replacement failed."
        }
    }
    $forbidden = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -in @('SAP2000v1.dll','CSiAPIv1.dll') -or $_.Extension -in @('.sdb','.bdb','.sbk') })
    if ($forbidden.Count) { throw 'The V2 package contains SAP2000 proprietary/test model files.' }
    $zip = $zipPath
    if (Test-Path -LiteralPath $zip) { throw "ZIP already exists: $zip" }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path -Leaf $zip)`n", [Text.Encoding]::ASCII)
    & python (Join-Path $PSScriptRoot 'build-source-v2.py') $version
    if ($LASTEXITCODE -ne 0) { throw 'V2 corresponding source archive failed.' }
    $gmshArtifact = Join-Path $artifacts 'gmsh-4.15.2-source.tgz'
    if (Test-Path -LiteralPath $gmshArtifact) {
        if ((Get-FileHash -LiteralPath $gmshArtifact -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) {
            throw 'Existing Gmsh source artifact differs from the pinned upstream source.'
        }
    } else { Copy-Item -LiteralPath $source -Destination $gmshArtifact }
    $gmshChecksum = "$gmshArtifact.sha256"
    $expectedChecksum = "$sourceHash  gmsh-4.15.2-source.tgz`n"
    if (Test-Path -LiteralPath $gmshChecksum) {
        if ([IO.File]::ReadAllText($gmshChecksum) -cne $expectedChecksum) {
            throw 'Existing Gmsh source checksum differs.'
        }
    } else { [IO.File]::WriteAllText($gmshChecksum, $expectedChecksum, [Text.Encoding]::ASCII) }
    "V2_PACKAGE=$zip"; "V2_SHA256=$hash"
} finally {
    if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
}
