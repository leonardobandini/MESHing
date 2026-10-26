# SPDX-License-Identifier: GPL-2.0-or-later
"""Gmsh 2D adapter for the shared SAP2000 boundary/mesh CSV protocol.

CLI: boundary.csv mesh.csv max_size settings.json
Only first-order planar triangles and quadrilaterals can become SAP shell objects.
"""
import csv
import json
import math
import sys
from itertools import combinations
from pathlib import Path

import gmsh


ALGORITHMS_2D = {1, 2, 3, 5, 6, 7, 8, 11}
OPTIMIZERS_2D = {"Laplace2D", "Relocate2D", "QuadQuasiStructured"}
FIELD_TYPES = {
    "AttractorAnisoCurve", "AutomaticMeshSizeField", "Ball", "BoundaryLayer", "Box",
    "Constant", "Curvature", "Cylinder", "Distance", "Extend", "ExternalProcess",
    "Frustum", "Gradient", "IntersectAniso", "Laplacian", "LonLat", "MathEval",
    "MathEvalAniso", "Max", "MaxEigenHessian", "Mean", "Min", "MinAniso", "Octree",
    "Param", "PostView", "Restrict", "Structured", "Threshold",
}


def read_boundary(path):
    corners = {}
    edges = {}
    interior = []
    frames = []
    opening_frames = []
    selected = []
    preserved_edges = set()
    holes = []
    symmetry = {}
    with open(path, newline="") as stream:
        for row in csv.reader(stream):
            if not row:
                continue
            kind = row[0]
            if kind == "PRESERVE_SELECTED_EDGE":
                preserved_edges.add(int(row[1]))
                continue
            if kind == "HOLE":
                diameter = float(row[3])
                center = tuple(float(value) for value in row[-3:])
                if diameter <= 0 or not math.isfinite(diameter) or len(center) != 3 or not all(
                        math.isfinite(value) for value in center):
                    raise ValueError("Invalid SAP2000 circular hole")
                holes.append((center, diameter / 2))
                continue
            if kind in ("SYMMETRY", "SYMMETRY2"):
                center = tuple(float(value) for value in row[1:4])
                direction = tuple(float(value) for value in row[4:7])
                length = math.sqrt(sum(value * value for value in direction))
                if len(center) != 3 or len(direction) != 3 or not all(
                        math.isfinite(value) for value in center + direction) or length <= 1e-12:
                    raise ValueError("Invalid SAP2000 symmetry axis")
                symmetry[kind] = (center, tuple(value / length for value in direction))
                continue
            if kind == "LOCK_EDGE":
                continue  # A locked edge already has its prescribed end points.
            if kind == "FRAME":
                start = tuple(float(value) for value in row[4:7])
                end = tuple(float(value) for value in row[7:10])
                if len(start) != 3 or len(end) != 3 or not all(
                        math.isfinite(value) for value in start + end) or start == end:
                    raise ValueError("Invalid selected SAP2000 frame: " + row[2])
                frame = (row[2], start, end)
                frames.append(frame)
                if len(row) > 11 and row[11] == "1":
                    opening_frames.append(frame)
                continue
            if kind not in ("CORNER", "EDGE", "INTERIOR", "EXPLICIT_POINT"):
                continue
            point = tuple(float(value) for value in row[4:7])
            if len(point) != 3 or not all(math.isfinite(value) for value in point):
                raise ValueError("Invalid SAP2000 boundary coordinate")
            if kind == "CORNER":
                corners[int(row[1])] = point
            elif kind == "EDGE":
                edges.setdefault(int(row[1]), []).append((float(row[3]), point))
            elif kind == "EXPLICIT_POINT":
                selected.append(point)
            else:
                interior.append(point)
    if len(corners) < 3 or sorted(corners) != list(range(1, len(corners) + 1)):
        raise ValueError("A planar area with at least three ordered corners is required")
    return corners, edges, interior, frames, selected, preserved_edges, holes, opening_frames, symmetry


def validate_constraints(nodes, elements, interior, frames):
    """Reject meshes with unembedded joints or with a frame that is not a mesh-edge chain."""
    used = {tag for element in elements for tag in element}
    for point in interior:
        if not any(math.dist(point, nodes[tag]) <= 1e-7 for tag in used):
            raise ValueError("A selected SAP2000 joint is missing from the shell mesh: " + str(point))
    edges = {tuple(sorted((element[i], element[(i + 1) % len(element)])))
             for element in elements for i in range(len(element))}
    for name, first, last in frames:
        length = math.dist(first, last)
        tol = max(1e-7, length * 1e-8)
        intervals = []
        for a_tag, b_tag in edges:
            stations = []
            for tag in (a_tag, b_tag):
                point = nodes[tag]
                t = sum((point[i] - first[i]) * (last[i] - first[i]) for i in range(3)) / length ** 2
                closest = tuple(first[i] + t * (last[i] - first[i]) for i in range(3))
                if t < -tol / length or t > 1 + tol / length or math.dist(point, closest) > tol:
                    break
                stations.append(min(1., max(0., t)))
            if len(stations) == 2 and abs(stations[0] - stations[1]) > 1e-9:
                intervals.append(tuple(sorted(stations)))
        covered = 0.
        for low, high in sorted(intervals):
            if low > covered + tol / length:
                break
            covered = max(covered, high)
        if covered < 1 - tol / length:
            raise ValueError("The generated shell mesh does not follow selected frame " + name)


