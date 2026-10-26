# Full-quad 1D constraints

Boundary CSVs captured from disposable copies of the two `17_bug` models,
using their complete selected area/joint/frame selections. These are Gmsh
inputs for reproducing 6/Full-Quad methods 2 and 3 offline. The `FRAME`
records represent existing SAP2000 frame objects; mesh guide stations must
not be mistaken for edits to those frame objects.

`circular-bamg-boundary.csv` and its options capture the originally reported
7/BAMG/Simple/0.3 m selection. It is intentionally rejected with an actionable
message rather than presenting a PyInstaller/Gmsh traceback or importing
collapsed shells into SAP2000.
