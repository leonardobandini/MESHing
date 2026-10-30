# SPDX-License-Identifier: GPL-2.0-or-later
"""Dedicated frozen backend for the SAP2000 Gmsh v2 installation."""
import sys

from mesher_backend import ALLOWED_SCRIPTS, main, self_test


MESHING_VERSION = "3.0.5"

ALLOWED_SCRIPTS.add("sap_gmsh_mesher.py")

if __name__ == "__main__":
    if sys.argv[1:] == ["--version"]:
        print("MESHing backend " + MESHING_VERSION)
    elif sys.argv[1:] == ["--self-test"]:
        self_test()
        print("MESHing backend " + MESHING_VERSION)
    else:
        main()
