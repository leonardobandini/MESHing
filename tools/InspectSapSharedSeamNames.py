"""Read-only check of named SAP joint chains along a source area's sides."""
import collections
import math
import re
import sys
from pathlib import Path


def parse(path):
    points, areas = {}, {}
    for line in path.read_text(encoding='utf-8-sig', errors='replace').splitlines():
        joint = re.search(r'\bJoint=(\S+)', line)
        x = re.search(r'\bXorR=([^\s]+)', line)
        y = re.search(r'\bY=([^\s]+)', line)
        z = re.search(r'\bZ=([^\s]+)', line)
        if joint and x and y and z:
            points[joint.group(1)] = tuple(map(float, (x.group(1), y.group(1), z.group(1))))
        area = re.search(r'\bArea=(\S+)', line)
        vertices = re.findall(r'\bJoint\d+=(\S+)', line)
        if area and len(vertices) >= 3:
            areas[area.group(1)] = vertices
    return points, areas


def on_segment(point, first, last):
    d = tuple(b - a for a, b in zip(first, last))
    length_squared = sum(value * value for value in d)
    if length_squared <= 1e-14:
        return False
    t = sum((p - a) * value for p, a, value in zip(point, first, d)) / length_squared
    return -1e-7 <= t <= 1 + 1e-7 and math.dist(point, tuple(a + t * value for a, value in zip(first, d))) <= 1e-7


def inspect(points, areas, area_name):
    source = areas[area_name]
    for index, start_name in enumerate(source):
        end_name = source[(index + 1) % len(source)]
        first, last = points[start_name], points[end_name]
        graph = collections.defaultdict(set)
        for other_name, boundary in areas.items():
            if other_name == area_name:
                continue
            for i, a in enumerate(boundary):
                b = boundary[(i + 1) % len(boundary)]
                if on_segment(points[a], first, last) and on_segment(points[b], first, last) and points[a] != points[b]:
                    graph[a].add(b)
                    graph[b].add(a)
        reached = {start_name}
        pending = collections.deque((start_name,))
        while pending:
            for neighbor in graph[pending.popleft()] - reached:
                reached.add(neighbor)
                pending.append(neighbor)
        connected = end_name in reached
        print(f'EDGE={start_name}->{end_name} CONNECTED_BY_NAMES={connected} '
              f'REACHABLE_STATIONS={len(reached) if connected else 0} '
              f'GEOMETRIC_NEIGHBOR_STATIONS={len(graph)}')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('Usage: python tools/InspectSapSharedSeamNames.py MODEL.$2k AREA_NAME')
    coords, shells = parse(Path(sys.argv[1]))
    print(f'INPUT_JOINTS={len(coords)} INPUT_AREAS={len(shells)} SOURCE={sys.argv[2]}')
    inspect(coords, shells, sys.argv[2])
