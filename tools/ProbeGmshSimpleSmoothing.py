"""Compare triangulation, Simple and Blossom nodes on captured circular sectors.

Uses read-only SAP boundary traces; writes meshes only to %TEMP%/opencode.
This is a Gmsh diagnostic, not a SAP2000 regression run.
"""
import collections
import csv
import math
import os
import tempfile
from pathlib import Path
from unittest.mock import patch
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'SapQuadTransitionPlugin'))
from sap_gmsh_mesher import gmsh, mesh_areas  # noqa: E402

TRACE = ROOT / 'docs' / 'circular-floor-iterations' / '07-strict-gmsh'
SECTORS = ('11', '2', '3', '4', '5', '6', '7', '8')


def run(folder, name, recombine, method, smoothing):
    target = folder / name
    target.mkdir()
    captures = []
    generate = gmsh.model.mesh.generate

    def capture(dim):
        generate(dim)
        tags, xyz, _ = gmsh.model.mesh.getNodes()
        captures.append({int(tag): tuple(xyz[3 * index:3 * index + 3])
                         for index, tag in enumerate(tags)})

    settings = dict(algorithm=8, recombine=recombine, recombination=method,
                    optimize=False, optimizeMethod='Relocate2D', optimizeIterations=1,
                    smoothing=smoothing, structured={'mode': 'unstructured'},
                    options={'Mesh.MeshSizeMin': 0, 'Mesh.MeshSizeFactor': 1,
                             'Mesh.MeshSizeFromPoints': 0,
                             'Mesh.MeshSizeExtendFromBoundary': 0,
                             'Mesh.MeshSizeFromCurvature': 0,
                             'Mesh.MeshSizeFromParametricPoints': 0,
                             'Mesh.SubdivisionAlgorithm': 0,
                             'Mesh.AlgorithmSwitchOnFailure': 1,
                             **({'Mesh.RecombineMinimumQuality': .01,
                                 'Mesh.RecombineOptimizeTopology': 0,
                                 'Mesh.RecombineNodeRepositioning': 0} if recombine else {})})
    paths = [(str(TRACE / f'area-{sector}-boundary.csv'), str(target / f'mesh-{sector}.csv'))
             for sector in SECTORS]
    with patch.object(gmsh.model.mesh, 'generate', side_effect=capture):
        mesh_areas(paths, .5, settings)
    counts = collections.Counter()
    used = {}
    for _, path in paths:
        with open(path, newline='') as stream:
            for row in csv.reader(stream):
                if row[0] == 'N':
                    used[int(row[1])] = tuple(map(float, row[2:5]))
                elif row[0] == 'E':
                    counts['quad' if len(row) == 5 else 'tri'] += 1
    print(f'{name}: nodes={len(used)} triangles={counts["tri"]} quads={counts["quad"]} '
          f'faces={counts["tri"] + counts["quad"]} raw_after_generate={len(captures[0])}', flush=True)
    return used, captures[0]


def compare(first, second, label):
    a, b = first[0], second[0]
    shared = a.keys() & b.keys()
    deltas = [math.dist(a[key], b[key]) for key in shared]
    print(f'{label}: matching_tags={len(shared)}/{len(a)},{len(b)} '
          f'moved_above_1e-8={sum(value > 1e-8 for value in deltas)} '
          f'max_tag_displacement={max(deltas, default=0):.9g}', flush=True)


def main():
    parent = Path(tempfile.gettempdir()) / 'opencode'
    if not parent.is_dir() or any(not (TRACE / f'area-{s}-boundary.csv').is_file() for s in SECTORS):
        raise FileNotFoundError('Approved temporary directory or captured boundaries are missing')
    folder = Path(tempfile.mkdtemp(prefix='sqm-circular-recombine-', dir=parent))
    print('DIAGNOSTIC=' + str(folder), flush=True)
    tri = run(folder, 'tri-smooth1', False, 0, 1)
    simple = run(folder, 'simple-smooth1', True, 0, 1)
    blossom = run(folder, 'blossom-smooth1', True, 1, 1)
    compare(tri, simple, 'tri_vs_simple_smooth1')
    compare(tri, blossom, 'tri_vs_blossom_smooth1')
    tri0 = run(folder, 'tri-smooth0', False, 0, 0)
    simple0 = run(folder, 'simple-smooth0', True, 0, 0)
    blossom0 = run(folder, 'blossom-smooth0', True, 1, 0)
    compare(tri0, simple0, 'tri_vs_simple_smooth0')
    compare(tri0, blossom0, 'tri_vs_blossom_smooth0')
    compare(tri0, simple, 'tri_smooth0_vs_simple_smooth1')


if __name__ == '__main__':
    main()