def check_settings(settings, max_size):
    if not math.isfinite(max_size) or max_size <= 0:
        raise ValueError("Mesh size must be positive and finite")
    if int(settings["algorithm"]) not in ALGORITHMS_2D:
        raise ValueError("Unsupported Gmsh 2D algorithm: " + str(settings["algorithm"]))
    if int(settings["recombination"]) not in range(4):
        raise ValueError("Unsupported Gmsh 2D recombination method")
    if int(settings["smoothing"]) not in range(101):
        raise ValueError("Smoothing steps must be between 0 and 100")
    if settings.get("optimize"):
        if settings.get("optimizeMethod", "Laplace2D") not in OPTIMIZERS_2D:
            raise ValueError("Unsupported 2D optimizer: " + str(settings.get("optimizeMethod")))
        if int(settings.get("optimizeIterations", 1)) < 1 or (
                not settings.get("previewMode") and int(settings.get("optimizeIterations", 1)) > 100):
            raise ValueError("Optimizer iterations must be positive" if settings.get("previewMode")
                             else "Optimizer iterations must be between 1 and 100")
    extras = settings.get("options", {})
    if int(extras.get("Mesh.SubdivisionAlgorithm", 0)) == 2:
        raise ValueError("All-hexahedra subdivision requires a 3D volume; selected SAP2000/CSiBridge shells are 2D")
    if settings.get("preserveSelectedEdges") and int(extras.get("Mesh.SubdivisionAlgorithm", 0)) == 1:
        raise ValueError("All-quad subdivision bisects prescribed SAP2000 edges; disable it when preserving selected edges")
    if float(extras.get("Mesh.MeshSizeMin", 0)) > max_size:
        raise ValueError("Mesh.MeshSizeMin cannot exceed the SAP2000 maximum size")
    if not isinstance(extras, dict):
        raise ValueError("Gmsh Mesh.* options must be a numeric object")
    if settings.get("previewMode"):
        fixed = {"Mesh.MeshSizeFactor": 1, "Mesh.MeshSizeFromPoints": 0,
                 "Mesh.MeshSizeExtendFromBoundary": 0, "Mesh.MeshSizeFromCurvature": 0,
                 "Mesh.MeshSizeFromParametricPoints": 0, "Mesh.SubdivisionAlgorithm": 0}
        if any(extras.get(name) != value for name, value in fixed.items()):
            raise ValueError("Gmsh preview fixed mesh options were changed")
        minimum = float(extras.get("Mesh.MeshSizeMin", -1))
        if not math.isfinite(minimum) or minimum < 0 or minimum >= max_size:
            raise ValueError("Min Mesh Size must be nonnegative and below Max Mesh Size")
        if int(settings["algorithm"]) == 11 and settings["recombine"]:
            raise ValueError("Quasi-Structured Quad must disable recombination")
        if int(extras.get("Mesh.AlgorithmSwitchOnFailure", -1)) not in (0, 1):
            raise ValueError("Invalid automatic algorithm switch setting")
    structured = settings.get("structured") or {}
    if structured.get("mode", "unstructured") not in ("unstructured", "automatic", "manual"):
        raise ValueError("Unsupported Gmsh transfinite mode")
    if structured.get("mode") == "manual":
        if settings.get("oGrid"):
            raise ValueError("Circular O-grid supplies its own transfinite ring; disable manual transfinite for this geometry")
        if int(structured.get("nodes", 0)) < 2 or structured.get("distribution") not in (
                "Progression", "Bump", "Beta") or float(structured.get("coefficient", 0)) <= 0:
            raise ValueError("Manual transfinite requires >=2 nodes, distribution and positive coefficient")
        if structured.get("arrangement", "Left") not in ("Left", "Right", "AlternateLeft", "AlternateRight"):
            raise ValueError("Unsupported transfinite triangle arrangement")
    field = settings.get("localField")
    if field:
        values = [float(field[name]) for name in ("sizeNear", "sizeFar", "distanceNear", "distanceFar")]
        if not all(math.isfinite(x) for x in values) or min(values) < 0 or values[0] <= 0 or values[1] <= 0 or values[3] <= values[2]:
            raise ValueError("Invalid Distance/Threshold field sizes or distances")
        if field.get("target") not in ("frames", "joints", "both"):
            raise ValueError("Size field target must be selected frames and/or joints")
        if float(extras.get("Mesh.MeshSizeMin", 0)) > values[0]:
            raise ValueError("Mesh.MeshSizeMin would clamp the local Distance/Threshold refinement")
    graph = settings.get("fieldGraph") or []
    if not isinstance(graph, list) or (field and graph):
        raise ValueError("Use one background size field: either Distance/Threshold or a field graph")
    for entry in graph:
        if not isinstance(entry, dict) or entry.get("type") not in FIELD_TYPES or not isinstance(entry.get("options", {}), dict):
            raise ValueError("Invalid Gmsh mesh size field definition")


def add_field_graph(entries, frame_curves, selected_point_tags):
    """Apply documented Gmsh 4.15 field types with numeric/list/string options.

    Field references use preceding symbolic ids. Geometry lists may refer to
    $selectedFrames (CurvesList) or $selectedJoints (PointsList).
    """
    resolved = {}
    background = None
    for item in entries:
        name = item.get("id")
        if name is not None and (not isinstance(name, str) or not name or name in resolved):
            raise ValueError("Gmsh field ids must be unique non-empty strings")
        tag = gmsh.model.mesh.field.add(item["type"])
        for key, value in item.get("options", {}).items():
            if not isinstance(key, str) or not key or not key[0].isalpha():
                raise ValueError("Invalid Gmsh field option name")
            if value == "$selectedFrames" and key == "CurvesList":
                value = frame_curves
            elif value == "$selectedJoints" and key == "PointsList":
                value = selected_point_tags
            elif key == "InField" and isinstance(value, str) and value in resolved:
                value = resolved[value]
            elif key == "FieldsList" and isinstance(value, list):
                value = [resolved.get(element, element) if isinstance(element, str) else element
                         for element in value]
            if isinstance(value, str):
                gmsh.model.mesh.field.setString(tag, key, value)
            elif isinstance(value, list):
                gmsh.model.mesh.field.setNumbers(tag, key, [float(number) for number in value])
            elif isinstance(value, (int, float)) and math.isfinite(value):
                gmsh.model.mesh.field.setNumber(tag, key, value)
            else:
                raise ValueError("Invalid Gmsh field value for " + key)
        if name is not None:
            resolved[name] = tag
        if item.get("background"):
            if background is not None:
                raise ValueError("Only one Gmsh background size field can be active")
            background = tag
        if item.get("boundaryLayer"):
            gmsh.model.mesh.field.setAsBoundaryLayer(tag)
    return background


def add_ogrid_transition_field(curves, near_size, max_size):
    """Grade the free mesh away from the outer O-grid seam, not the hole itself."""
    distance = gmsh.model.mesh.field.add("Distance")
    gmsh.model.mesh.field.setNumbers(distance, "CurvesList", sorted(curves))
    gmsh.model.mesh.field.setNumber(distance, "Sampling", 30)
    threshold = gmsh.model.mesh.field.add("Threshold")
    for key, value in (("InField", distance), ("SizeMin", near_size),
                       ("SizeMax", max_size), ("DistMin", near_size),
                       ("DistMax", max(3 * near_size, max_size))):
        gmsh.model.mesh.field.setNumber(threshold, key, value)
    return threshold


def normal_of(corners):
    for a, b, c in combinations(corners.values(), 3):
        u = [b[i] - a[i] for i in range(3)]
        v = [c[i] - a[i] for i in range(3)]
        cross = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        if sum(component * component for component in cross) > 1e-20:
            return cross
    raise ValueError("Degenerate source area")


def inside_source_area(point, corners):
    """Disambiguate OCC fragments claimed by touching, non-overlapping areas."""
    normal = normal_of(corners)
    axes = [axis for axis in range(3) if axis != max(range(3), key=lambda i: abs(normal[i]))]
    x, y = (point[axis] for axis in axes)
    polygon = [tuple(vertex[axis] for axis in axes) for vertex in corners.values()]
    inside = False
    for (ax, ay), (bx, by) in zip(polygon, polygon[1:] + polygon[:1]):
        if (ay > y) != (by > y) and x < ax + (y - ay) * (bx - ax) / (by - ay):
            inside = not inside
    return inside


