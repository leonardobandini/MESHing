# Copyright (C) 2026 Leonardo Bandini
# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "SapQuadTransitionPlugin\SapQuadTransitionPlugin.csproj"
$installerProject = Join-Path $PSScriptRoot "SQMPluginInstaller.csproj"
$installerBuildDir = Join-Path $PSScriptRoot "build\installer\net48"
$license = Join-Path $root "LICENSE"
$gpl3 = Join-Path $root "GPL-3.0.txt"
$licenseApplication = Join-Path $root "LICENSE-APPLICATION.md"
$apiException = Join-Path $root "SAP2000-LINKING-EXCEPTION.md"
$backendSpec = Join-Path $PSScriptRoot "SapQuadMesherBackend.spec"
$backendDist = Join-Path $PSScriptRoot "dist\SapQuadMesherBackend"
$buildDir = Join-Path $PSScriptRoot "build\plugin\net48"
$cacheDir = Join-Path $PSScriptRoot "cache"
$gmshSourceName = "gmsh-4.15.2-source.tgz"
$gmshSource = Join-Path $cacheDir $gmshSourceName
$gmshSourceUrl = "https://gmsh.info/src/$gmshSourceName"
$gmshSourceSha256 = "be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e"
$reportDir = Join-Path $root "examples\atomic\results"
$reportDocx = Join-Path $reportDir "SAP2000-Quad-Mesher-Atomic-Examples.docx"
$reportPdf = Join-Path $reportDir "SAP2000-Quad-Mesher-Atomic-Examples.pdf"
$comparisonDir = Join-Path $root "examples\comparisons\MODO"

if (-not (Test-Path $license)) {
    throw "LICENSE non trovata. Scegliere una licenza prima di creare un pacchetto distribuibile."
}
if (((Get-Content $license -TotalCount 2) -join ' ') -notmatch 'GENERAL PUBLIC LICENSE' -or
    ((Get-Content $license -TotalCount 2) -join ' ') -notmatch 'Version 2' -or
    -not (Test-Path $gpl3) -or
    -not (Test-Path $licenseApplication) -or -not (Test-Path $apiException)) {
    throw 'Testi GPLv2/GPLv3, applicazione della licenza o eccezione API mancanti.'
}

[xml]$projectXml = Get-Content $project
$version = $projectXml.Project.PropertyGroup.Version
if (-not $version) {
    throw "Versione non trovata in $project"
}

if (-not $SkipBuild) {
    & python -c "import gmsh, PyInstaller; assert gmsh.__version__ == '4.15.2'; assert PyInstaller.__version__ == '6.16.0'"
    if ($LASTEXITCODE -ne 0) {
        throw "Dipendenze di build mancanti. Eseguire: python -m pip install -r packaging\requirements-build.txt"
    }
    & dotnet build $project -c $Configuration -o $buildDir
    if ($LASTEXITCODE -ne 0) {
        throw "Build fallita con codice $LASTEXITCODE."
    }
    & dotnet build $installerProject -c $Configuration -o $installerBuildDir
    if ($LASTEXITCODE -ne 0) {
        throw "Build dell'installer fallita con codice $LASTEXITCODE."
    }
    & python -m PyInstaller --noconfirm --clean --distpath (Join-Path $PSScriptRoot "dist") --workpath (Join-Path $PSScriptRoot "build") $backendSpec
    if ($LASTEXITCODE -ne 0) {
        throw "Build del runtime Python/Gmsh fallita con codice $LASTEXITCODE."
    }
}

$artifactDir = Join-Path $root "artifacts"
$packageName = "Sap2000QuadMesher-$version"
$stageDir = Join-Path $artifactDir $packageName
$zipPath = Join-Path $artifactDir "$packageName.zip"

if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item $stageDir -ItemType Directory -Force | Out-Null

$backend = Join-Path $backendDist "SapQuadMesherBackend.exe"
if (-not (Test-Path $backend)) {
    throw "Backend autonomo non trovato: $backend"
}
& $backend --self-test
if ($LASTEXITCODE -ne 0) {
    throw "Self-test del backend fallito con codice $LASTEXITCODE."
}

New-Item $cacheDir -ItemType Directory -Force | Out-Null
if (-not (Test-Path $gmshSource)) {
    Invoke-WebRequest $gmshSourceUrl -OutFile $gmshSource
}
$gmshHash = (Get-FileHash $gmshSource -Algorithm SHA256).Hash.ToLowerInvariant()
if ($gmshHash -ne $gmshSourceSha256) {
    throw "Checksum del sorgente Gmsh non valido: $gmshHash"
}
$gmshLicense = Join-Path $cacheDir "gmsh-4.15.2-source\LICENSE.txt"
if (-not (Test-Path $gmshLicense)) {
    & tar -xzf $gmshSource -C $cacheDir "gmsh-4.15.2-source/LICENSE.txt"
    if ($LASTEXITCODE -ne 0) {
        throw "Estrazione della licenza Gmsh fallita."
    }
}
$pythonRoot = & python -c "import sys; print(sys.base_prefix)"
$pythonLicense = Join-Path $pythonRoot "LICENSE.txt"
if (-not (Test-Path $pythonLicense)) {
    throw "Licenza Python non trovata: $pythonLicense"
}

