# License of MESHing (formerly FEMesh)

Copyright (C) 2026 Leonardo Bandini (original material only).

Leonardo Bandini is identified as the author of the original project code.
**Only that code and documentation, to the extent he holds the rights,** are
licensed under GPL-2.0-or-later. The full GPLv2 text is in `LICENSE`; the
GPLv3 text is in `GPL-3.0.txt`. These texts are the license terms, not a
promise of warranty or a claim to own anyone else's work.

This license grants **no rights in SAP2000, CSiBridge, CSiAPIv1.dll or Gmsh**.
The CSI host and API must be obtained independently. The additional permission
in `SAP2000-LINKING-EXCEPTION.md` applies only to original C# code to the
extent the author holds its rights; it does not license CSI software, Gmsh or
other third-party works. See `THIRD_PARTY_NOTICES.md` and `licenses/`.

Gmsh 4.15.2 is GPL-2.0-or-later under its own copyright holders' terms. For
the combined Python/Gmsh backend binary containing OpenSSL 3 (Apache-2.0),
distribution elects the GPLv3 option permitted by the "or later" clauses of
both the original FEMesh code and Gmsh. Other components retain their own
licenses. This choice does not alter Gmsh's original license or exception.

Each distributed release provides the corresponding MESHing/backend source
archive and the upstream Gmsh 4.15.2 source archive alongside the unmodified
official Gmsh wheel binary, identified by version and checksums. The distribution
also lists four pinned upstream native source archives in `NATIVE-SOURCES.json`;
these are supplementary assets, not a claim that MESHing rebuilt the upstream
Windows wheel byte for byte. Further component notices and upstream sources
are identified in `THIRD_PARTY_NOTICES.md`. A release tag alone is not a
substitute for making the listed sources available to recipients.