def on_source_plane(point, corners, tolerance=1e-8):
    normal = normal_of(corners)
    norm = math.sqrt(sum(value * value for value in normal))
    origin = next(iter(corners.values()))
    return abs(sum(normal[axis] * (point[axis] - origin[axis]) for axis in range(3))) <= tolerance * norm


def inside_or_on_source_area(point, corners, tolerance=1e-8):
    normal = normal_of(corners)
    axes = [axis for axis in range(3) if axis != max(range(3), key=lambda i: abs(normal[i]))]
    x, y = (point[axis] for axis in axes)
    polygon = [tuple(vertex[axis] for axis in axes) for vertex in corners.values()]
    for (ax, ay), (bx, by) in zip(polygon, polygon[1:] + polygon[:1]):
        abx, aby = bx - ax, by - ay
        length = math.hypot(abx, aby)
        if length and abs((x - ax) * aby - (y - ay) * abx) <= tolerance * length and \
                -tolerance <= (x - ax) * abx + (y - ay) * aby <= length * length + tolerance:
            return True
    return inside_source_area(point, corners)


def make_ogrid_rings(geometry, geo, point_tag, max_size, allow_partial=False):
    """Build conforming transfinite annular patches; keep SAP ownership per sector."""
    holes = {}
    for (corners, _, _, _, _, _, local_holes, _, _), _ in geometry:
        for center, radius in local_holes:
            key = tuple(round(value, 8) for value in center) + (round(radius, 8),)
            holes.setdefault(key, (center, radius, corners))
    ring_areas = [[] for _ in geometry]
    carve_areas = [[] for _ in geometry]
    ring_faces = set()
    ring_distances = []
    corner_notches = {}
    for center, radius, host in holes.values():
        unit = tuple((host[2][i] - host[1][i]) / math.dist(host[1], host[2]) for i in range(3))
        normal = normal_of(host)
        norm = math.sqrt(sum(value * value for value in normal))
        normal = tuple(value / norm for value in normal)
        side = (normal[1] * unit[2] - normal[2] * unit[1],
                normal[2] * unit[0] - normal[0] * unit[2],
                normal[0] * unit[1] - normal[1] * unit[0])
        clearances = []
        for other_center, other_radius, other_host in holes.values():
            if other_center == center and other_radius == radius:
                continue
            if abs(sum(normal[axis] * (other_center[axis] - center[axis])
                       for axis in range(3))) < 1e-8:
                clearances.append((math.dist(center, other_center) - radius - other_radius) / 2)
        for data, _ in geometry:
            for point in data[2] + data[4]:
                if abs(sum(normal[axis] * (point[axis] - center[axis])
                           for axis in range(3))) < 1e-8:
                    clearances.append(math.dist(point, center) - radius)
            for _, start, end in data[3]:
                if max(abs(sum(normal[axis] * (point[axis] - center[axis])
                               for axis in range(3))) for point in (start, end)) >= 1e-8:
                    continue
                segment = tuple(end[axis] - start[axis] for axis in range(3))
                length_sq = sum(value * value for value in segment)
                t = max(0, min(1, sum((center[axis] - start[axis]) * segment[axis]
                                    for axis in range(3)) / length_sq))
                nearest = tuple(start[axis] + t * segment[axis] for axis in range(3))
                clearances.append(math.dist(center, nearest) - radius)
        width = min(radius * .8, max_size * 1.5, min(clearances, default=float('inf')) * .8)
        if width <= 1e-8:
            raise ValueError("Circular O-grid has insufficient radial clearance")
        outer = radius + width
        ring_distances.append((center, radius, outer))
        count = max(8, math.ceil(2 * math.pi * outer / max_size - 1e-9))
        count = int(math.ceil(count / 8) * 8)

        def ring_point(distance, sector):
            angle = 2 * math.pi * sector / count
            offsets = [distance * (math.cos(angle) * unit[axis] + math.sin(angle) * side[axis])
                       for axis in range(3)]
            # At quadrant seams cos(pi/2) is ~6e-17 rather than exactly 0.
            # Exact coincidence is essential for OCC cutting and conformity.
            return tuple(center[axis] if abs(offsets[axis]) < 1e-12 else center[axis] + offsets[axis]
                         for axis in range(3))

        inner = [ring_point(radius, sector) for sector in range(count)]
        outer_points = [ring_point(outer, sector) for sector in range(count)]
        active = []
        for sector in range(count):
            next_sector = (sector + 1) % count
            vertices = (inner[sector], inner[next_sector], outer_points[sector], outer_points[next_sector])
            mid = tuple(sum(vertex[axis] for vertex in vertices) / 4 for axis in range(3))
            matching = [index for index, (data, _) in enumerate(geometry)
                        if on_source_plane(mid, data[0]) and inside_source_area(mid, data[0]) and
                        all(inside_or_on_source_area(vertex, data[0]) for vertex in vertices)]
            boundary_center = any(abs(math.dist(host[edge], center) +
                                      math.dist(center, host[edge % len(host) + 1]) -
                                      math.dist(host[edge], host[edge % len(host) + 1])) < 1e-8
                                  for edge in host)
            if not matching and allow_partial and len(geometry) == 1 and boundary_center:
                active.append(None)
                continue
            if len(matching) != 1:
                raise ValueError("O-grid sectors cross a SAP2000 area seam or plate boundary; align the radial sectors with the seams")
            active.append(matching[0])
        for index, (data, _) in enumerate(geometry):
            if not any(owner == index for owner in active):
                continue
            for point in data[2] + data[4]:
                if math.dist(point, center) < outer - 1e-8:
                    raise ValueError("A selected SAP2000 joint lies inside the O-grid transition ring")
            for _, start, end in data[3]:
                segment = tuple(end[i] - start[i] for i in range(3))
                length_sq = sum(value * value for value in segment)
                t = max(0, min(1, sum((center[i] - start[i]) * segment[i] for i in range(3)) / length_sq))
                nearest = tuple(start[i] + t * segment[i] for i in range(3))
                if math.dist(center, nearest) < outer - 1e-8:
                    raise ValueError("A selected SAP2000 frame crosses the O-grid transition ring")
            if all(owner == index for owner in active):
                carve_areas[index].append(outer_points)
            else:
                starts = [sector for sector, owner in enumerate(active)
                          if owner == index and active[(sector - 1) % count] != index]
                if len(starts) != 1:
                    raise ValueError("O-grid requires one continuous sector interval in each SAP2000 area")
                first = starts[0]
                length = 0
                while active[(first + length) % count] == index:
                    length += 1
                arc = [outer_points[(first + offset) % count] for offset in range(length + 1)]
                corners = data[0]
                corner = next((number for number, point in corners.items()
                               if math.dist(point, center) <= 1e-8), None)
                if corner is None:
                    edge = next((number for number in corners if
                                 abs(math.dist(corners[number], center) +
                                     math.dist(center, corners[number % len(corners) + 1]) -
                                     math.dist(corners[number], corners[number % len(corners) + 1])) < 1e-8), None)
                    if edge is None:
                        carve_areas[index].append([center] + arc)
                    else:
                        start = corners[edge]
                        if math.dist(start, arc[0]) > math.dist(start, arc[-1]):
                            arc.reverse()
                        corner_notches[index] = (None, center, outer, arc, edge)
                else:
                    if index in corner_notches:
                        raise ValueError("Two O-grids cannot replace the same SAP2000 area corner")
                    before = corners[(corner - 2) % len(corners) + 1]
                    after = corners[corner % len(corners) + 1]
                    prev_on_edge = lambda point: abs(math.dist(before, point) + math.dist(point, center) -
                                                      math.dist(before, center)) < 1e-7
                    next_on_edge = lambda point: abs(math.dist(after, point) + math.dist(point, center) -
                                                      math.dist(after, center)) < 1e-7
                    if prev_on_edge(arc[-1]) and next_on_edge(arc[0]):
                        arc.reverse()
                    elif not (prev_on_edge(arc[0]) and next_on_edge(arc[-1])):
                        raise ValueError("O-grid radial cuts do not align with the selected SAP2000 area corner")
                    corner_notches[index] = (corner, center, outer, arc, None)
        radial = [geo.addLine(point_tag(inner[i]), point_tag(outer_points[i])) for i in range(count)]
        # An inner chord with three transfinite stations places its midpoint
        # inside the specified hole. Mesh the true circular arc instead so
        # every station on the hole boundary has exactly the requested radius.
        boundary_in = [geo.addCircleArc(point_tag(inner[i]), point_tag(center),
                                        point_tag(inner[(i + 1) % count]), center=True)
                       for i in range(count)]
        boundary_out = [geo.addLine(point_tag(outer_points[i]), point_tag(outer_points[(i + 1) % count]))
                        for i in range(count)]
        for sector, owner in enumerate(active):
            if owner is None:
                continue
            next_sector = (sector + 1) % count
            loop = geo.addWire([radial[sector], boundary_out[sector],
                                -radial[next_sector], -boundary_in[sector]])
            face = geo.addPlaneSurface([loop])
            ring_areas[owner].append(face)
            ring_faces.add(face)
    return ring_areas, carve_areas, ring_faces, ring_distances, corner_notches


