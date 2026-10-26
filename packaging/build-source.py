# Copyright (C) 2026 Leonardo Bandini
# SPDX-License-Identifier: GPL-2.0-or-later
"""Assemble corresponding GPL source beside the public binary release."""

import hashlib
import json
import sys
import tarfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree

import gmsh


ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts"
VERSION = ElementTree.parse(ROOT / "SapQuadTransitionPlugin" / "SapQuadTransitionPlugin.csproj").findtext(".//Version")
GMSH_SOURCE = ARTIFACTS / "gmsh-4.15.2-source.tgz"
GMSH_DLL = ARTIFACTS / f"Sap2000QuadMesher-{VERSION}" / "mesher" / "_internal" / "gmsh-4.15.dll"
BINDING = Path(gmsh.__file__).resolve()
GMSH_SOURCE_SHA = "be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e"
GMSH_DLL_SHA = "6cac3eefb477265d9fa60bbd869dbbbf7c7ca4cb308c0f8f2b43e91d43de3c1c"
GMSH_BINDING_SHA = "a56ebe69dc57a3ea15eee191cae4f1881b06784174b2d9df306a98bdd8b06606"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checksum(path):
    path.with_name(path.name + ".sha256").write_text(
        f"{sha256(path)}  {path.name}\n", encoding="ascii"
    )