$files = @(
    (Join-Path $buildDir "SapQuadTransitionPlugin.dll"),
    (Join-Path $installerBuildDir "SQMPluginInstaller.exe"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_general_polygon_mesher.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_sector_blocks.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_dense_sector_band.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_sector_collar.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_sector_smoothing.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_sector_repair.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_rectangular_transition_mesher.py"),
    (Join-Path $root "SapQuadTransitionPlugin\sap_quad_transition_mesher.py"),
    (Join-Path $root "SapQuadTransitionPlugin\install_plugin.cmd"),
    (Join-Path $root "SapQuadTransitionPlugin\uninstall_plugin.cmd"),
    (Join-Path $root "SapQuadTransitionPlugin\README.md"),
    (Join-Path $root "requirements.txt"),
    (Join-Path $root "THIRD_PARTY_NOTICES.md"),
    $license, $gpl3, $licenseApplication, $apiException
)

foreach ($file in $files) {
    if (-not (Test-Path $file)) {
        throw "File richiesto non trovato: $file"
    }
    Copy-Item $file $stageDir
}
$pluginVersion = (Get-Item (Join-Path $stageDir 'SapQuadTransitionPlugin.dll')).VersionInfo.FileVersion
if ($pluginVersion -ne "$version.0") {
    throw "DLL non coerente con la versione del pacchetto: attesa $version.0, trovata $pluginVersion."
}

Copy-Item $backendDist (Join-Path $stageDir "mesher") -Recurse
$docsDir = New-Item (Join-Path $stageDir "docs") -ItemType Directory -Force
foreach ($report in @($reportDocx, $reportPdf)) {
    if (-not (Test-Path $report)) {
        throw "Report richiesto non trovato: $report"
    }
    Copy-Item $report $docsDir
}
$performanceNotes = Join-Path $root 'docs\PERFORMANCE.md'
if (-not (Test-Path $performanceNotes)) { throw "Note sulle prestazioni non trovate: $performanceNotes" }
Copy-Item $performanceNotes $docsDir
$multicoreReport = Join-Path $root 'docs\MULTICORE_PERFORMANCE_1_0_87.md'
if (-not (Test-Path $multicoreReport)) { throw "Report multicore v1.0.87 non trovato: $multicoreReport" }
Copy-Item $multicoreReport $docsDir
Copy-Item (Join-Path $root 'CHANGELOG.md') $docsDir
$layoutProposal = Join-Path $root 'docs\PROPOSTA_INTERFACCIA_E_QUALITA_MESH.md'
if (-not (Test-Path $layoutProposal)) { throw "Proposta interfaccia e report non trovata: $layoutProposal" }
Copy-Item $layoutProposal $docsDir
foreach ($guide in @('CATALOGO_OPZIONI_E_TOOLTIP_IT.md', 'QUALITA_MESH_AGGREGATA.md')) {
    $guidePath = Join-Path $root ('docs\' + $guide)
    if (-not (Test-Path $guidePath)) { throw "Documentazione interfaccia/qualita non trovata: $guidePath" }
    Copy-Item $guidePath $docsDir
}
$helpSource = Join-Path $root 'docs\help'
$helpAssets = Join-Path $helpSource 'assets'
if (-not (Test-Path (Join-Path $helpSource 'index-it.html')) -or
    -not (Test-Path (Join-Path $helpSource 'index-en.html')) -or
    -not (Test-Path (Join-Path $helpSource 'help.css')) -or
    @(Get-ChildItem $helpAssets -Filter '*.svg' -File).Count -ne 20 -or
    @(Get-ChildItem $helpAssets -Filter 'ui-*.png' -File).Count -ne 8) {
    throw 'Guida bilingue incompleta: richiesti 20 diagrammi SVG e 8 screenshot reali.'
}
Copy-Item $helpSource (Join-Path $docsDir 'help') -Recurse
$comparisonDocs = New-Item (Join-Path $docsDir "comparisons") -ItemType Directory -Force
foreach ($extension in @('docx', 'pdf')) {
    $comparisonReport = Join-Path $comparisonDir "MODO-performance-comparison.$extension"
    if (-not (Test-Path $comparisonReport)) { throw "Report richiesto non trovato: $comparisonReport" }
    Copy-Item $comparisonReport $comparisonDocs
}
$comparisonSvgs = @(Get-ChildItem $comparisonDir -Filter 'MODO-*.svg' -File)
if ($comparisonSvgs.Count -ne 6) { throw "Sono richiesti sei SVG MODO prima/dopo; trovati $($comparisonSvgs.Count)" }
$comparisonSvgs | Copy-Item -Destination $comparisonDocs
$logo = Join-Path $root 'img\logo.png'
if (-not (Test-Path $logo)) { throw "Logo richiesto non trovato: $logo" }
$imageDir = New-Item (Join-Path $stageDir 'img') -ItemType Directory -Force
Copy-Item $logo (Join-Path $imageDir 'logo.png')
foreach ($iconName in @('quad-mesher.ico', 'quad-mesher-icon.svg')) {
    $iconPath = Join-Path $root ('img\' + $iconName)
    if (-not (Test-Path $iconPath)) { throw "Icona plugin mancante: $iconPath" }
    Copy-Item $iconPath (Join-Path $imageDir $iconName)
}
& python (Join-Path $root 'tools\VerifyBilingualHelp.py') $stageDir
if ($LASTEXITCODE -ne 0) { throw 'Verifica della guida offline bilingue fallita.' }
$licensesDir = New-Item (Join-Path $stageDir "licenses") -ItemType Directory -Force
Copy-Item $gmshLicense (Join-Path $licensesDir "GMSH-LICENSE.txt")
Copy-Item $pythonLicense (Join-Path $licensesDir "PYTHON-LICENSE.txt")
$sourceGuide = (Get-Content (Join-Path $PSScriptRoot 'SOURCE.template.md') -Raw).Replace('{VERSION}', $version)
Set-Content -Path (Join-Path $stageDir 'SOURCE.md') -Value $sourceGuide -Encoding UTF8

$installer = Join-Path $stageDir 'SQMPluginInstaller.exe'
& $installer verify $stageDir
if ($LASTEXITCODE -ne 0) { throw 'Verifica del pacchetto installabile fallita.' }
$spacedPackage = Join-Path $env:TEMP ("opencode\SAP2000 Quad Mesher verify-" + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path (Split-Path -Parent $spacedPackage))) { throw 'Cartella dei test installer non trovata.' }
try {
    Copy-Item $stageDir $spacedPackage -Recurse
    & (Join-Path $spacedPackage 'install_plugin.cmd') verify
    if ($LASTEXITCODE -ne 0) { throw 'Verifica del launcher CMD da un percorso con spazi fallita.' }
} finally {
    Remove-Item $spacedPackage -Recurse -Force -ErrorAction SilentlyContinue
}
$testRoot = Join-Path $env:TEMP ("opencode\SAP2000QuadMesher-test-" + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path (Split-Path -Parent $testRoot))) { throw 'Cartella dei test installer non trovata.' }
try {
    foreach ($run in 1..2) {
        & $installer test-install $testRoot
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $testRoot 'SapQuadTransitionPlugin.dll'))) {
            throw "Prova installazione/sostituzione fallita al tentativo $run."
        }
    }
} finally {
    Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$smokeTests = @(
    @("sap_general_polygon_mesher.py", "test_polygon_boundary.csv", "0.2"),
    @("sap_rectangular_transition_mesher.py", "test_rectangle_coarse_boundary.csv", "0.25"),
    @("sap_quad_transition_mesher.py", "test_rectangle_coarse_boundary.csv", "0.25")
)
foreach ($test in $smokeTests) {
    $script = Join-Path $stageDir $test[0]
    $fixture = Join-Path $root "SapQuadTransitionPlugin\$($test[1])"
    $output = Join-Path $env:TEMP "sap_quad_release_$([Guid]::NewGuid().ToString('N')).csv"
    try {
        & (Join-Path $stageDir "mesher\SapQuadMesherBackend.exe") $script $fixture $output $test[2] | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $output) -or (Get-Item $output).Length -eq 0) {
            throw "Smoke test fallito per $($test[0])."
        }
    }
    finally {
        Remove-Item $output -Force -ErrorAction SilentlyContinue
    }
}

$forbidden = Get-ChildItem $stageDir -Recurse -File | Where-Object {
    $_.Name -ieq "SAP2000v1.dll" -or $_.Extension -in ".sdb", ".sbk", ".$2k"
}
if ($forbidden) {
    throw "Il pacchetto contiene file non distribuibili o fixture SAP2000: $($forbidden.FullName -join ', ')"
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
$sourceArtifact = Join-Path $artifactDir $gmshSourceName
Copy-Item $gmshSource $sourceArtifact -Force
$sourceBuilder = Join-Path $PSScriptRoot 'build-source.py'
& python $sourceBuilder $version
if ($LASTEXITCODE -ne 0) { throw 'Sorgenti corrispondenti o provenienza della DLL Gmsh non validi.' }
$hash = Get-FileHash $zipPath -Algorithm SHA256
Set-Content -Path "$zipPath.sha256" -Value "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zipPath))" -Encoding ascii

Write-Host "Pacchetto: $zipPath"
Write-Host "SHA-256:  $($hash.Hash.ToLowerInvariant())"
Write-Host "Sorgente Gmsh: $sourceArtifact"
Write-Host "Sorgenti del plugin: $(Join-Path $artifactDir "Sap2000QuadMesher-$version-source.zip")"