def reflect_elements_exactly(nodes, elements, corners, symmetry):
    """Reuse one half/quarter mesh and mirror it about the selected SAP axes."""
    axes = [symmetry[key] for key in ('SYMMETRY', 'SYMMETRY2') if key in symmetry]
    if not axes:
        return elements
    normal = normal_of(corners)
    length = math.sqrt(sum(component * component for component in normal))
    normal = tuple(component / length for component in normal)
    sides = []
    for center, direction in axes:
        side = (normal[1] * direction[2] - normal[2] * direction[1],
                normal[2] * direction[0] - normal[0] * direction[2],
                normal[0] * direction[1] - normal[1] * direction[0])
        sides.append((center, direction, side))

    def signed(point, center, side):
        return sum((point[axis] - center[axis]) * side[axis] for axis in range(3))

    representative = []
    for element in elements:
        centroid = tuple(sum(nodes[tag][axis] for tag in element) / len(element) for axis in range(3))
        if not all(signed(centroid, center, side) > 1e-8 for center, _, side in sides):
            continue
        if any(signed(nodes[tag], center, side) < -1e-7 for tag in element
               for center, _, side in sides):
            raise ValueError("Gmsh shells cross a selected symmetry axis; the geometry must be split on both axes")
        representative.append(element)
    if not representative:
        raise ValueError("No shell elements were generated on the selected symmetry half/quarter")
    originals = {tuple(round(value, 9) for value in coords): tag for tag, coords in nodes.items()}
    next_tag = max(nodes) + 1
    result = []
    for mask in range(1 << len(sides)):
        for element in representative:
            mapped = []
            for tag in element:
                point = nodes[tag]
                for index, (center, direction, _) in enumerate(sides):
                    if not mask & (1 << index):
                        continue
                    offset = [point[axis] - center[axis] for axis in range(3)]
                    along = sum(offset[axis] * direction[axis] for axis in range(3))
                    reflected = tuple(center[axis] + 2 * along * direction[axis] - offset[axis]
                                      for axis in range(3))
                    point = point if math.dist(reflected, point) < 1e-9 else reflected
                key = tuple(round(value, 9) for value in point)
                if key not in originals:
                    originals[key] = next_tag
                    nodes[next_tag] = point
                    next_tag += 1
                mapped.append(originals[key])
            result.append(tuple(mapped))
    return result


def write_mesh(output, nodes, elements, normal):
    used = {tag for element in elements for tag in element}
    with open(output, "w", newline="") as stream:
        writer = csv.writer(stream)
        for tag in sorted(used):
            writer.writerow(("N", tag, *nodes[tag]))
        for element in elements:
            a, b, c = (nodes[tag] for tag in element[:3])
            u = [b[i] - a[i] for i in range(3)]
            v = [c[i] - a[i] for i in range(3)]
            cross = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
            if sum(cross[i] * normal[i] for i in range(3)) < 0:
                element = tuple(reversed(element))
            writer.writerow(("E", *element))


def triangle_shape(nodes, triangle):
    a, b, c = (nodes[tag] for tag in triangle)
    sides = (math.dist(a, b), math.dist(b, c), math.dist(c, a))
    if min(sides) <= 1e-12:
        return 0., 0.
    cross = ((b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]),
             (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]),
             (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]))
    double_area = math.sqrt(sum(component * component for component in cross))
    angles = [math.degrees(math.atan2(double_area,
              sum((nodes[triangle[(i + 1) % 3]][axis] - nodes[triangle[i]][axis]) *
                  (nodes[triangle[(i + 2) % 3]][axis] - nodes[triangle[i]][axis])
                  for axis in range(3)))) for i in range(3)]
    return min(angles), double_area / max(sides)


