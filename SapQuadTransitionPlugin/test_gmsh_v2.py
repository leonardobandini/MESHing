# SPDX-License-Identifier: GPL-2.0-or-later
"""Small end-to-end fixtures for the SAP CSV -> Gmsh -> SAP CSV adapter."""
import csv
import json
import math
import os
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from sap_gmsh_mesher import (check_settings, main, mesh_areas, read_boundary,
                             repair_sliver_triangles, triangle_shape, validate_constraints,
                             validate_sliver_triangles)


class GmshV2Tests(unittest.TestCase):
    def test_meshadapt_allows_zero_refinement_steps(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'patch.csv')
            output = os.path.join(directory, 'mesh.csv')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for index, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', index, str(index), 0, *point))
            mesh_areas([(boundary, output)], .25,
                       dict(algorithm=1, recombine=False, recombination=0, smoothing=0,
                            optimize=False, structured={'mode': 'unstructured'},
                            options={'Mesh.RefineSteps': 0}))
            with open(output, newline='') as stream:
                self.assertTrue(any(row[0] == 'E' for row in csv.reader(stream)))

    def test_simple_and_blossom_recombine_completed_triangles_without_hidden_optimization(self):
        from sap_gmsh_mesher import gmsh
        for method in (0, 1):
            with self.subTest(recombination=method), tempfile.TemporaryDirectory() as directory:
                boundary = os.path.join(directory, 'patch.csv')
                mesh = os.path.join(directory, 'result.csv')
                options = os.path.join(directory, 'settings.json')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', 'A1', 'Shell'))
                    for index, point in enumerate(((0, 0, 0), (2, 0, 0), (2.1, 1, 0), (0, 1, 0)), 1):
                        writer.writerow(('CORNER', index, str(index), 0, *point))
                settings = dict(algorithm=8, recombine=True, recombination=method,
                                optimize=False, smoothing=1, structured={'mode': 'unstructured'},
                                options={'Mesh.RecombineOptimizeTopology': 0,
                                         'Mesh.RecombineNodeRepositioning': 0})
                with open(options, 'w') as stream:
                    json.dump(settings, stream)
                generate = gmsh.model.mesh.generate
                recombine = gmsh.model.mesh.recombine
                calls = []
                def record_generate(dimension):
                    calls.append(('generate', dimension, gmsh.option.getNumber('Mesh.RecombineAll')))
                    return generate(dimension)
                def record_recombine():
                    calls.append(('recombine', gmsh.option.getNumber('Mesh.RecombinationAlgorithm')))
                    return recombine()
                with patch.object(gmsh.model.mesh, 'generate', side_effect=record_generate), \
                     patch.object(gmsh.model.mesh, 'recombine', side_effect=record_recombine), \
                     patch.object(gmsh.model.mesh, 'optimize', side_effect=AssertionError('Optimize Mesh is off')):
                    main(boundary, mesh, .3, options)
                self.assertEqual(calls, [('generate', 2, 0), ('recombine', method)])
                with open(mesh, newline='') as stream:
                    elements = [row for row in csv.reader(stream) if row and row[0] == 'E']
                self.assertGreater(len(elements), 1)
                self.assertTrue(any(len(row) == 5 for row in elements))

    def test_simple_and_blossom_preserve_triangular_node_coordinates_with_smoothing(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'patch.csv')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for index, point in enumerate(((0, 0, 0), (2, 0, 0), (2.1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', index, str(index), 0, *point))
            for smoothing in (0, 1):
                baseline = None
                for method in (None, 0, 1):
                    with self.subTest(smoothing=smoothing, recombination=method):
                        output = os.path.join(directory, f'mesh-{smoothing}-{method}.csv')
                        settings = dict(algorithm=8, recombine=method is not None,
                                        recombination=method or 0, optimize=False,
                                        smoothing=smoothing, structured={'mode': 'unstructured'},
                                        options={'Mesh.RecombineOptimizeTopology': 0,
                                                 'Mesh.RecombineNodeRepositioning': 0})
                        mesh_areas([(boundary, output)], .3, settings)
                        with open(output, newline='') as stream:
                            rows = list(csv.reader(stream))
                        nodes = {int(row[1]): tuple(map(float, row[2:5]))
                                 for row in rows if row[0] == 'N'}
                        if baseline is None:
                            baseline = nodes
                        else:
                            self.assertEqual(nodes, baseline)
                            self.assertTrue(any(row[0] == 'E' and len(row) == 5 for row in rows))

    def test_full_quad_methods_keep_the_integrated_generation_path(self):
        from sap_gmsh_mesher import gmsh
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'patch.csv')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for index, point in enumerate(((0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', index, str(index), 0, *point))
            for method in (2, 3):
                with self.subTest(recombination=method):
                    settings = dict(algorithm=8, recombine=True, recombination=method,
                                    optimize=False, smoothing=0, structured={'mode': 'unstructured'},
                                    options={'Mesh.RecombineOptimizeTopology': 0,
                                             'Mesh.RecombineNodeRepositioning': 0})
                    generate = gmsh.model.mesh.generate
                    observed = []
                    def record_generate(dimension):
                        observed.append(gmsh.option.getNumber('Mesh.RecombineAll'))
                        return generate(dimension)
                    output = os.path.join(directory, f'full-{method}.csv')
                    with patch.object(gmsh.model.mesh, 'generate', side_effect=record_generate), \
                         patch.object(gmsh.model.mesh, 'recombine', side_effect=AssertionError('Full-quad uses integrated generation')):
                        mesh_areas([(boundary, output)], .3, settings)
                    self.assertEqual(observed, [1])
                    with open(output, newline='') as stream:
                        self.assertTrue(any(row[0] == 'E' and len(row) == 5 for row in csv.reader(stream)))

    def test_full_quad_on_real_silo_frames_without_editing_sap_frames(self):
        fixtures = os.path.join(os.path.dirname(os.path.dirname(__file__)),
                                'examples', 'bug17', 'geometry')
        for name, area_ids, size in (('circular', ('1',), .4),
                                     ('multiple', ('6', '7', '8', '9', '10', '11'), .7)):
            with self.subTest(model=name), tempfile.TemporaryDirectory() as directory:
                with open(os.path.join(fixtures, name + '-options.json'), encoding='utf-8') as stream:
                    settings = json.load(stream)
                pairs = [(os.path.join(fixtures, f'{name}-area-{area}-boundary.csv'),
                          os.path.join(directory, f'area-{area}.csv')) for area in area_ids]
                mesh_areas(pairs, size, settings)
                for _, output in pairs:
                    with open(output, newline='') as stream:
                        elements = [row for row in csv.reader(stream) if row and row[0] == 'E']
                    self.assertGreater(len(elements), 0)
                    self.assertTrue(all(len(element) == 5 for element in elements))

    def test_full_quad_ogrid_keeps_the_circular_opening(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'plate.csv')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for index, point in enumerate(((0, 0, 0), (1, 0, 0),
                                                (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', index, str(index), 0, *point))
                writer.writerow(('HOLE', 1, '', .2, .5, .5, 0))
            for method in (2, 3):
                with self.subTest(recombination=method):
                    output = os.path.join(directory, f'full-quad-{method}.csv')
                    mesh_areas([(boundary, output)], .15,
                               dict(algorithm=8, recombine=True, recombination=method,
                                    optimize=False, smoothing=1, oGrid=True,
                                    structured={'mode': 'unstructured'}, options={}))
                    with open(output, newline='') as stream:
                        rows = list(csv.reader(stream))
                    nodes = {int(row[1]): tuple(map(float, row[2:5])) for row in rows if row[0] == 'N'}
                    elements = [row for row in rows if row[0] == 'E']
                    self.assertTrue(elements)
                    self.assertTrue(all(len(row) == 5 for row in elements))
                    self.assertTrue(all(math.dist(nodes[int(row[1])][:2], (.5, .5)) >= .09
                                        for row in elements))

    def test_full_quad_respects_immutable_edges_and_manual_1d_divisions(self):
        from sap_gmsh_mesher import prepare_full_quad_curves
        settings = dict(recombine=True, recombination=2,
                        structured={'mode': 'manual', 'nodes': 4})
        with self.assertRaisesRegex(ValueError, 'even number'):
            prepare_full_quad_curves(.3, settings, [])
        with self.assertRaisesRegex(ValueError, 'immutable'):
            prepare_full_quad_curves(.3, settings, [(None, None, None)])

    def test_two_phase_blossom_preserves_circular_ogrid(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'hole.csv')
            mesh = os.path.join(directory, 'mesh.csv')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for index, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', index, str(index), 0, *point))
                writer.writerow(('HOLE', 1, '', .2, .5, .5, 0))
            settings = dict(algorithm=8, recombine=True, recombination=1,
                            optimize=False, smoothing=1, oGrid=True,
                            structured={'mode': 'unstructured'}, options={})
            mesh_areas([(boundary, mesh)], .15, settings)
            with open(mesh, newline='') as stream:
                rows = list(csv.reader(stream))
            self.assertGreater(sum(row[0] == 'E' and len(row) == 5 for row in rows), 20)
            self.assertFalse(any(row[0] == 'N' and math.dist(tuple(map(float, row[2:4])),
                (.5, .5)) < .09 for row in rows))

    def test_automatic_regular_grid_produces_aligned_quads_on_rectangle(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'rectangle.csv')
            output = os.path.join(directory, 'mesh.csv')
            settings_path = os.path.join(directory, 'options.json')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for i, vertex in enumerate(((0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', i, str(i), 0, *vertex))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            optimize=True, optimizeMethod='Laplace2D', optimizeIterations=1,
                            smoothing=1, structured={'mode': 'automatic', 'cornerAngle': 2.356194490192345},
                            options={})
            with open(settings_path, 'w') as stream:
                json.dump(settings, stream)
            main(boundary, output, .4, settings_path)
            with open(output, newline='') as stream:
                rows = list(csv.reader(stream))
            nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
            elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
            self.assertGreater(len(elements), 8)
            self.assertTrue(all(len(element) == 4 for element in elements))
            self.assertTrue(all(abs(nodes[a][0] - nodes[b][0]) < 1e-8 or
                                abs(nodes[a][1] - nodes[b][1]) < 1e-8
                                for element in elements for a, b in
                                zip(element, element[1:] + element[:1])))

    def test_local_flip_replaces_a_collinear_triangle_without_moving_joints(self):
        nodes = {1: (0., 0., 0.), 2: (1., 0., 0.),
                 3: (.5, .001, 0.), 4: (.5, -.5, 0.)}
        original = [(3, 1, 2), (4, 2, 1)]
        repaired = repair_sliver_triangles(nodes, original, (0, 0, 1), 1.)
        self.assertNotEqual(repaired, original)
        self.assertGreater(min(triangle_shape(nodes, tri)[0] for tri in repaired), 10)
        self.assertEqual(repair_sliver_triangles(nodes, [original[0]], (0, 0, 1), 1.),
                         [original[0]])
        self.assertEqual(repair_sliver_triangles(nodes, original, (0, 0, 1), 1.,
                         [('frame', nodes[1], nodes[2])]), original)
        with self.assertRaisesRegex(ValueError, 'Near-collinear triangle'):
            validate_sliver_triangles(nodes, [original[0]], 1.)
        validate_sliver_triangles(nodes, repaired, 1.)

    def test_near_collinear_triangle_is_rejected_without_ogrid(self):
        # BAMG may return a long, almost collinear triangle on a curved
        # constraint even without an O-grid. SAP's AddByPoint returns success
        # but silently discards this shell; reject it before any SAP import.
        nodes = {1: (8.01212237666583, 6.2614961763553643, 0.),
                 2: (1.8697880679838441, 8.301170920128552, 0.),
                 3: (3.6168733595300884, 7.7075439392816385, 0.)}
        with self.assertRaisesRegex(ValueError, 'Near-collinear triangle'):
            validate_sliver_triangles(nodes, [(1, 2, 3)], .3)

    def test_reported_bamg_failure_is_one_line_not_a_python_traceback(self):
        geometry = os.path.join(os.path.dirname(os.path.dirname(__file__)),
                                'examples', 'bug17', 'geometry')
        with tempfile.TemporaryDirectory() as directory:
            output = os.path.join(directory, 'result.csv')
            result = subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__),
                'sap_gmsh_mesher.py'), os.path.join(geometry, 'circular-bamg-boundary.csv'),
                output, '0.3', os.path.join(geometry, 'circular-bamg-options.json')],
                capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 2)
            self.assertIn('SAP_GMSH_NEAR_COLLINEAR:', result.stderr)
            self.assertNotIn('Traceback', result.stderr)
            self.assertFalse(os.path.exists(output))

    def test_nTEST026_ogrid_transition_avoids_near_collinear_triangles(self):
        # Same three source planes and twelve 20 mm hole centres as nTEST026.
        with tempfile.TemporaryDirectory() as directory:
            polygons = (
                ((0, 0, 0), (.3, 0, 0), (.3, .3, 0), (0, .3, 0)),
                ((0, .3, 0), (.3, .3, 0), (.3, .3, .3), (0, .3, .3)),
                ((.15, .3, 0), (.15, 0, 0), (.15, .15, .3), (.15, .3, .3)),
            )
            areas = []
            for index, polygon in enumerate(polygons):
                boundary = os.path.join(directory, f'area-{index}.csv')
                output = os.path.join(directory, f'mesh-{index}.csv')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', index + 1, '15mm'))
                    for edge, point in enumerate(polygon, 1):
                        writer.writerow(('CORNER', edge, edge, 0, *point))
                    if index < 2:
                        for x in (.073, .225):
                            for station in (.07, .15, .23):
                                center = (x, station, 0) if index == 0 else (x, .3, station)
                                writer.writerow(('HOLE', 1, '', .02, *center))
                areas.append((boundary, output))
            settings = dict(algorithm=8, recombine=True, recombination=0, optimize=True,
                            optimizeMethod='Laplace2D', optimizeIterations=1, smoothing=1,
                            oGrid=True, previewMode=True,
                            options={'Mesh.MeshSizeFactor': 1, 'Mesh.MeshSizeFromPoints': 0,
                                     'Mesh.MeshSizeExtendFromBoundary': 0,
                                     'Mesh.MeshSizeFromCurvature': 0,
                                     'Mesh.MeshSizeFromParametricPoints': 0,
                                     'Mesh.SubdivisionAlgorithm': 0, 'Mesh.MeshSizeMin': 0,
                                     'Mesh.AlgorithmSwitchOnFailure': 1,
                                     'Mesh.RecombineMinimumQuality': .01,
                                     'Mesh.RecombineOptimizeTopology': 5,
                                     'Mesh.RecombineNodeRepositioning': 1})
            # Same mesher without graded transition/local repair: a controlled
            # comparison on the reconstructed 026 input, without SAP2000.
            from sap_gmsh_mesher import gmsh
            def uniform_field(curves, near_size, max_size):
                tag = gmsh.model.mesh.field.add('MathEval')
                gmsh.model.mesh.field.setString(tag, 'F', str(max_size))
                return tag
            with patch('sap_gmsh_mesher.add_ogrid_transition_field', side_effect=uniform_field), \
                 patch('sap_gmsh_mesher.repair_sliver_triangles', side_effect=lambda nodes, elements, *args: elements), \
                 patch('sap_gmsh_mesher.validate_sliver_triangles'):
                mesh_areas(areas, .05, settings)
            def sliver_count():
                result = 0
                for _, output in areas:
                    with open(output, newline='') as stream:
                        rows = list(csv.reader(stream))
                    points = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                    for row in rows:
                        if row[0] == 'E' and len(row) == 4:
                            angle, height = triangle_shape(points, tuple(map(int, row[1:])))
                            result += angle < 5 and height < .001
                return result
            baseline_slivers = sliver_count()
            mesh_areas(areas, .05, settings)
            for index, (_, output) in enumerate(areas):
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                triangles = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E' and len(row) == 4]
                self.assertTrue(triangles)
                if index < 2:
                    for x in (.073, .225):
                        for station in (.07, .15, .23):
                            center = (x, station, 0) if index == 0 else (x, .3, station)
                            self.assertTrue(all(math.dist(point, center) >= .0092 for point in nodes.values()))
                slivers = [(triangle, triangle_shape(nodes, triangle)) for triangle in triangles
                           if triangle_shape(nodes, triangle)[0] < 5 and
                           triangle_shape(nodes, triangle)[1] < .001]
                self.assertFalse(slivers, slivers[:3])
            self.assertGreater(baseline_slivers, 0)

    def test_preview_settings_reach_gmsh_with_optional_optimizer(self):
        fixed = {"Mesh.MeshSizeFactor": 1, "Mesh.MeshSizeFromPoints": 0,
                 "Mesh.MeshSizeExtendFromBoundary": 0, "Mesh.MeshSizeFromCurvature": 0,
                 "Mesh.MeshSizeFromParametricPoints": 0, "Mesh.SubdivisionAlgorithm": 0,
                 "Mesh.MeshSizeMin": 0, "Mesh.AlgorithmSwitchOnFailure": 1,
                 "Mesh.RecombineMinimumQuality": .01, "Mesh.RecombineOptimizeTopology": 5,
                 "Mesh.RecombineNodeRepositioning": 1}
        settings = dict(previewMode=True, algorithm=8, recombine=True, recombination=0,
                        optimize=True, optimizeMethod="Laplace2D", optimizeIterations=1,
                        smoothing=1, options=fixed)
        check_settings(settings, .2)
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "preview.csv")
            output = os.path.join(directory, "mesh.csv")
            config = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for index, point in enumerate(((0, 0, 0), (.4, 0, 0), (.4, .4, 0), (0, .4, 0)), 1):
                    writer.writerow(("CORNER", index, str(index), 0, *point))
            for optimize in (False, True):
                settings["optimize"] = optimize
                with open(config, "w") as stream:
                    json.dump(settings, stream)
                from sap_gmsh_mesher import gmsh
                real_optimize = gmsh.model.mesh.optimize
                with patch.object(gmsh.model.mesh, "optimize", wraps=real_optimize) as called:
                    main(boundary, output, .2, config)
                self.assertEqual(called.call_count, int(optimize))
                if optimize:
                    self.assertEqual(called.call_args.kwargs, {"force": 0, "niter": 1})
                    self.assertEqual(called.call_args.args, ("Laplace2D",))
                with open(output, newline="") as stream:
                    self.assertTrue(any(row[0] == "E" for row in csv.reader(stream)))
        settings["options"]["Mesh.MeshSizeMin"] = .2
        with self.assertRaisesRegex(ValueError, "Min Mesh Size"):
            check_settings(settings, .2)
        settings["options"]["Mesh.MeshSizeMin"] = 0
        settings["options"]["Mesh.MeshSizeFromPoints"] = 1
        with self.assertRaisesRegex(ValueError, "fixed mesh options"):
            check_settings(settings, .2)
        settings["options"]["Mesh.MeshSizeFromPoints"] = 0
        settings["algorithm"] = 11
        with self.assertRaisesRegex(ValueError, "disable recombination"):
            check_settings(settings, .2)
        settings["algorithm"] = 9
        settings["recombine"] = False
        with self.assertRaisesRegex(ValueError, "Unsupported Gmsh 2D algorithm"):
            check_settings(settings, .2)

    def test_triangle_quad_and_embedded_node(self):
        corners = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, corner in enumerate(corners, 1):
                    writer.writerow(("CORNER", i, str(i), 0, *corner))
                writer.writerow(("INTERIOR", 0, "center", 0, 0.5, 0.5, 0))
            for recombine in (False, True):
                with open(settings, "w") as stream:
                    json.dump(dict(algorithm=6, recombine=recombine, recombination=1,
                                   optimize=False, smoothing=1, options={}), stream)
                main(boundary, output, 0.25, settings)
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [list(map(int, row[1:])) for row in rows if row[0] == "E"]
                self.assertTrue(elements)
                self.assertTrue(all(len(element) in (3, 4) for element in elements))
                self.assertTrue(any(node == (0.5, 0.5, 0) for node in nodes.values()))
                self.assertTrue(any((0.5, 0.5, 0) in [nodes[tag] for tag in element] for element in elements))
                if recombine:
                    self.assertTrue(any(len(element) == 4 for element in elements))
            initial_count = len(elements)
            with open(boundary, "a", newline="") as stream:
                csv.writer(stream).writerow(("EXPLICIT_POINT", 0, "center", 0, .5, .5, 0))
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=6, recombine=True, recombination=1,
                               optimize=False, smoothing=1,
                               options={"Mesh.MeshSizeFromPoints": 0,
                                        "Mesh.MeshSizeExtendFromBoundary": 0},
                               localField={"target": "joints", "sizeNear": .04, "sizeFar": .25,
                                           "distanceNear": .01, "distanceFar": .2}), stream)
            main(boundary, output, .25, settings)
            with open(output, newline="") as stream:
                refined = sum(row[0] == "E" for row in csv.reader(stream))
            self.assertGreater(refined, initial_count, "Selected SAP joints should drive the Gmsh Distance field")

    def test_tilted_triangle_and_invalid_hole(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings = os.path.join(directory, "settings.json")
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=5, recombine=False, recombination=0,
                               optimize=True, smoothing=0, options={"Mesh.MeshSizeMin": 0.05}), stream)
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, coords in enumerate(((0, 0, 0), (1, 0, 1), (0, 1, 1)), 1):
                    writer.writerow(("CORNER", i, str(i), 0, *coords))
            main(boundary, output, 0.3, settings)
            with open(output, newline="") as stream:
                rows = list(csv.reader(stream))
            nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
            self.assertTrue(all(abs(x + y - z) < 1e-8 for x, y, z in nodes.values()))
            self.assertTrue(any(row[0] == "E" and len(row) == 4 for row in rows))
            with open(boundary, "a", newline="") as stream:
                csv.writer(stream).writerow(("HOLE", 1, "", -0.2, 0.2, 0.2, 0.4))
            with self.assertRaisesRegex(ValueError, "Invalid SAP2000 circular hole"):
                read_boundary(boundary)

    def test_circular_holes_remove_material_and_keep_shared_seams(self):
        with tempfile.TemporaryDirectory() as directory:
            settings = os.path.join(directory, "settings.json")
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=8, recombine=True, recombination=0,
                               optimize=False, smoothing=1, options={}), stream)
            for count, holes in ((1, [(.5, .5, .12), (.25, .25, .08)]),
                                 (2, [(1., .5, .18)])):
                items = []
                for index in range(count):
                    left = index if count == 2 else 0
                    boundary = os.path.join(directory, f"hole-{count}-{index}.csv")
                    output = os.path.join(directory, f"result-{count}-{index}.csv")
                    with open(boundary, "w", newline="") as stream:
                        writer = csv.writer(stream)
                        writer.writerow(("AREA", index, "Shell"))
                        for edge, coords in enumerate(((left, 0, 0), (left + 1, 0, 0),
                                                       (left + 1, 1, 0), (left, 1, 0)), 1):
                            writer.writerow(("CORNER", edge, str(edge), 0, *coords))
                        for hole_id, (x, y, radius) in enumerate(holes, 1):
                            if left - radius <= x <= left + 1 + radius:
                                writer.writerow(("HOLE", hole_id, "", 2 * radius, x, y, 0))
                    items.append((boundary, output))
                from sap_gmsh_mesher import mesh_areas
                with open(settings) as stream:
                    mesh_areas(items, .08, json.load(stream))
                seams = []
                for boundary, output in items:
                    with open(output, newline="") as stream:
                        rows = list(csv.reader(stream))
                    nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                    elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                    self.assertTrue(elements)
                    for x, y, radius in holes:
                        self.assertTrue(all(math.dist(nodes[tag][:2], (x, y)) >= radius - 1e-6
                                            for element in elements for tag in element))
                    if count == 2:
                        seams.append({tuple(sorted((a, b))) for element in elements
                                      for a, b in zip(element, element[1:] + element[:1])
                                      if abs(nodes[a][0] - 1) < 1e-8 and abs(nodes[b][0] - 1) < 1e-8})
                if count == 2:
                    self.assertTrue(seams[0])
                    self.assertEqual(seams[0], seams[1])
                    with open(settings, "w") as stream:
                        json.dump(dict(algorithm=8, recombine=True, recombination=0,
                                       optimize=False, smoothing=1, oGrid=True, options={}), stream)
                    with open(settings) as stream:
                        mesh_areas(items, .08, json.load(stream))
                    for _, output in items:
                        with open(output, newline="") as stream:
                            rows = list(csv.reader(stream))
                        nodes = {int(row[1]): tuple(map(float, row[2:]))
                                 for row in rows if row[0] == "N"}
                        ring = [row for row in rows if row[0] == "E" and all(
                            .18 - 1e-7 <= math.dist(nodes[int(tag)][:2], (1., .5)) <= .3 + 1e-7
                            for tag in row[1:])]
                        self.assertGreaterEqual(len(ring), 16)
                        self.assertTrue(all(len(row) == 5 for row in ring))

    def test_opt_in_circular_ogrid_has_radial_quadrilateral_layers(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'area.csv')
            output = os.path.join(directory, 'mesh.csv')
            config = os.path.join(directory, 'settings.json')
            with open(boundary, 'w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(('AREA', 'A1', 'Shell'))
                for i, corner in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(('CORNER', i, str(i), 0, *corner))
                writer.writerow(('HOLE', 1, '', .24, .5, .5, 0))
            settings = dict(algorithm=8, recombine=True, recombination=0, optimize=False,
                            smoothing=1, oGrid=True, options={})
            with open(config, 'w') as stream:
                json.dump(settings, stream)
            main(boundary, output, .08, config)
            with open(output, newline='') as stream:
                rows = list(csv.reader(stream))
            nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
            elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
            self.assertTrue(elements)
            ring = [element for element in elements if all(
                .12 * math.cos(math.pi / 24) - 1e-7 <= math.dist(nodes[tag][:2], (.5, .5)) <= .216 + 1e-7
                for tag in element)]
            self.assertGreaterEqual(len(ring), 32)
            self.assertTrue(all(len(element) == 4 for element in ring))
            self.assertTrue(any(len(element) == 4 and any(
                abs(math.dist(nodes[tag][:2], (.5, .5)) - .12) < 1e-7 for tag in element)
                for element in ring))
            hole_boundary = {tag for element in elements for tag in element
                             if math.dist(nodes[tag][:2], (.5, .5)) < .15}
            self.assertGreaterEqual(len(hole_boundary), 16)
            self.assertTrue(all(abs(math.dist(nodes[tag][:2], (.5, .5)) - .12) < 1e-7
                                for tag in hole_boundary))

    def test_ogrid_ignores_non_coplanar_projected_area(self):
        from sap_gmsh_mesher import mesh_areas
        with tempfile.TemporaryDirectory() as directory:
            areas = []
            polygons = (
                ((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)),
                ((.75, 0, 0), (.75, 1, 0), (.75, 1, 1), (.75, 0, 1)),
            )
            for index, polygon in enumerate(polygons):
                boundary = os.path.join(directory, f'area-{index}.csv')
                output = os.path.join(directory, f'mesh-{index}.csv')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', index, 'Shell'))
                    for edge, corner in enumerate(polygon, 1):
                        writer.writerow(('CORNER', edge, str(edge), 0, *corner))
                    if index == 0:
                        writer.writerow(('HOLE', 1, '', .2, .25, .5, 0))
                areas.append((boundary, output))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            smoothing=1, optimize=False, oGrid=True, options={})
            mesh_areas(areas, .08, settings)
            for _, output in areas:
                with open(output, newline='') as stream:
                    elements = [row for row in csv.reader(stream) if row[0] == 'E']
                self.assertTrue(elements)

    def test_four_shell_junction_ogrid_keeps_quadrants_and_seams(self):
        from sap_gmsh_mesher import mesh_areas
        with tempfile.TemporaryDirectory() as directory:
            areas = []
            for index, (x, y) in enumerate(((-1, -1), (0, -1), (0, 0), (-1, 0))):
                boundary = os.path.join(directory, f'area-{index}.csv')
                output = os.path.join(directory, f'mesh-{index}.csv')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', index, 'Shell'))
                    for edge, corner in enumerate(((x, y, 0), (x+1, y, 0),
                                                   (x+1, y+1, 0), (x, y+1, 0)), 1):
                        writer.writerow(('CORNER', edge, str(edge), 0, *corner))
                    writer.writerow(('HOLE', 1, '', .24, 0, 0, 0))
                areas.append((boundary, output))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            smoothing=1, optimize=False, oGrid=True, options={})
            mesh_areas(areas, .08, settings)
            joined = []
            for _, output in areas:
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
                self.assertTrue(elements)
                self.assertTrue(all(math.dist(nodes[tag][:2], (0, 0)) >= .12 * math.cos(math.pi / 24) - 1e-7
                                    for element in elements for tag in element),
                                min(math.dist(nodes[tag][:2], (0, 0)) for element in elements for tag in element))
                ring = [element for element in elements if all(
                    .12 * math.cos(math.pi / 24) - 1e-7 <= math.dist(nodes[tag][:2], (0, 0)) <= .216 + 1e-7
                    for tag in element)]
                self.assertGreaterEqual(len(ring), 8)
                self.assertTrue(all(len(element) == 4 for element in ring))
                joined.append((nodes, elements))
            for axis in (0, 1):
                negative, positive = set(), set()
                for nodes, elements in joined:
                    for element in elements:
                        for first, second in zip(element, element[1:] + element[:1]):
                            a, b = nodes[first], nodes[second]
                            if abs(a[axis]) < 1e-8 and abs(b[axis]) < 1e-8:
                                (negative if sum(nodes[tag][axis] for tag in element) < 0
                                 else positive).add(tuple(sorted((first, second))))
                self.assertTrue(negative)
                self.assertEqual(negative, positive)

    def test_exact_single_and_double_symmetry_with_optional_ogrid(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'symmetric.csv')
            output = os.path.join(directory, 'symmetric-mesh.csv')
            config = os.path.join(directory, 'settings.json')
            for double_axis, ogrid in ((False, False), (True, False), (True, True)):
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', 'A1', 'Shell'))
                    for edge, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                        writer.writerow(('CORNER', edge, str(edge), 0, *point))
                    writer.writerow(('SYMMETRY', .5, .5, 0, 0, 1, 0))
                    if double_axis:
                        writer.writerow(('SYMMETRY2', .5, .5, 0, 1, 0, 0))
                    if ogrid:
                        writer.writerow(('HOLE', 1, '', .24, .5, .5, 0))
                with open(config, 'w') as stream:
                    json.dump(dict(algorithm=8, recombine=True, recombination=0,
                                   optimize=False, smoothing=1, exactSymmetry=True,
                                   oGrid=ogrid, options={}), stream)
                main(boundary, output, .08, config)
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
                self.assertTrue(elements)
                signatures = {tuple(sorted(tuple(round(value, 7) for value in nodes[tag])
                                           for tag in element)) for element in elements}
                for axis in ((0, 1) if double_axis else (0,)):
                    for element in elements:
                        reflected = tuple(sorted(tuple(round(1 - nodes[tag][i] if i == axis else
                                                           nodes[tag][i], 7) for i in range(3))
                                                 for tag in element))
                        self.assertIn(reflected, signatures, f'axis={axis} oGrid={ogrid}')
                if ogrid:
                    inner = [.12 * math.cos(math.pi / 24) - 1e-7 <=
                             math.dist(nodes[tag][:2], (.5, .5)) <= .216 + 1e-7
                             for element in elements for tag in element]
                    self.assertGreater(sum(inner), 100)

    def test_grouped_frame_opening_requires_closed_eligible_loop(self):
        corners = ((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0))
        loop = ((.3, .3, 0), (.7, .3, 0), (.7, .7, 0), (.3, .7, 0))
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            config = os.path.join(directory, "settings.json")
            with open(config, "w") as stream:
                json.dump(dict(algorithm=8, recombine=True, recombination=0,
                               optimize=False, smoothing=1, options={}), stream)
            for enabled in (False, True):
                with open(boundary, "w", newline="") as stream:
                    writer = csv.writer(stream)
                    writer.writerow(("AREA", "A1", "Shell"))
                    for i, corner in enumerate(corners, 1):
                        writer.writerow(("CORNER", i, str(i), 0, *corner))
                    for i, start in enumerate(loop):
                        end = loop[(i + 1) % 4]
                        writer.writerow(("FRAME", i, str(i), 0, *start, *end, 0, int(enabled)))
                main(boundary, output, .08, config)
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                centers = [tuple(sum(nodes[tag][axis] for tag in element) / len(element) for axis in (0, 1))
                           for element in elements]
                self.assertEqual(any(.31 < x < .69 and .31 < y < .69 for x, y in centers), not enabled)
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, corner in enumerate(corners, 1):
                    writer.writerow(("CORNER", i, str(i), 0, *corner))
                for i in range(3):
                    writer.writerow(("FRAME", i, str(i), 0, *loop[i], *loop[i + 1], 0, 1))
            with self.assertRaisesRegex(ValueError, "no closed"):
                main(boundary, output, .08, config)

    def test_grouped_opening_survives_coincident_non_group_frames(self):
        # The long grouped right side has been deduplicated by the SAP-side
        # segment splitter. Three pieces now carry the names of overlapping
        # non-group frames; marking those pieces is required to close the loop.
        corners = ((0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0))
        grouped = (((.6, .6, 0), (1.4, .6, 0)),
                   ((.6, .6, 0), (.6, 1.4, 0)),
                   ((.6, 1.4, 0), (1.4, 1.4, 0)))
        right = (((1.4, .4, 0), (1.4, .6, 0)),
                 ((1.4, .6, 0), (1.4, 1, 0)),
                 ((1.4, 1, 0), (1.4, 1.4, 0)),
                 ((1.4, 1.4, 0), (1.4, 1.6, 0)))
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, 'boundary.csv')
            output = os.path.join(directory, 'mesh.csv')
            config = os.path.join(directory, 'settings.json')
            with open(config, 'w') as stream:
                json.dump(dict(algorithm=8, recombine=True, recombination=0,
                               optimize=False, smoothing=1, options={}), stream)
            for recovered in (False, True):
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', 'A1', 'Shell'))
                    for i, corner in enumerate(corners, 1):
                        writer.writerow(('CORNER', i, str(i), 0, *corner))
                    for i, (first, last) in enumerate(grouped + right, 1):
                        belongs = i <= len(grouped) or recovered and i in (5, 6)
                        writer.writerow(('FRAME', i, str(i), 0, *first, *last, 0, int(belongs)))
                if not recovered:
                    with self.assertRaisesRegex(ValueError, 'no closed'):
                        main(boundary, output, .16, config)
                    continue
                main(boundary, output, .16, config)
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
                self.assertTrue(elements)
                self.assertFalse(any(.62 < sum(nodes[tag][0] for tag in element) / len(element) < 1.38
                                     and .62 < sum(nodes[tag][1] for tag in element) / len(element) < 1.38
                                     for element in elements))

    def test_vertical_wall_base_crosses_opening_in_horizontal_slab(self):
        # A wall/base intersection lies inside a removed floor cell but still
        # belongs to the retained vertical wall. A second intersection just
        # outside the opening must remain connected to both shell meshes.
        from sap_gmsh_mesher import mesh_areas
        inside = (0, .75, 0)
        outside = (0, 1.25, 0)
        with tempfile.TemporaryDirectory() as directory:
            inputs = []
            for index in (0, 1):
                boundary = os.path.join(directory, f'area{index}.csv')
                output = os.path.join(directory, f'mesh{index}.csv')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', f'A{index}', 'Shell'))
                    corners = (((-2, -2, 0), (2, -2, 0), (2, 2, 0), (-2, 2, 0))
                               if index == 0 else
                               ((0, .5, 0), (0, 1.5, 0), (0, 1.5, 1), (0, .5, 1)))
                    for number, point in enumerate(corners, 1):
                        writer.writerow(('CORNER', number, str(number), 0, *point))
                    if index == 0:
                        for number, point in enumerate((inside, outside), 1):
                            writer.writerow(('INTERIOR', 0, f'wall-base-{number}', 0, *point))
                        opening = ((-1, -1, 0), (1, -1, 0), (1, 1, 0), (-1, 1, 0))
                        for number, point in enumerate(opening, 1):
                            writer.writerow(('FRAME', number, f'opening-{number}', 0, *point,
                                             *opening[number % 4], 0, 1))
                        # Automatic wall/slab intersection constraints straddle
                        # the opening. The part in the void is not a floor edge;
                        # the part beyond the opening remains mandatory.
                        writer.writerow(('FRAME', 5, 'wall-inside-opening', 0,
                                         0, .5, 0, 0, 1, 0, 1, 0))
                        writer.writerow(('FRAME', 6, 'wall-outside-opening', 0,
                                         0, 1, 0, 0, 1.5, 0, 1, 0))
                    else:
                        for station, point in ((.25, inside), (.75, outside)):
                            writer.writerow(('EDGE', 1, 'wall-base', station, *point))
                inputs.append((boundary, output))
            mesh_areas(inputs, .25, dict(algorithm=8, recombine=True, recombination=0,
                                         optimize=False, smoothing=1, options={}))
            used_by_area = []
            for _, output in inputs:
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == 'N'}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == 'E']
                self.assertTrue(elements)
                used_by_area.append({nodes[tag] for element in elements for tag in element})
            self.assertFalse(any(math.dist(inside, point) < 1e-7 for point in used_by_area[0]))
            self.assertTrue(any(math.dist(inside, point) < 1e-7 for point in used_by_area[1]))
            for area in used_by_area:
                self.assertTrue(any(math.dist(outside, point) < 1e-7 for point in area))

    def test_grouped_opening_crosses_unprotected_shared_area_seam(self):
        from sap_gmsh_mesher import mesh_areas
        with tempfile.TemporaryDirectory() as directory:
            outputs = []
            for index in (0, 1):
                boundary = os.path.join(directory, f"area{index}.csv")
                output = os.path.join(directory, f"mesh{index}.csv")
                with open(boundary, "w", newline="") as stream:
                    writer = csv.writer(stream)
                    writer.writerow(("AREA", index, "Shell"))
                    for edge, corner in enumerate(((index, 0, 0), (index + 1, 0, 0),
                                                   (index + 1, 1, 0), (index, 1, 0)), 1):
                        writer.writerow(("CORNER", edge, str(edge), 0, *corner))
                    for station in (.3, .7):
                        writer.writerow(("EDGE", 2 if index == 0 else 4, "shared", station,
                                         1, station, 0))
                    segments = (
                        [((.7, .3, 0), (1, .3, 0)), ((1, .7, 0), (.7, .7, 0)),
                         ((.7, .7, 0), (.7, .3, 0))] if index == 0 else
                        [((1, .3, 0), (1.3, .3, 0)), ((1.3, .3, 0), (1.3, .7, 0)),
                         ((1.3, .7, 0), (1, .7, 0))])
                    for number, (first, last) in enumerate(segments, 1):
                        writer.writerow(("FRAME", number, str(number), 0, *first, *last, 0, 1))
                outputs.append((boundary, output))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            optimize=False, smoothing=1, options={})
            mesh_areas(outputs, .08, settings)
            seams = []
            for _, output in outputs:
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                self.assertTrue(elements)
                self.assertFalse(any(.71 < sum(nodes[tag][0] for tag in element) / len(element) < 1.29
                                     and .31 < sum(nodes[tag][1] for tag in element) / len(element) < .69
                                     for element in elements))
                seams.append({tuple(sorted((a, b))) for element in elements
                              for a, b in zip(element, element[1:] + element[:1])
                              if abs(nodes[a][0] - 1) < 1e-8 and abs(nodes[b][0] - 1) < 1e-8})
            self.assertTrue(seams[0])
            self.assertEqual(*seams)

    def test_selected_frames_are_conforming_shell_edges(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings = os.path.join(directory, "settings.json")
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=6, recombine=True, recombination=1,
                               optimize=True, smoothing=1, options={}), stream)
            for frames in (
                    [("crossing", (0, 0.5, 0), (1, 0.5, 0))],
                    [("ending inside", (0, 0.5, 0), (0.5, 0.5, 0))],
                    [("crossing", (0, 0.5, 0), (1, 0.5, 0)),
                     ("perpendicular", (0.5, 0, 0), (0.5, 1, 0))]):
                with open(boundary, "w", newline="") as stream:
                    writer = csv.writer(stream)
                    writer.writerow(("AREA", "A1", "Shell"))
                    for i, coords in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                        writer.writerow(("CORNER", i, str(i), 0, *coords))
                    writer.writerow(("EDGE", 4, "left-frame-end", .5, 0, .5, 0))
                    if frames[0][2] == (1, .5, 0):
                        writer.writerow(("EDGE", 2, "right-frame-end", .5, 1, .5, 0))
                    writer.writerow(("INTERIOR", 0, "joint", 0, .5, .5, 0))
                    for i, (name, a, b) in enumerate(frames, 1):
                        writer.writerow(("FRAME", i, name, 0, *a, *b, 0, 0))
                main(boundary, output, 0.2, settings)
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                self.assertTrue(elements)
                validate_constraints(nodes, elements, [(.5, .5, 0)], frames)
                self.assertTrue(any((.5, .5, 0) in [nodes[tag] for tag in element]
                                    for element in elements))

    def test_missing_constraints_are_rejected(self):
        nodes = {1: (0, 0, 0), 2: (1, 0, 0), 3: (1, 1, 0), 4: (0, 1, 0)}
        elements = [(1, 2, 3), (1, 3, 4)]
        with self.assertRaisesRegex(ValueError, "joint"):
            validate_constraints(nodes, elements, [(.5, .25, 0)], [])
        with self.assertRaisesRegex(ValueError, "frame"):
            validate_constraints(nodes, elements, [], [("diagonal not in mesh", (0, 1, 0), (1, 0, 0))])

    def test_selected_interior_edge_joint_prevents_new_boundary_nodes(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            config = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for index, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(("CORNER", index, str(index), 0, *point))
                writer.writerow(("EDGE", 1, "M", .5, .5, 0, 0))
                writer.writerow(("EXPLICIT_POINT", 0, "M", 0, .5, 0, 0))
                writer.writerow(("PRESERVE_SELECTED_EDGE", 1))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            optimize=False, smoothing=1, preserveSelectedEdges=True, options={})
            with open(config, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .2, config)
            with open(output, newline="") as stream:
                rows = list(csv.reader(stream))
            edge = {round(float(row[2]), 7) for row in rows if row[0] == "N" and
                    abs(float(row[3])) < 1e-8 and 0 <= float(row[2]) <= 1}
            self.assertEqual(edge, {0, .5, 1}, "The selected point must remain without inserted edge stations")
            settings["recombination"] = 1
            with open(config, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .2, config)
            with open(output, newline="") as stream:
                blossom_rows = list(csv.reader(stream))
            self.assertEqual({round(float(row[2]), 7) for row in blossom_rows
                              if row[0] == "N" and abs(float(row[3])) < 1e-8
                              and 0 <= float(row[2]) <= 1}, {0, .5, 1},
                             "Blossom must also preserve the selected boundary intervals")
            settings["recombination"] = 0
            settings["preserveSelectedEdges"] = False
            with open(config, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .2, config)
            with open(output, newline="") as stream:
                rows = list(csv.reader(stream))
            self.assertGreater(sum(row[0] == "N" and abs(float(row[3])) < 1e-8 for row in rows), 3)
            settings["preserveSelectedEdges"] = True
            settings["options"] = {"Mesh.SubdivisionAlgorithm": 1}
            with open(config, "w") as stream:
                json.dump(settings, stream)
            with self.assertRaisesRegex(ValueError, "bisects prescribed"):
                main(boundary, output, .2, config)

    def test_preserved_selected_joint_on_shared_edge_remains_conforming(self):
        with tempfile.TemporaryDirectory() as directory:
            areas = []
            for area, polygon, protected_edge in (
                    ('A1', ((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 2),
                    ('A2', ((1, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0)), 4)):
                boundary = os.path.join(directory, area + '.csv')
                output = os.path.join(directory, area + '-mesh.csv')
                with open(boundary, 'w', newline='') as stream:
                    writer = csv.writer(stream)
                    writer.writerow(('AREA', area, 'Shell'))
                    for index, corner in enumerate(polygon, 1):
                        writer.writerow(('CORNER', index, str(index), 0, *corner))
                    writer.writerow(('EDGE', protected_edge, 'M', .5, 1, .5, 0))
                    writer.writerow(('EXPLICIT_POINT', 0, 'M', 0, 1, .5, 0))
                    writer.writerow(('PRESERVE_SELECTED_EDGE', protected_edge))
                areas.append((boundary, output))
            settings = dict(algorithm=8, recombine=True, recombination=0,
                            optimize=False, smoothing=1, preserveSelectedEdges=True, options={})
            mesh_areas(areas, .25, settings)
            seam_nodes = []
            for _, output in areas:
                with open(output, newline='') as stream:
                    rows = list(csv.reader(stream))
                seam_nodes.append({round(float(row[3]), 7) for row in rows
                                   if row[0] == 'N' and abs(float(row[2]) - 1) < 1e-8
                                   and -.01 <= float(row[3]) <= 1.01})
            self.assertEqual(seam_nodes, [{0, .5, 1}, {0, .5, 1}])

    def test_selectable_2d_optimizers_and_invalid_3d_subdivision(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings_path = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, corner in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(("CORNER", i, str(i), 0, *corner))
            for method in ("Relocate2D", "QuadQuasiStructured", "Laplace2D"):
                settings = dict(algorithm=8, recombine=True, recombination=1,
                                optimize=True, optimizeMethod=method, optimizeIterations=2,
                                smoothing=1, options={})
                with open(settings_path, "w") as stream:
                    json.dump(settings, stream)
                main(boundary, output, .25, settings_path)
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                self.assertTrue(any(row[0] == "E" and len(row) == 5 for row in rows), method)
            settings["options"] = {"Mesh.SubdivisionAlgorithm": 2}
            with open(settings_path, "w") as stream:
                json.dump(settings, stream)
            with self.assertRaisesRegex(ValueError, "3D volume"):
                main(boundary, output, .25, settings_path)
            settings["options"] = {}
            settings["optimizeMethod"] = "UntangleMeshGeometry"
            with open(settings_path, "w") as stream:
                json.dump(settings, stream)
            with self.assertRaisesRegex(ValueError, "Unsupported 2D optimizer"):
                main(boundary, output, .25, settings_path)

    def test_adjacent_sap_areas_share_gmsh_seam_and_frame_nodes(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for index, x in enumerate((0, 1), 1):
                path = os.path.join(directory, f"area-{index}.csv")
                with open(path, "w", newline="") as stream:
                    writer = csv.writer(stream)
                    writer.writerow(("AREA", index, "Shell"))
                    corners = ((x, 0, 0), (x + 1, 0, 0), (x + 1, 1, 0), (x, 1, 0))
                    for edge, point in enumerate(corners, 1):
                        writer.writerow(("CORNER", edge, str(edge), 0, *point))
                    writer.writerow(("INTERIOR", 0, "selected-joint", 0, x + .5, .5, 0))
                    writer.writerow(("FRAME", 1, "F" + str(index), 0, x, .5, 0, x + 1, .5, 0, 0, 0))
                    writer.writerow(("EDGE", 2 if x == 0 else 4, "shared-joint", .5, 1, .5, 0))
                paths.append(path)
            manifest = os.path.join(directory, "batch.json")
            with open(manifest, "w") as stream:
                json.dump({"areas": [{"boundary": f"area-{i}.csv", "mesh": f"mesh-{i}.csv"}
                                     for i in (1, 2)]}, stream)
            settings = os.path.join(directory, "settings.json")
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=8, recombine=True, recombination=0,
                               optimize=False, smoothing=1, options={}), stream)
            main(manifest, directory, .25, settings)
            seams = []
            for index in (1, 2):
                with open(os.path.join(directory, f"mesh-{index}.csv"), newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                self.assertTrue(elements)
                self.assertTrue(any(abs(nodes[tag][0] - (index - .5)) < 1e-8 for element in elements for tag in element))
                seam = set()
                for element in elements:
                    for a, b in zip(element, element[1:] + element[:1]):
                        if abs(nodes[a][0] - 1) < 1e-8 and abs(nodes[b][0] - 1) < 1e-8:
                            seam.add(tuple(sorted((a, b))))
                seams.append(seam)
            self.assertTrue(seams[0])
            self.assertEqual(seams[0], seams[1], "Adjacent SAP surfaces must share the exact Gmsh seam")
            settings_data = dict(algorithm=8, recombine=True, recombination=0,
                                 optimize=False, smoothing=1,
                                 options={"Mesh.MeshSizeFromPoints": 0, "Mesh.MeshSizeExtendFromBoundary": 0},
                                 localField={"target": "frames", "sizeNear": .06, "sizeFar": .25,
                                             "distanceNear": .02, "distanceFar": .3})
            with open(settings, "w") as stream:
                json.dump(settings_data, stream)
            main(manifest, directory, .25, settings)
            refined_seams = []
            for index in (1, 2):
                with open(os.path.join(directory, f"mesh-{index}.csv"), newline="") as stream:
                    rows = list(csv.reader(stream))
                nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
                elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
                seam = {tuple(sorted((a, b))) for element in elements
                        for a, b in zip(element, element[1:] + element[:1])
                        if abs(nodes[a][0] - 1) < 1e-8 and abs(nodes[b][0] - 1) < 1e-8}
                refined_seams.append(seam)
            self.assertTrue(refined_seams[0])
            self.assertEqual(refined_seams[0], refined_seams[1], "Local fields must preserve shared SAP seams")

    def test_transfinite_methods_and_local_frame_sizing(self):
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings_path = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(("CORNER", i, str(i), 0, *point))
            settings = dict(algorithm=6, recombine=True, recombination=1,
                            optimize=False, smoothing=1, options={})
            for structured in ({"mode": "manual", "nodes": 5, "distribution": "Progression",
                                "coefficient": 1, "arrangement": "Left"},
                               {"mode": "automatic", "cornerAngle": 2.35}):
                settings["structured"] = structured
                with open(settings_path, "w") as stream:
                    json.dump(settings, stream)
                main(boundary, output, .25, settings_path)
                with open(output, newline="") as stream:
                    rows = list(csv.reader(stream))
                self.assertTrue(any(row[0] == "E" and len(row) == 5 for row in rows))
                if structured["mode"] == "manual":
                    self.assertEqual(sum(row[0] == "E" for row in rows), 16)

            with open(boundary, "a", newline="") as stream:
                csv.writer(stream).writerow(("FRAME", 1, "selected", 0, 0, .5, 0, 1, .5, 0, 0, 0))
            settings["structured"] = {"mode": "unstructured"}
            with open(settings_path, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .25, settings_path)
            with open(output, newline="") as stream:
                without_field = sum(row[0] == "E" for row in csv.reader(stream))
            settings["localField"] = {"target": "frames", "sizeNear": .06, "sizeFar": .25,
                                      "distanceNear": .02, "distanceFar": .28}
            settings["options"] = {"Mesh.MeshSizeFromPoints": 0, "Mesh.MeshSizeExtendFromBoundary": 0}
            with open(settings_path, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .25, settings_path)
            with open(output, newline="") as stream:
                with_field = sum(row[0] == "E" for row in csv.reader(stream))
            self.assertGreater(with_field, without_field, "The Distance/Threshold field must actually refine near selected frames")
            settings.pop("localField")
            settings["fieldGraph"] = [
                {"id": "edge", "type": "Distance", "options": {"CurvesList": "$selectedFrames"}},
                {"type": "Threshold", "options": {"InField": "edge", "SizeMin": .06,
                                                     "SizeMax": .25, "DistMin": .02, "DistMax": .28},
                 "background": True}]
            with open(settings_path, "w") as stream:
                json.dump(settings, stream)
            main(boundary, output, .25, settings_path)
            with open(output, newline="") as stream:
                graph_elements = sum(row[0] == "E" for row in csv.reader(stream))
            self.assertGreater(graph_elements, without_field,
                               "Advanced Gmsh field graphs must reach the size-field API")

    def test_installed_backend_when_requested(self):
        backend = os.environ.get("SAP_GMSH_V2_BACKEND")
        if not backend:
            self.skipTest("Set SAP_GMSH_V2_BACKEND to test the packaged runtime")
        with tempfile.TemporaryDirectory() as directory:
            boundary = os.path.join(directory, "boundary.csv")
            output = os.path.join(directory, "mesh.csv")
            settings = os.path.join(directory, "settings.json")
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, coords in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(("CORNER", i, str(i), 0, *coords))
                writer.writerow(("INTERIOR", 0, "joint", 0, .5, .5, 0))
                writer.writerow(("FRAME", 1, "selected-frame", 0, 0, .5, 0, 1, .5, 0, 0, 0))
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=6, recombine=True, recombination=1,
                               optimize=False, smoothing=1, options={}), stream)
            script = os.path.join(os.path.dirname(backend), "..", "sap_gmsh_mesher.py")
            if not os.path.isfile(script):
                script = os.path.join(os.path.dirname(__file__), "sap_gmsh_mesher.py")
            process = subprocess.run([backend, script, boundary, output, "0.25", settings],
                                     capture_output=True, text=True, timeout=90)
            self.assertEqual(process.returncode, 0, process.stderr)
            with open(output, newline="") as stream:
                rows = list(csv.reader(stream))
            self.assertTrue(any(row[0] == "E" and len(row) == 5 for row in rows))
            nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
            elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
            validate_constraints(nodes, elements, [(.5, .5, 0)],
                                  [("selected-frame", (0, .5, 0), (1, .5, 0))])
            # Exercise the shipped worker with the new V2 features, not just
            # the pre-existing rectangle path used by the self-test.
            with open(boundary, "w", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("AREA", "A1", "Shell"))
                for i, point in enumerate(((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), 1):
                    writer.writerow(("CORNER", i, str(i), 0, *point))
                writer.writerow(("HOLE", 1, "", .24, .5, .5, 0))
                writer.writerow(("SYMMETRY", .5, .5, 0, 0, 1, 0))
                writer.writerow(("SYMMETRY2", .5, .5, 0, 1, 0, 0))
            with open(settings, "w") as stream:
                json.dump(dict(algorithm=8, recombine=True, recombination=0,
                               optimize=False, smoothing=1, exactSymmetry=True,
                               oGrid=True, options={}), stream)
            process = subprocess.run([backend, script, boundary, output, "0.08", settings],
                                     capture_output=True, text=True, timeout=90)
            self.assertEqual(process.returncode, 0, process.stderr)
            with open(output, newline="") as stream:
                rows = list(csv.reader(stream))
            nodes = {int(row[1]): tuple(map(float, row[2:])) for row in rows if row[0] == "N"}
            elements = [tuple(map(int, row[1:])) for row in rows if row[0] == "E"]
            self.assertGreaterEqual(sum(len(element) == 4 and all(
                .12 * math.cos(math.pi / 24) - 1e-7 <= math.dist(nodes[tag][:2], (.5, .5)) <= .216 + 1e-7
                for tag in element) for element in elements), 32)


if __name__ == "__main__":
    unittest.main()