def main():
    if len(sys.argv) != 2 or sys.argv[1] != VERSION:
        raise SystemExit(f"Expected release version {VERSION}")
    if gmsh.__version__ != "4.15.2":
        raise RuntimeError("Gmsh binding version differs from bundled source")
    for path, expected in (
        (GMSH_SOURCE, GMSH_SOURCE_SHA),
        (GMSH_DLL, GMSH_DLL_SHA),
        (BINDING, GMSH_BINDING_SHA),
    ):
        if sha256(path) != expected:
            raise RuntimeError(f"Corresponding source mismatch: {path}")
    with tarfile.open(GMSH_SOURCE) as gmsh_tar:
        if "gmsh-4.15.2-source/api/gmsh.py" not in gmsh_tar.getnames():
            raise RuntimeError("Upstream Gmsh API source is absent")

    sources = [
        "LICENSE", "GPL-3.0.txt", "LICENSE-APPLICATION.md", "SAP2000-LINKING-EXCEPTION.md",
        "THIRD_PARTY_NOTICES.md", "README.md", "CHANGELOG.md", "CONTRIBUTING.md",
        "Directory.Build.props", "requirements.txt", "img/logo.png",
        "img/quad-mesher.ico", "img/quad-mesher-icon.svg",
        "docs/PERFORMANCE.md", "docs/MULTICORE_PERFORMANCE_1_0_87.md", "docs/PROJECT_STATUS.md", "docs/DEVELOPMENT.md",
        "docs/MIGRATION_AND_RELEASE.md", "docs/UPDATES.md", "docs/PROPOSTA_INTERFACCIA_E_QUALITA_MESH.md",
        "docs/CATALOGO_OPZIONI_E_TOOLTIP_IT.md", "docs/QUALITA_MESH_AGGREGATA.md",
        "docs/help/index-it.html", "docs/help/index-en.html", "docs/help/help.css",
        "SapQuadTransitionPlugin/Plugin.cs", "SapQuadTransitionPlugin/ModelCsvSnapshot.cs",
        "SapQuadTransitionPlugin/SapQuadTransitionPlugin.csproj",
        "SapQuadTransitionPlugin/README.md", "SapQuadTransitionPlugin/install_plugin.cmd",
        "SapQuadTransitionPlugin/uninstall_plugin.cmd", "SapQuadTransitionPlugin/install_plugin.ps1",
        "SapQuadTransitionPlugin/uninstall_plugin.ps1",
        "SapQuadTransitionPlugin/sap_general_polygon_mesher.py",
        "SapQuadTransitionPlugin/sap_sector_blocks.py",
        "SapQuadTransitionPlugin/sap_dense_sector_band.py",
        "SapQuadTransitionPlugin/sap_sector_collar.py",
        "SapQuadTransitionPlugin/sap_sector_smoothing.py",
        "SapQuadTransitionPlugin/sap_sector_repair.py",
        "SapQuadTransitionPlugin/sap_rectangular_transition_mesher.py",
        "SapQuadTransitionPlugin/sap_quad_transition_mesher.py",
        "SapQuadTransitionPlugin/test_polygon_boundary.csv",
        "SapQuadTransitionPlugin/test_rectangle_coarse_boundary.csv",
        "packaging/mesher_backend.py", "packaging/SQMPluginInstaller.cs",
        "packaging/SQMPluginInstaller.csproj", "packaging/SapQuadMesherBackend.spec",
        "packaging/requirements-build.txt", "packaging/build-release.ps1",
        "packaging/build-source.py", "packaging/publish-release.py",
        "packaging/SOURCE.template.md",
        "packaging/SOURCE_BUILD.md",
        "tools/build-multicore-report.py", "tools/CompareMeshGeometry.py",
        "tools/BuildHelpAssets.py", "tools/BuildQuadMesherIcon.py",
        "tools/GenerateHelpScreenshots.ps1", "tools/TestTabbedOptionsLayout.ps1",
        "tools/VerifyBilingualHelp.py", "tools/RenderHelpPreview.py",
        "tools/TrimHelpScreenshots.py",
        "tools/TestSapCompletionWatcher.ps1", "tools/TestDisabledSymmetryCrash.ps1",
        "tools/CloseIsolatedCrashTest.ps1",
        "examples/atomic/build-report.py", "examples/atomic/stamp-existing-report.py",
        "examples/atomic/results/report-provenance.json",
        "examples/comparisons/MODO/measurements.json",
        "examples/atomic/results/SAP2000-Quad-Mesher-Atomic-Examples.docx",
        "examples/atomic/results/SAP2000-Quad-Mesher-Atomic-Examples.pdf",
        "examples/comparisons/MODO/MODO-performance-comparison.docx",
        "examples/comparisons/MODO/MODO-performance-comparison.pdf",
    ]
    sources.extend(
        str(path.relative_to(ROOT)).replace("\\", "/")
        for path in sorted((ROOT / "examples" / "comparisons" / "MODO").glob("MODO-*.svg"))
    )
    sources.extend(
        str(path.relative_to(ROOT)).replace("\\", "/")
        for path in sorted((ROOT / "docs" / "help" / "assets").iterdir()) if path.is_file()
    )
    for name in ("nTEST001", "nTEST005", "nTEST007", "nTEST010", "nTEST011"):
        sources.extend(f"examples/atomic/results/diagrams/{name}-{stage}.svg"
                       for stage in ("source", "result"))
    sources.append("examples/atomic/manifest.json")
    if len(sources) != len(set(sources)):
        raise RuntimeError("Duplicate corresponding-source paths")
    source_zip = ARTIFACTS / f"Sap2000QuadMesher-{VERSION}-source.zip"
    with zipfile.ZipFile(source_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for relative in sources:
            path = ROOT / relative
            if not path.is_file():
                raise FileNotFoundError(path)
            archive.write(path, relative)
        archive.write(BINDING, "third_party/gmsh.py")
        for name in ("GMSH-LICENSE.txt", "PYTHON-LICENSE.txt"):
            license_path = ARTIFACTS / f"Sap2000QuadMesher-{VERSION}" / "licenses" / name
            archive.write(license_path, "licenses/" + name)

    with zipfile.ZipFile(source_zip) as archive:
        names = set(archive.namelist())
        if "third_party/gmsh.py" not in names or "packaging/mesher_backend.py" not in names:
            raise RuntimeError("Incomplete source archive")
        if any(name.endswith(("SAP2000v1.dll", ".sdb", ".sbk")) for name in names):
            raise RuntimeError("Proprietary binary or SAP model in source archive")
    checksum(source_zip)
    checksum(GMSH_SOURCE)
    print(json.dumps({"source": str(source_zip), "sha256": sha256(source_zip),
                      "gmsh_source_sha256": GMSH_SOURCE_SHA}))


if __name__ == "__main__":
    main()