def repair_sliver_triangles(nodes, elements, normal, max_size, frames=()):
    """Flip only an internal triangle edge if both new triangles are sound.

    An edge adjacent to a quad, a hole, a frame boundary or another source
    area has fewer than two incident triangles here and cannot be changed.
    """
    elements = list(elements)
    limit = max_size * .02
    for _ in range(3):
        adjacency = {}
        for index, element in enumerate(elements):
            for a, b in zip(element, element[1:] + element[:1]):
                adjacency.setdefault(tuple(sorted((a, b))), []).append(index)
        changed = False
        for edge, incident in adjacency.items():
            if len(incident) != 2 or any(len(elements[i]) != 3 for i in incident):
                continue
            if any(all(math.dist(nodes[tag], start) + math.dist(nodes[tag], end) -
                       math.dist(start, end) <= 1e-7 for tag in edge)
                   for _, start, end in frames):
                continue
            first, second = (elements[i] for i in incident)
            before = [triangle_shape(nodes, triangle) for triangle in (first, second)]
            if all(angle >= 10 and height >= limit for angle, height in before):
                continue
            c, d = (next(tag for tag in triangle if tag not in edge) for triangle in (first, second))
            if c == d or tuple(sorted((c, d))) in adjacency:
                continue
            # The alternate diagonal must lie inside a convex, planar patch.
            trial = ((c, edge[0], d), (c, d, edge[1]))
            signed = []
            for triangle in trial:
                a, b, t = (nodes[tag] for tag in triangle)
                u = [b[j] - a[j] for j in range(3)]
                v = [t[j] - a[j] for j in range(3)]
                signed.append(sum(normal[j] * (u[(j + 1) % 3] * v[(j + 2) % 3] -
                                                u[(j + 2) % 3] * v[(j + 1) % 3]) for j in range(3)))
            old_signed = []
            for triangle in (first, second):
                a, b, t = (nodes[tag] for tag in triangle)
                u = [b[j] - a[j] for j in range(3)]
                v = [t[j] - a[j] for j in range(3)]
                old_signed.append(sum(normal[j] * (u[(j + 1) % 3] * v[(j + 2) % 3] -
                                                    u[(j + 2) % 3] * v[(j + 1) % 3]) for j in range(3)))
            if min(abs(value) for value in signed) <= 1e-16 or signed[0] * signed[1] <= 0 or \
                    signed[0] * old_signed[0] <= 0 or \
                    abs(sum(signed) - sum(old_signed)) > 1e-7 * abs(sum(old_signed)):
                continue
            after = [triangle_shape(nodes, triangle) for triangle in trial]
            if min(angle for angle, _ in after) < max(10., min(angle for angle, _ in before) + 1) or \
                    min(height for _, height in after) < max(limit, min(height for _, height in before)) or \
                    any(math.dist(nodes[a], nodes[b]) > max_size * 1.25 for a, b in
                        ((c, edge[0]), (c, edge[1]), (d, edge[0]), (d, edge[1]), (c, d))):
                continue
            elements[incident[0]], elements[incident[1]] = trial
            changed = True
            break
        if not changed:
            break
    return elements


def validate_sliver_triangles(nodes, elements, max_size):
    """Reject collapsed triangles before SAP2000 can silently discard them."""
    for element in elements:
        if len(element) != 3:
            continue
        angle, height = triangle_shape(nodes, element)
        if angle < 2 or (angle < 5 and height < max_size * .02):
            center = tuple(sum(nodes[tag][axis] for tag in element) / 3 for axis in range(3))
            raise ValueError("Near-collinear triangle at " + str(center) +
                             ": minimum angle=" + format(angle, '.3f') +
                             " degrees, minimum height=" + format(height, '.6g') +
                             "; try another mesh algorithm or element size")


def prepare_full_quad_curves(max_size, settings, protected):
    """Provide even 1D *area mesh* intervals for Gmsh's full-quad coarsening.

    Methods 2/3 halve each CAD curve's 1D mesh during 2D generation. A curve
    already represented by one interval (e.g. between two selected frame
    joints) cannot be halved. This only changes the temporary Gmsh area mesh:
    it never calls SAP's FrameObj API or replaces a frame object.
    """
    if not settings['recombine'] or int(settings['recombination']) not in (2, 3):
        return
    if protected:
        raise ValueError('Full-Quad cannot halve immutable selected SAP2000 boundary edges; '
                         'use Simple/Blossom or disable Preserve Selected Edges')
    structured = settings.get('structured') or {}
    if structured.get('mode') == 'manual':
        intervals = int(structured['nodes']) - 1
        if intervals < 2 or intervals % 2:
            raise ValueError('Full-Quad manual transfinite curves require an even number '
                             'of 1D intervals (at least two)')
        return
    for _, curve in gmsh.model.getEntities(1):
        length = gmsh.model.occ.getMass(1, curve)
        if not math.isfinite(length) or length <= 1e-12:
            raise ValueError('Full-Quad encountered a degenerate area mesh guide curve')
        intervals = max(2, 2 * math.ceil(length / (2 * max_size)))
        gmsh.model.mesh.setTransfiniteCurve(curve, intervals + 1)


