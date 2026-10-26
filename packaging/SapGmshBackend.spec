# SPDX-License-Identifier: GPL-2.0-or-later
# Separate from the v1 frozen backend; the output keeps the executable name
# expected by the shared C# SAP2000 adapter.
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
    [str(root / "packaging" / "mesher_backend_v2.py")],
    pathex=[str(root), str(root / "packaging")],
    binaries=[(str(gmsh_dll), ".")],
    datas=[],
    hiddenimports=["csv", "ctypes", "gmsh", "json", "math", "os", "shutil", "subprocess", "time", "pathlib"],
    hookspath=[], hooksconfig={}, runtime_hooks=[], excludes=["numpy"],
    noarchive=False, optimize=0,
)
# The Visual C++ runtime is an external prerequisite installed by Microsoft.
# Fail if the expected DLLs are absent from dependency analysis: otherwise a
# changed Python environment could silently produce a different package.
external_runtime = {"VCRUNTIME140.DLL", "VCRUNTIME140_1.DLL"}
found = {Path(entry[0]).name.upper() for entry in a.binaries} & external_runtime
if found != external_runtime:
    raise RuntimeError(f"Expected both Visual C++ runtime DLLs in analysis, found {found}")
a.binaries = [entry for entry in a.binaries if Path(entry[0]).name.upper() not in external_runtime]

pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="SapQuadMesherBackend",
           debug=False, bootloader_ignore_signals=False, strip=False, upx=True,
           console=True, disable_windowed_traceback=False,
           version=str(root / "packaging" / "BackendVersion.txt"))
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=True,
               upx_exclude=[], name="SapQuadMesherBackend")
