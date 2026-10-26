# SPDX-License-Identifier: GPL-2.0-or-later
# Build a disposable package using the existing verified V2 backend.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$parent=Join-Path $env:TEMP 'opencode'
$source=Join-Path $root 'artifacts\Sap2000QuadMesher-2.0.13'
$dll=Join-Path $root 'SapQuadTransitionPlugin\bin\Release\net48\SapQuadTransitionPlugin.dll'
if(-not (Test-Path -LiteralPath $parent -PathType Container) -or
   -not (Test-Path -LiteralPath $source -PathType Container) -or
   -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Missing trial build parent or source package.' }
$refs=[Reflection.Assembly]::LoadFrom($dll).GetReferencedAssemblies()
if(@($refs|Where-Object Name -eq 'CSiAPIv1').Count -ne 1 -or
   @($refs|Where-Object Name -eq 'SAP2000v1').Count -ne 0) { throw 'V2 DLL must reference only xAPI.' }
$stage=Join-Path $parent ('sqm-xapi-parent-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse
Copy-Item -LiteralPath $dll -Destination (Join-Path $stage 'SapQuadTransitionPlugin.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'SapQuadTransitionPlugin\sap_gmsh_mesher.py') -Destination $stage -Force
$help=Join-Path $stage 'docs\help-gmsh-v2'
if(-not (Test-Path -LiteralPath $help -PathType Container)) { throw 'Staged V2 help directory missing.' }
Copy-Item -Path (Join-Path $root 'docs\help-gmsh-v2\*') -Destination $help -Recurse -Force
$installer=Join-Path $stage 'SQMPluginInstaller.exe'
& $installer verify $stage
if($LASTEXITCODE -ne 0) { throw "Trial package validation failed: $stage" }
"TRIAL_PLUGIN=$((Join-Path $stage 'SapQuadTransitionPlugin.dll'))"
"TRIAL_ROOT=$stage"