def mesh_areas(areas, max_size, settings):
    """Mesh one or more SAP areas in a single conforming OCC/Gmsh model.

    `areas` is a list of (boundary_csv_path, result_csv_path). Each source
    surface retains its identity after OCC fragmentation for SAP assignments.
    """
    max_size = float(max_size)
    check_settings(settings, max_size)
    geometry = [(read_boundary(boundary), result) for boundary, result in areas]
    if any(data[8] for data, _ in geometry) and len(geometry) != 1:
        raise ValueError("Exact symmetry on several SAP2000 areas requires per-area mirroring by the SAP adapter")
    if settings.get("oGrid") and not any(data[6] for data, _ in geometry):
        raise ValueError("Circular O-grid requires at least one selected hole-center group")
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Terminal", 0)
        gmsh.model.add("SAP2000 selected areas")
        geo = gmsh.model.occ
        point_tags = {}

        def point_tag(coordinates):
            key = tuple(round(number, 8) for number in coordinates)
            if key not in point_tags:
                point_tags[key] = geo.addPoint(*coordinates, max_size)
            return point_tags[key]

        ring_areas, carve_areas, ring_faces, ring_distances, corner_notches = make_ogrid_rings(
            geometry, geo, point_tag, max_size, bool(settings.get("exactSymmetry"))) if settings.get("oGrid") else (
                [[] for _ in geometry], [[] for _ in geometry], set(), [], {})
        # OCC boolean operations used by O-grid construction can consume point
        # entities. Recreate boundary tags instead of reusing stale cached IDs.
        if settings.get("oGrid"):
            point_tags.clear()
        surfaces = []
        area_faces = []
        protected = []
        for index, ((corners, edges, _, _, _, protected_edges, holes, _, _), _) in enumerate(geometry):
            # A cut performed for a previous source area can consume shared OCC
            # point entities. Fragmentation later restores common topology, so
            # each source boundary must start with fresh point tags.
            point_tags.clear()
            n = len(corners)
            lines = []
            notch = corner_notches.get(index)
            for edge in range(1, n + 1):
                start, end = corners[edge], corners[edge % n + 1]
                stations = sorted(edges.get(edge, []), key=lambda item: item[0])
                path = [start] + [coords for t, coords in stations if 1e-9 < t < 1 - 1e-9] + [end]
                if notch:
                    corner, center, outer, arc, notch_edge = notch
                    if edge == notch_edge:
                        path = [start] + [point for point in path[1:-1]
                                          if math.dist(start, point) < math.dist(start, arc[0]) - 1e-8] + arc + [
                                          point for point in path[1:-1]
                                          if math.dist(start, point) > math.dist(start, arc[-1]) + 1e-8] + [end]
                    elif edge == corner:
                        path = [arc[-1]] + [point for point in path[1:-1]
                                            if math.dist(point, center) > outer + 1e-8] + [end]
                    elif corner is not None and edge == (corner - 2) % n + 1:
                        path = [start] + [point for point in path[1:-1]
                                          if math.dist(point, center) > outer + 1e-8] + [arc[0]]
                if settings.get("preserveSelectedEdges") and edge in protected_edges:
                    protected.append((start, end, path))
                for first, second in zip(path, path[1:]):
                    if first != second:
                        lines.append(geo.addLine(point_tag(first), point_tag(second)))
                if notch and notch[0] is not None and edge == (notch[0] - 2) % n + 1:
                    for first, second in zip(notch[3], notch[3][1:]):
                        lines.append(geo.addLine(point_tag(first), point_tag(second)))
            if len(lines) < n:
                raise ValueError("The selected area has degenerate boundary edges")
            face = geo.addPlaneSurface([geo.addWire(lines)])
            if carve_areas[index]:
                cuts = []
                for polygon in carve_areas[index]:
                    boundary = [geo.addLine(point_tag(polygon[i]), point_tag(polygon[(i + 1) % len(polygon)]))
                                for i in range(len(polygon))]
                    cuts.append(geo.addPlaneSurface([geo.addWire(boundary)]))
                remaining, _ = geo.cut([(2, face)], [(2, cut) for cut in cuts])
                faces = [tag for dim, tag in remaining if dim == 2]
                if not faces:
                    raise ValueError("O-grid transition removed an entire selected SAP2000 area")
            elif holes:
                normal = normal_of(corners)
                magnitude = math.sqrt(sum(component * component for component in normal))
                axis = [component / magnitude for component in normal]
                disks = [geo.addDisk(*center, radius, radius, zAxis=axis) for center, radius in holes]
                remaining, _ = geo.cut([(2, face)], [(2, disk) for disk in disks])
                faces = [tag for dim, tag in remaining if dim == 2]
                if not faces:
                    raise ValueError("Circular holes removed the entire selected SAP2000 area")
            else:
                faces = [face]
            area_faces.append(faces + ring_areas[index])
            surfaces.extend(area_faces[index])

        unique_frames, unique_points, opening_pairs = set(), set(), set()
        for (_, _, interior, frames, _, _, _, opening_frames, _), _ in geometry:
            unique_points.update(point_tag(point) for point in interior)
            for _, start, end in frames:
                tags = tuple(sorted((point_tag(start), point_tag(end))))
                if tags[0] != tags[1]:
                    unique_frames.add(tags)
            for _, start, end in opening_frames:
                opening_pairs.add(tuple(sorted((point_tag(start), point_tag(end)))))
        ordered_frames = sorted(unique_frames)
        frame_lines = [geo.addLine(*pair) for pair in ordered_frames]
        symmetry = geometry[0][0][8] if len(geometry) == 1 else {}
        axis_lines = []
        if symmetry:
            all_corners = list(geometry[0][0][0].values())
            extent = max(math.dist(a, b) for a, b in combinations(all_corners, 2)) * 2
            for center, direction in (symmetry[name] for name in ('SYMMETRY', 'SYMMETRY2') if name in symmetry):
                start = tuple(center[axis] - extent * direction[axis] for axis in range(3))
                end = tuple(center[axis] + extent * direction[axis] for axis in range(3))
                axis_lines.append(geo.addLine(point_tag(start), point_tag(end)))
        tools = [(2, surface) for surface in surfaces[1:]] + \
                 [(1, line) for line in frame_lines + axis_lines] + [(0, tag) for tag in sorted(unique_points)]
        if tools:
            _, ownership = geo.fragment([(2, surfaces[0])], tools)
            face_fragments = [[tag for dim, tag in item if dim == 2] for item in ownership[:len(surfaces)]]
            faces_by_area = []
            start = 0
            for faces in area_faces:
                faces_by_area.append(sorted({tag for fragments in face_fragments[start:start + len(faces)]
                                             for tag in fragments}))
                start += len(faces)
            frame_curves = sorted({tag for item in ownership[len(surfaces):len(surfaces) + len(frame_lines)]
                                    for dim, tag in item if dim == 1})
            opening_curves = {tag for index, pair in enumerate(ordered_frames) if pair in opening_pairs
                              for dim, tag in ownership[len(surfaces) + index] if dim == 1}
        else:
            faces_by_area = area_faces
            frame_curves = []
            opening_curves = set()
        geo.synchronize()
        excluded_opening_points = [set() for _ in geometry]
        excluded_opening_frames = [set() for _ in geometry]
        owners = {}
        for index, faces in enumerate(faces_by_area):
            if not faces:
                raise ValueError("Gmsh lost a selected source area during fragmentation")
            for face in faces:
                owners.setdefault(face, set()).add(index)
        for face, claimed_by in owners.items():
            if len(claimed_by) <= 1:
                continue
            center = geo.getCenterOfMass(2, face)
            matching = [index for index in claimed_by if inside_source_area(center, geometry[index][0][0])]
            if len(matching) != 1:
                raise ValueError("Overlapping SAP2000/CSiBridge areas cannot both own the same Gmsh surface")
            for index in claimed_by - set(matching):
                faces_by_area[index].remove(face)
        if any(not faces for faces in faces_by_area):
            raise ValueError("Gmsh lost a selected source area during fragmentation")
        structured_ring_faces = {tag for source, mapped in zip(surfaces, face_fragments if tools else [[face] for face in surfaces])
                                 if source in ring_faces for tag in mapped}
        if opening_curves:
            face_owner = {face: index for index, faces in enumerate(faces_by_area) for face in faces}
            boundaries = {face: {tag for dim, tag in gmsh.model.getBoundary(
                [(2, face)], combined=False, oriented=False) if dim == 1} for face in face_owner}
            adjacent = {}
            for face, curves in boundaries.items():
                for curve in curves:
                    adjacent.setdefault(curve, set()).add(face)

            def coplanar(first, second):
                first_corners = geometry[face_owner[first]][0][0]
                second_corners = geometry[face_owner[second]][0][0]
                normal = normal_of(first_corners)
                origin = next(iter(first_corners.values()))
                return all(abs(sum(normal[i] * (vertex[i] - origin[i]) for i in range(3))) < 1e-7
                           for vertex in second_corners.values())

            removed = set()
            visited = set()
            for face in face_owner:
                if face in visited:
                    continue
                component = {face}
                pending = [face]
                visited.add(face)
                while pending:
                    for curve in boundaries[pending.pop()] - opening_curves:
                        for neighbor in adjacent[curve] - visited:
                            if coplanar(face, neighbor):
                                visited.add(neighbor)
                                component.add(neighbor)
                                pending.append(neighbor)
                perimeter = {curve for item in component for curve in boundaries[item]
                             if not (adjacent[curve] - {item}) & component}
                if perimeter and perimeter.issubset(opening_curves):
                    removed.update(component)
            for face in removed:
                owner = face_owner[face]
                # Non-coplanar shell intersections can introduce an INTERIOR
                # constraint on the host slab inside a cell that is removed as
                # an opening. That joint is still required on the incident
                # wall, but cannot be required on the now-empty slab cell.
                for point in geometry[owner][0][2] + geometry[owner][0][4]:
                    if gmsh.model.isInside(2, face, point) and not any(math.dist(
                            point, gmsh.model.getClosestPoint(1, curve, point)[0]) <= 1e-7
                            for curve in opening_curves):
                        excluded_opening_points[owner].add(point)
                # The same removed floor cell can contain segments of an
                # automatic wall/slab intersection. Keep checking segments
                # outside the void and the actual opening perimeter.
                for frame in geometry[owner][0][3]:
                    _, start, end = frame
                    midpoint = tuple((a + b) / 2 for a, b in zip(start, end))
                    if any(math.dist(midpoint, gmsh.model.getClosestPoint(1, curve, midpoint)[0]) <= 1e-7
                           for curve in opening_curves):
                        continue
                    if all(gmsh.model.isInside(2, face, tuple(a + (b - a) * t
                            for a, b in zip(start, end))) for t in (.01, .25, .5, .75, .99)):
                        excluded_opening_frames[owner].add(frame)
            for faces in faces_by_area:
                faces[:] = [face for face in faces if face not in removed]
            if any(not faces for faces in faces_by_area):
                raise ValueError("The grouped frame opening would remove an entire selected SAP2000 area")
            if not removed:
                raise ValueError("The chosen opening group contains no closed frame or shell-edge cell")
            gmsh.model.removeEntities([(2, face) for face in sorted(removed)])

        selected_positions = {point for index, ((_, _, _, _, explicit, _, _, _, _), _) in enumerate(geometry)
                              for point in explicit if point not in excluded_opening_points[index]}
        selected_geom_tags = set()
        if selected_positions:
            for _, tag in gmsh.model.getEntities(0):
                value = gmsh.model.getValue(0, tag, [])
                if any(math.dist(point, value) <= 1e-7 for point in selected_positions):
                    selected_geom_tags.add(tag)

        gmsh.option.setNumber("Mesh.ElementOrder", 1)
        gmsh.option.setNumber("Mesh.Algorithm", int(settings["algorithm"]))
        # A user's explicit algorithm selection should not silently fall back
        # to MeshAdapt. It can still be enabled in the advanced Mesh.* options.
        gmsh.option.setNumber("Mesh.AlgorithmSwitchOnFailure", 0)
        gmsh.option.setNumber("Mesh.MeshSizeMax", max_size)
        # Simple and Blossom must receive the same completed (and smoothed)
        # triangulation. In particular, algorithm 8 skips its generation-time
        # smoothing if RecombineAll is enabled. Full-quad methods 2/3 need the
        # integrated path: they coarsen the 1D mesh before generating faces.
        two_phase_recombine = bool(settings["recombine"]) and int(settings["recombination"]) in (0, 1)
        gmsh.option.setNumber("Mesh.RecombineAll", int(bool(settings["recombine"]) and not two_phase_recombine))
        gmsh.option.setNumber("Mesh.RecombinationAlgorithm", int(settings["recombination"]))
        gmsh.option.setNumber("Mesh.Smoothing", int(settings["smoothing"]))
        # Expose additional Gmsh Mesh.* numeric options; keep the SAP shell contract fixed.
        reserved = {"Mesh.ElementOrder", "Mesh.Algorithm", "Mesh.RecombineAll",
                    "Mesh.RecombinationAlgorithm", "Mesh.Smoothing", "Mesh.MeshSizeMax"}
        for name, value in settings.get("options", {}).items():
            if not name.startswith("Mesh.") or name in reserved:
                raise ValueError("Unsupported or controlled Gmsh option: " + name)
            gmsh.option.setNumber(name, float(value))
        structured = settings.get("structured") or {}
        if structured.get("mode") == "automatic":
            gmsh.model.mesh.setTransfiniteAutomatic(cornerAngle=float(structured.get("cornerAngle", 2.35)),
                                                    recombine=bool(settings["recombine"]))
        elif structured.get("mode") == "manual":
            for _, tag in gmsh.model.getEntities(1):
                gmsh.model.mesh.setTransfiniteCurve(tag, int(structured["nodes"]),
                    structured["distribution"], float(structured["coefficient"]))
            for tag in owners:
                curves = gmsh.model.getBoundary([(2, tag)], combined=False, oriented=False)
                if len(curves) not in (3, 4):
                    raise ValueError("Manual transfinite requires 3- or 4-sided patches; partition the selected SAP2000/CSiBridge area first")
                gmsh.model.mesh.setTransfiniteSurface(tag, structured.get("arrangement", "Left"))
        transition_curves = set()
        transition_size = max_size
        for surface in sorted(structured_ring_faces):
            if surface not in {face for faces in faces_by_area for face in faces}:
                continue
            curves = gmsh.model.getBoundary([(2, surface)], oriented=False)
            center, radius, outer = min(ring_distances,
                                        key=lambda hole: math.dist(geo.getCenterOfMass(2, surface), hole[0]))
            threshold = (radius + outer) / 2
            outer_curves = []
            radial_pairs = []
            for _, curve in curves:
                endpoints = gmsh.model.getBoundary([(1, curve)], oriented=False)
                if len(endpoints) != 2:
                    raise ValueError("An O-grid sector has a non-linear or degenerate radial edge")
                distances = [(math.dist(gmsh.model.getValue(0, tag, []), center), tag)
                             for _, tag in endpoints]
                if min(distance for distance, _ in distances) >= threshold:
                    outer_curves.append(curve)
                elif max(distance for distance, _ in distances) > threshold:
                    radial_pairs.append((min(distances)[1], max(distances)[1]))
                else:
                    gmsh.model.mesh.setTransfiniteCurve(curve, 3)
            if len(radial_pairs) != 2 or len(outer_curves) not in (1, 2):
                raise ValueError("A selected constraint or overlapping O-grid split a radial patch; "
                                 "reduce the hole diameter, mesh size or conflicting constraints")
            for curve in outer_curves:
                transition_curves.add(curve)
                endpoints = gmsh.model.getBoundary([(1, curve)], oriented=False)
                length = math.dist(*(gmsh.model.getValue(0, tag, []) for _, tag in endpoints))
                transition_size = min(transition_size, max(length / (2 if len(outer_curves) == 1 else 1),
                                                            max_size * .08))
                gmsh.model.mesh.setTransfiniteCurve(curve, 3 if len(outer_curves) == 1 else 2)
            for inner, outer_tag in radial_pairs:
                gmsh.model.mesh.setTransfiniteCurve(next(curve for _, curve in curves if
                    {tag for _, tag in gmsh.model.getBoundary([(1, curve)], oriented=False)} ==
                    {inner, outer_tag}), 3)
            first, second = radial_pairs
            gmsh.model.mesh.setTransfiniteSurface(surface, cornerTags=[first[0], first[1],
                                                                         second[1], second[0]])
            gmsh.model.mesh.setRecombine(2, surface)
        if protected:
            if structured.get("mode") == "manual" and int(structured.get("nodes", 2)) > 2:
                raise ValueError("Manual transfinite curve subdivisions conflict with immutable selected SAP2000 edges")
            for _, tag in gmsh.model.getEntities(1):
                endpoints = gmsh.model.getBoundary([(1, tag)], oriented=False)
                if len(endpoints) != 2: continue
                coords = [tuple(gmsh.model.getValue(0, point_tag, [])) for _, point_tag in endpoints]
                if any(all(abs(math.dist(start, point) + math.dist(point, end) - math.dist(start, end)) <= 1e-7
                           for point in coords) for start, end, _ in protected):
                    gmsh.model.mesh.setTransfiniteCurve(tag, 2)
        backgrounds = []
        if transition_curves and transition_size < max_size:
            backgrounds.append(add_ogrid_transition_field(transition_curves, transition_size, max_size))
        field = settings.get("localField")
        if field:
            distance = gmsh.model.mesh.field.add("Distance")
            target = field["target"]
            if target in ("frames", "both") and frame_curves:
                gmsh.model.mesh.field.setNumbers(distance, "CurvesList", frame_curves)
            if target in ("joints", "both") and selected_geom_tags:
                gmsh.model.mesh.field.setNumbers(distance, "PointsList", sorted(selected_geom_tags))
            if target == "frames" and not frame_curves or target == "joints" and not selected_geom_tags or \
                    target == "both" and not (frame_curves or selected_geom_tags):
                raise ValueError("Selected frame/joint targets for the Gmsh size field were not found in the selected SAP2000/CSiBridge areas")
            threshold = gmsh.model.mesh.field.add("Threshold")
            for name, value in (("InField", distance), ("SizeMin", field["sizeNear"]),
                                ("SizeMax", field["sizeFar"]), ("DistMin", field["distanceNear"]),
                                ("DistMax", field["distanceFar"])):
                gmsh.model.mesh.field.setNumber(threshold, name, float(value))
            backgrounds.append(threshold)
        if settings.get("fieldGraph"):
            custom = add_field_graph(settings["fieldGraph"], frame_curves, sorted(selected_geom_tags))
            if custom is not None:
                backgrounds.append(custom)
        if len(backgrounds) > 1:
            combined = gmsh.model.mesh.field.add("Min")
            gmsh.model.mesh.field.setNumbers(combined, "FieldsList", backgrounds)
            backgrounds = [combined]
        if backgrounds:
            gmsh.model.mesh.field.setAsBackgroundMesh(backgrounds[0])
        prepare_full_quad_curves(max_size, settings, protected)
        gmsh.model.mesh.generate(2)
        if two_phase_recombine:
            gmsh.model.mesh.recombine()
        if settings["optimize"]:
            gmsh.model.mesh.optimize(settings.get("optimizeMethod", "Laplace2D"),
                                     force=0, niter=int(settings.get("optimizeIterations", 1)))
        node_tags, xyz, _ = gmsh.model.mesh.getNodes()
        nodes = {int(tag): tuple(xyz[3 * i:3 * i + 3]) for i, tag in enumerate(node_tags)}
        for area_index, (((corners, edges, interior, frames, selected, protected_edges, holes,
                           opening_frames, symmetry), output), faces) in enumerate(zip(geometry, faces_by_area)):
            elements = []
            for surface in faces:
                types, _, connectivity = gmsh.model.mesh.getElements(2, surface)
                for kind, block in zip(types, connectivity):
                    _, dimension, _, count, _, _ = gmsh.model.mesh.getElementProperties(kind)
                    if dimension != 2 or count not in (3, 4):
                        raise ValueError("Gmsh returned elements that SAP2000 cannot import: type " + str(kind))
                    elements.extend(tuple(int(tag) for tag in block[i:i + count])
                                    for i in range(0, len(block), count))
            if not elements:
                raise ValueError("Gmsh produced no 2D shell elements for a selected SAP2000/CSiBridge area")
            if symmetry:
                elements = reflect_elements_exactly(nodes, elements, corners, symmetry)
            if settings.get("oGrid"):
                elements = repair_sliver_triangles(nodes, elements, normal_of(corners), max_size, frames)
            validate_sliver_triangles(nodes, elements, max_size)
            excluded = excluded_opening_points[area_index]
            validate_constraints(nodes, elements,
                                 [point for point in interior + selected if point not in excluded],
                                 [frame for frame in frames if frame not in excluded_opening_frames[area_index]])
            used = {tag for element in elements for tag in element}
            if settings.get("preserveSelectedEdges"):
                for number in protected_edges:
                    start, end = corners[number], corners[number % len(corners) + 1]
                    prescribed = [start, end] + [point for _, point in edges.get(number, [])]
                    for tag in used:
                        point = nodes[tag]
                        if abs(math.dist(start, point) + math.dist(point, end) - math.dist(start, end)) <= 1e-7 and \
                                not any(math.dist(point, original) <= 1e-7 for original in prescribed):
                            raise ValueError("Gmsh inserted an extra joint on a protected selected SAP2000 edge")
            write_mesh(output, nodes, elements, normal_of(corners))
    finally:
        gmsh.finalize()


