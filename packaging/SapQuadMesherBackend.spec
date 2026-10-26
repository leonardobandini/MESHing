# Copyright (C) 2026 Leonardo Bandini
# SPDX-License-Identifier: GPL-2.0-or-later
from pathlib import Path

import gmsh


root = Path(SPECPATH).parent
gmsh_module = Path(gmsh.__file__).resolve()
gmsh_dll = next(
    (candidate for candidate in (
        gmsh_module.with_name("gmsh-4.15.dll"),
        gmsh_module.parent.parent / "gmsh-4.15.dll",
    ) if candidate.is_file()),
    None,
)
if gmsh_dll is None:
    raise FileNotFoundError(f"Gmsh DLL not found near: {gmsh_module}")

a = Analysis(
    [str(root / "packaging" / "mesher_backend.py")],
    pathex=[str(root)],
    binaries=[(str(gmsh_dll), ".")],
    datas=[],
    hiddenimports=["csv", "ctypes", "gmsh", "json", "math", "os", "shutil", "subprocess", "time", "pathlib"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["numpy"],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="SapQuadMesherBackend",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="SapQuadMesherBackend",
)
