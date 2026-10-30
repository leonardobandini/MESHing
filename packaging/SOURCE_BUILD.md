# Build MESHing from the corresponding source

This archive contains the preferred source for the MESHing plugin, installer,
Python backend and packaging scripts. The independently maintained upstream
Gmsh source is the `gmsh-4.15.2-source.tgz` asset on the **same release**.
The project's original code and Gmsh are GPL-2.0-or-later. Because the bundled
CPython runtime contains OpenSSL 3 (Apache-2.0), the combined binary elects
GPLv3. The complete license texts, notices and third-party terms are in
`LICENSE`, `GPL-3.0.txt`, `LICENSE-APPLICATION.md`,
`THIRD_PARTY_NOTICES.md` and `licenses/`.

The distributed `gmsh-4.15.dll` comes from the unmodified official Gmsh
4.15.2 Windows wheel (wheel SHA-256
`7b36083bb410fa27c5d0e052929d1a9844a5b09169d66017b72b41aabd49d711`,
DLL SHA-256 `6cac3eefb477265d9fa60bbd869dbbbf7c7ca4cb308c0f8f2b43e91d43de3c1c`).
The exact Python binding is included as `third_party/gmsh.py` (SHA-256
`a56ebe69dc57a3ea15eee191cae4f1881b06784174b2d9df306a98bdd8b06606`).
The upstream source tarball SHA-256 is
`be3f66f225d27ba9fa014f07e83169285da8a051b0e8ab7103d88066b39bdd3e`.

To build from the extracted archive on Windows x64, install .NET SDK and .NET
Framework 4.8, Python **3.12.7**, and either SAP2000 or CSiBridge 27+ with
`CSiAPIv1.dll`. The API DLL is supplied by the independently licensed host;
**it is not distributed with MESHing**. Install the
[official Microsoft Visual C++ Redistributable x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist),
version **14.42.34226.3 or later compatible**, separately before building or
running the standalone backend: Visual C++ runtime DLLs are not included in
MESHing. The graphical installer checks this prerequisite and, if necessary,
downloads Microsoft's signed installer before installing MESHing. From
the top directory of this source archive, in PowerShell:

```powershell
python -m pip install -r packaging\requirements-build.txt
New-Item -ItemType Directory -Path artifacts, packaging\cache -Force | Out-Null
Copy-Item -LiteralPath "<downloaded release>\gmsh-4.15.2-source.tgz" -Destination packaging\cache\gmsh-4.15.2-source.tgz
Copy-Item -LiteralPath "<downloaded release>\mmg-5.5.2-source.tar.gz" -Destination packaging\cache\mmg-5.5.2-source.tar.gz
foreach ($name in @('occt-7.8.1', 'fltk-1.3.11', 'med-4.1.0')) {
    Copy-Item -LiteralPath "third_party\native-sources\$name-source.tar.gz" -Destination "packaging\cache\$name-source.tar.gz"
}
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\build-release-v2.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\PrepareV306Candidate.ps1
```

The release script compiles `SapQuadTransitionPlugin/SapGmshReplacement.csproj`
against the locally installed CSiAPI, compiles `packaging/SQMV2Installer.csproj`
and the bilingual graphical installer `packaging/MESHingSetup.csproj`,
builds `packaging/SapGmshBackend.spec` with PyInstaller, verifies the pinned
Gmsh library and sources, validates the help, and creates matching binary and
corresponding-source ZIPs under `artifacts/`. An older V1/V2 installable in that
directory enables additional upgrade tests; it is not required to build the
current package. `packaging/verify-native-sources.py` checks the four upstream
source archives against immutable revisions and SHA-256 values in
`packaging/native-gmsh-sources.json` and stages their license texts. Three
archives are included above in the MESHing source ZIP; MMG is a separate asset
of the same release. The original PETSc/HDF5 permissive notices and the CGNS
reference text are already in `licenses/` in this source archive and each is
checked by its own hash. MMG 5.5.2 is pinned to the official tag after
observing its version in the bundled DLL's execution banner; CGNS has no
revision exposed by the unmodified official Gmsh wheel;
neither the sources nor this script claim a bit-for-bit rebuild of that wheel.
`-SkipBackendBuild` reuses a previously built frozen backend.

`PrepareV306Candidate.ps1` stages ten release assets and checksums under
`artifacts/candidate-v3.0.7/`, including a `MESHing-latest.zip` byte-identical
to the versioned installer. Publishing 3.0.7 as Latest moves the public link
and the in-plugin update feed to this release.
The 3.0.7 help is generated before building the plug-in and packaging sources.

`PrepareV305Candidate.ps1` stages eight versioned binary/source assets and
checksums under `artifacts/candidate-v3.0.5/`. This non-Latest release does not
contain a `MESHing-latest.zip` alias; the public moving download and in-plugin
update feed continue to resolve to the separately selected Latest release.
The 3.0.5 source ZIP includes `DiskModelCheckpoint.cs` alongside the unchanged
source for the legacy CSV rollback on non-FAST paths.

`PrepareV304Candidate.ps1` creates `artifacts/candidate-v3.0.4/` with only ten
MESHing assets. During local assembly, `MESHing-latest.zip` is copied byte-for-byte
from the versioned installer. Once published, the `latest` download link follows
the latest stable release and may differ from this versioned ZIP in the future.
Older preparation scripts remain in the source archive solely as historical
material and do not run when building MESHing 3.0.4.

The source build uses the pinned official Python 3.12.7 runtime. Its separate
source is available at https://www.python.org/downloads/release/python-3127/ ;
OpenSSL 3.0.15 sources at
https://github.com/openssl/openssl/releases/tag/openssl-3.0.15 . See
`THIRD_PARTY_NOTICES.md` for libffi, Microsoft and PyInstaller terms.