def main(boundary, output, max_size, settings_path):
    with open(settings_path, encoding="utf-8-sig") as stream:
        settings = json.load(stream)
    if str(boundary).endswith(".json"):
        with open(boundary, encoding="utf-8-sig") as stream:
            manifest = json.load(stream)
        outputs = [(str(Path(boundary).parent / item["boundary"]),
                    str(Path(output) / item["mesh"])) for item in manifest["areas"]]
        if len(outputs) == 0 or len(outputs) != len({mesh for _, mesh in outputs}):
            raise ValueError("Each selected SAP2000/CSiBridge area needs a distinct output mesh")
        if not Path(output).is_dir():
            raise FileNotFoundError("Batch mesh output directory is missing: " + str(output))
        mesh_areas(outputs, max_size, settings)
    else:
        mesh_areas([(boundary, output)], max_size, settings)


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("Usage: sap_gmsh_mesher.py boundary.csv mesh.csv max_size settings.json")
    try:
        main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4])
    except ValueError as error:
        if not str(error).startswith("Near-collinear triangle at "):
            raise
        # A controlled, single-line diagnostic survives the frozen backend
        # without exposing its Python traceback in the SAP2000 UI.
        print("SAP_GMSH_NEAR_COLLINEAR:" + str(error), file=sys.stderr)
        raise SystemExit(2)
