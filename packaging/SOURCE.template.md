# Corresponding source for MESHing v{VERSION}

The fixed installer `MESHing-{VERSION}.zip` and its corresponding sources and
checksums are assets of the **same** MESHing release:

https://github.com/leonardobandini/MESHing/releases/tag/v{VERSION}

At publication, `MESHing-latest.zip` is an asset of this release and is
byte-for-byte identical to `MESHing-{VERSION}.zip`. The `/releases/latest/`
link is moving: once a later release becomes Latest, it may point to a
different ZIP. Its `.sha256` link verifies the currently downloaded alias;
use the versioned ZIP and checksum below for a fixed binary.

| Asset | Download |
| --- | --- |
| `MESHing-{VERSION}.zip` (installer) | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/MESHing-{VERSION}.zip |
| `MESHing-{VERSION}.zip.sha256` | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/MESHing-{VERSION}.zip.sha256 |
| `MESHing-latest.zip` (moving latest-stable alias) | https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip |
| `MESHing-latest.zip.sha256` (moving alias checksum) | https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip.sha256 |
| `MESHing-{VERSION}-source.zip` (C# plug-in, Python backend, exact Gmsh Python binding, build and installation scripts) | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/MESHing-{VERSION}-source.zip |
| `MESHing-{VERSION}-source.zip.sha256` | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/MESHing-{VERSION}-source.zip.sha256 |
| `gmsh-4.15.2-source.tgz` (unmodified upstream Gmsh source) | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/gmsh-4.15.2-source.tgz |
| `gmsh-4.15.2-source.tgz.sha256` | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/gmsh-4.15.2-source.tgz.sha256 |
| `mmg-5.5.2-source.tar.gz` (official MmgTools tag confirmed by the DLL execution banner) | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/mmg-5.5.2-source.tar.gz |
| `mmg-5.5.2-source.tar.gz.sha256` | https://github.com/leonardobandini/MESHing/releases/download/v{VERSION}/mmg-5.5.2-source.tar.gz.sha256 |

`NATIVE-SOURCES.json` in the installer and `packaging/native-gmsh-sources.json`
in the source archive record upstream URLs, immutable revisions and SHA-256
values for native dependency sources.
The OCCT 7.8.1, FLTK 1.3.11 and MED 4.1.0 source archives are included in
`MESHing-{VERSION}-source.zip` at `third_party/native-sources/`;
MMG 5.5.2 and Gmsh 4.15.2 are separate source assets of this same release.
PETSc and HDF5 have permissive licenses: their original notices are included
with official upstream source links in `THIRD_PARTY_NOTICES.md`. The unmodified Gmsh wheel's
build options mention CGNS but do not expose its internal revision. MMG 5.5.2
was identified from the bundled DLL's execution banner; no CGNS revision or
byte-for-byte rebuild recipe of the Windows wheel is claimed. The original Gmsh
4.15.2 source, licensing texts, binding and checksums remain available as
listed above.

The original project code, now MESHing, is GPL-2.0-or-later. Gmsh 4.15.2 is
GPL-2.0-or-later under its own copyright holders, with a limited exception.
For the combined Python/Gmsh backend containing OpenSSL 3 (Apache-2.0), this
distribution elects the GPLv3 option allowed by the "or later" clauses. This
does not change Gmsh's original license or exception. Third-party components
retain their respective licenses; see `LICENSE`, `GPL-3.0.txt`,
`LICENSE-APPLICATION.md`, `THIRD_PARTY_NOTICES.md` and `licenses/`.

CPython 3.12.7, OpenSSL 3.0.15, libffi and the PyInstaller bootloader retain
their own terms; details and upstream source locations are in
`THIRD_PARTY_NOTICES.md` and `licenses/PYTHON-LICENSE.txt`. Microsoft Visual
C++ runtime DLLs are **not included**. The installer checks for the
[official Microsoft Visual C++ Redistributable x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist),
version **14.42.34226.3 or later compatible**. If missing, it downloads the
separate installer from Microsoft and launches its interactive setup before
installing MESHing. The CSI API must be
obtained independently from an authorized SAP2000 or CSiBridge installation;
no CSI binary is included.

If you redistribute the binary, you must satisfy the applicable GPL
corresponding-source requirements yourself.
