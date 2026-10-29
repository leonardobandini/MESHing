# SPDX-License-Identifier: GPL-2.0-or-later
"""Ship complete corresponding V2 sources next to the binary installer."""
import hashlib
import json
import subprocess
import sys
import zipfile
from pathlib import Path

import gmsh

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts"


def sha(path):
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
        return digest.hexdigest()


def main(version):
    stage = ARTIFACTS / f"Sap2000QuadMesher-{version}"
    binary = stage / "mesher" / "_internal" / "gmsh-4.15.dll"
    source = ROOT / "packaging" / "cache" / "gmsh-4.15.2-source.tgz"
    if gmsh.__version__ != "4.15.2" or sha(source) != "be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e":
        raise RuntimeError("Corresponding Gmsh source or version mismatch")
    if sha(binary) != "6cac3eefb477265d9fa60bbd869dbbbf7c7ca4cb308c0f8f2b43e91d43de3c1c":
        raise RuntimeError("Bundled Gmsh DLL differs from pinned upstream source")
    binding = Path(gmsh.__file__).resolve()
    if sha(binding) != "a56ebe69dc57a3ea15eee191cae4f1881b06784174b2d9df306a98bdd8b06606":
        raise RuntimeError("Gmsh Python binding differs from pinned source")
    if (ROOT / '.git').exists():
        tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode("utf-8").split("\0")
    else:
        # A corresponding-source ZIP must itself be buildable without a Git index.
        tracked = [path.relative_to(ROOT).as_posix() for path in ROOT.rglob('*')
                   if path.is_file() and not any(part in ('bin', 'obj', 'build', 'build-v2',
                       'dist', 'dist-v2', 'cache', 'artifacts') for part in path.relative_to(ROOT).parts)]
    allowed = (".py", ".cs", ".csproj", ".ps1", ".cmd", ".md", ".html", ".css",
               ".svg", ".json", ".props", ".spec", ".txt", ".png", ".ico")
    shipped_v2 = {"PluginV2.cs", "ModelCsvSnapshot.cs", "GmshPreviewPanel.cs",
                  "SapGmshReplacement.csproj", "sap_gmsh_mesher.py",
                  "README-GMSH-V2.md", "test_gmsh_v2.py",
                   "install_plugin.cmd", "uninstall_plugin.cmd",
                   "install_MESHing.cmd", "uninstall_MESHing.cmd"}
    names = {name for name in tracked if (name.startswith(("packaging/", "docs/help-gmsh-v2/")) or
             name.startswith("SapQuadTransitionPlugin/") and name.split("/", 1)[1] in shipped_v2)
              and name.endswith(allowed) and "/bin/" not in name and "/obj/" not in name
              and not name.startswith("docs/help-gmsh-v2/legacy-")
              and (ROOT / name).is_file()}
    names.update(str(path.relative_to(ROOT)).replace("\\", "/")
                  for path in (ROOT / "docs" / "help-gmsh-v2").rglob("*")
                  if path.is_file() and not path.name.startswith('legacy-'))
    names.update(str(path.relative_to(ROOT)).replace("\\", "/")
                  for path in (ROOT / "examples" / "bug17" / "geometry").glob("*")
                  if path.is_file() and path.suffix in (".csv", ".json", ".md"))
    names.update(str(path.relative_to(ROOT)).replace("\\", "/")
                 for path in (ROOT / "img").glob("*") if path.is_file())
    names.update({
         ".github/workflows/latest-installer.yml",
         "SapQuadTransitionPlugin/SapGmshReplacement.csproj",
         "SapQuadTransitionPlugin/PluginV2.cs",
          "SapQuadTransitionPlugin/GmshPreviewPanel.cs",
         "SapQuadTransitionPlugin/install_MESHing.cmd",
         "SapQuadTransitionPlugin/uninstall_MESHing.cmd",
          "README.md", "CHANGELOG.md", "packaging/SOURCE.template.md",
          "docs/MESHING_3_0_3_RELEASE.md",
          "docs/MESHING_3_0_4_RELEASE.md",
         "packaging/native-gmsh-sources.json", "packaging/verify-native-sources.py",
         "packaging/SQMV2Installer.csproj", "packaging/MESHingSetup.csproj",
         "packaging/MESHingSetup.cs", "packaging/BackendVersion.txt",
         "packaging/build-release-v2.ps1", "packaging/PrepareV266Candidate.ps1",
          "packaging/PrepareV267Candidate.ps1", "packaging/PrepareV268Candidate.ps1",
           "packaging/PrepareV300Candidate.ps1",
            "packaging/PrepareV301Candidate.ps1",
            "packaging/PrepareV303Candidate.ps1",
            "packaging/PrepareV304Candidate.ps1",
        "packaging/build-source-v2.py", "packaging/TestV2Standalone.ps1",
        "packaging/TestV2MenuMigration.ps1", "packaging/InstallV2FromZip.ps1",
         "packaging/TestMESHingHostDiscovery.ps1",
         "tools/VerifyGmshV2Help.py", "tools/BrandMESHingHelp.py",
         "tools/BrandMESHingNotices.py",
        "tools/TestFEMeshNumericTest01.ps1", "tools/ProbeBug02Snapshot.ps1",
         "Directory.Build.props", "requirements.txt",
         ".gitattributes",
        "LICENSE", "GPL-3.0.txt", "LICENSE-APPLICATION.md", "SAP2000-LINKING-EXCEPTION.md",
        "THIRD_PARTY_NOTICES.md", "packaging/requirements-build.txt",
        "tools/TestUpdateNotice.ps1", "tools/TestOpeningMembership.ps1",
         "tools/TestModalLockWarning.ps1", "tools/TestV2LockLifecycle.ps1",
         "tools/TestUnsavedModelHost.ps1",
         "tools/TestUnsavedModelUi.ps1", "tools/TestVisualCppPrerequisite.ps1",
          "tools/TestMESHingSetupUi.ps1", "tools/TestMicrosoftPrerequisiteDownload.ps1",
        "run_test01_fast.cs", "RunTest01Fast.csproj", "FastTest/RunTest01Fast.csproj",
         "packaging/TestMESHingMenus.ps1", "tools/VerifyFEMeshBridgeResults.py",
        "tools/TestFEMeshHosts.ps1",
        "tools/UpdateFEMeshGuideBrand.py", "tools/UpdateFEMeshHelpV259.py",
        "tools/UpdateFEMeshHelpV260.py", "tools/UpdateFEMeshHelpV261.py",
        "tools/RunXApiAtomic.ps1", "tools/TrialParentChild.cs", "tools/TrialParentChild.csproj",
        "tools/TrialSnapshotUndo.cs", "tools/TrialSnapshotUndo.csproj",
        "tools/ProbeDesignWarning.cs", "tools/ProbeDesignWarning.csproj",
        "tools/StageV2XApiTrial.ps1",
        "tools/TrialInterpolatedAssignments.cs", "tools/TrialInterpolatedAssignments.csproj",
         "tools/RunGmshV2Regression.ps1", "tools/RunGmshV2FullRegression.ps1",
          "tools/RunMESHingV303Matrix.ps1",
          "tools/RunMESHingV304Matrix.ps1",
         "tools/RunMESHingRegressionWorker.ps1", "tools/TestGmshSettingsZOrder.ps1",
         "tools/TestMESHingTableNumbers.ps1", "tools/VerifyMESHingV302Matrix.py",
         "tools/BumpMESHingHelpVersion.py",
        "tools/RunGmshV2Examples.ps1",
        "tools/TestGmshUiPreview.ps1",
        "tools/CaptureGmshV2Help.ps1",
        "tools/TestV2ModelSwitch.cs", "tools/TestV2ModelSwitch.csproj",
        "tools/RunV2ModelSwitch.ps1", "tools/RunV240PreviewUndo.ps1",
        "tools/RunV2ProtectedEdgeTrial.ps1",
        "tools/VerifyV241PublicRegression.py",
        "tools/VerifyV242PublicRegression.py",
        "tools/ProbeGmshSimpleSmoothing.py", "tools/InstallLocalV2Candidate.ps1",
        "tools/TestV2GenerationAreaDialog.ps1", "tools/TestV2CloseUnappliedSap.ps1",
        "tools/UpdateV242Help.py",
        "tools/UpdateV243Help.py",
        "tools/UpdateV244Help.py", "tools/RunV2GroupReproducer.ps1",
        "tools/InspectSapSharedSeamNames.py", "tools/TestV2TopologyAwareJointIndex.ps1",
        "tools/TestV2SapApiErrorMessage.ps1", "tools/TestV2OptionalTransferWarning.ps1",
        "tools/TestGmshSizeQuality.ps1", "tools/TestV2QualityLayout.ps1",
        "tools/TestSapUnitContext.ps1",
          "tools/UpdateV246Help.py",
    })
    target = ARTIFACTS / f"MESHing-{version}-source.zip"
    if target.exists():
        raise FileExistsError(target)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name in sorted(names):
            path = ROOT / name
            if not path.is_file():
                raise FileNotFoundError(path)
            archive.write(path, name)
        archive.write(binding, "third_party/gmsh.py")
        native_sources = json.loads((ROOT / "packaging" / "native-gmsh-sources.json").read_text(encoding="utf-8"))
        for item in native_sources["components"]:
            if item["archive"] == "mmg-5.5.2-source.tar.gz":
                continue  # The versioned MMG source is a separate release asset.
            source_archive = ROOT / "packaging" / "cache" / item["archive"]
            if sha(source_archive) != item["sha256"]:
                raise ValueError("Pinned native source does not match " + item["archive"])
            archive.write(source_archive, "third_party/native-sources/" + item["archive"])
        for license_file in sorted((stage / "licenses").glob("*.txt")):
            archive.write(license_file, "licenses/" + license_file.name)
    (ARTIFACTS / (target.name + ".sha256")).write_text(f"{sha(target)}  {target.name}\n", encoding="ascii")
    print(f"V2_SOURCE={target}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python packaging/build-source-v2.py VERSION")
    main(sys.argv[1])
