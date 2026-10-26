# Copyright (C) 2026 Leonardo Bandini
# SPDX-License-Identifier: GPL-2.0-or-later
import runpy
import sys
from pathlib import Path


ALLOWED_SCRIPTS = {
    "sap_general_polygon_mesher.py",
    "sap_quad_transition_mesher.py",
    "sap_rectangular_transition_mesher.py",
}


def self_test():
    import gmsh

    if gmsh.__version__ != "4.15.2":
        raise RuntimeError(f"Expected Gmsh 4.15.2, found {gmsh.__version__}")
    gmsh.initialize()
    gmsh.finalize()
    print(f"Python {sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}; Gmsh {gmsh.__version__}")


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        return
    if len(sys.argv) < 5:
        raise SystemExit("Usage: SapQuadMesherBackend.exe SCRIPT INPUT OUTPUT MAX_SIZE")

    script = Path(sys.argv[1]).resolve()
    if script.name not in ALLOWED_SCRIPTS or not script.is_file():
        raise SystemExit(f"Unsupported or missing mesher script: {script}")

    if str(script.parent) not in sys.path:
        sys.path.insert(0, str(script.parent))
    sys.argv = [str(script), *sys.argv[2:]]
    runpy.run_path(str(script), run_name="__main__")


if __name__ == "__main__":
    main()
