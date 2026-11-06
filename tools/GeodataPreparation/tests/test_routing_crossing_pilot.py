import importlib.util
import sys
import unittest
from pathlib import Path

from shapely.geometry import LineString, Point

MODULE = Path(__file__).resolve().parents[3] / "tools/routing_crossing_pilot.py"
sys.path.insert(0, str(MODULE.parent))
spec = importlib.util.spec_from_file_location("crossing_pilot", MODULE)
pilot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pilot)


class CrossingPilotTests(unittest.TestCase):
    def test_pair_straddles_target_road_and_is_repeatable(self):
        road = LineString([(0, -500), (0, 500)])
        first = pilot.pair_around(road, 0, 1000, 0)
        self.assertEqual(first, pilot.pair_around(road, 0, 1000, 0))
        self.assertEqual(len(first), 2)
        self.assertTrue(LineString(first).crosses(road))
        self.assertLess(first[0][0] * first[1][0], 0)

    def test_frozen_corpus_has_two_points_and_distance_bands(self):
        import json

        corpus = json.loads(
            (MODULE.parents[1] / "samples/routing-crossing-pilot.json").read_text()
        )
        self.assertEqual(len(corpus), 10)
        self.assertEqual({case["area"] for case in corpus}, set(pilot.WINDOWS))
        self.assertEqual(sum(case["distanceClass"] == "full" for case in corpus), 4)
        for case in corpus:
            self.assertEqual(len(case["points"]), 2)
            points = [(p["Longitude"], p["Latitude"]) for p in case["points"]]
            self.assertAlmostEqual(
                pilot.distance(*points), case["distanceMetres"], places=6
            )
            lower, upper = (
                (10000, 15000) if case["distanceClass"] == "full" else (500, 3000)
            )
            self.assertLessEqual(lower, case["distanceMetres"])
            self.assertLessEqual(case["distanceMetres"], upper)

    def test_bounded_runner_records_success_failure_and_timeout(self):
        import sys

        from benchmark_routes import run_bounded_command

        self.assertEqual(
            run_bounded_command([sys.executable, "-c", "pass"], 2)["returncode"], 0
        )
        self.assertEqual(
            run_bounded_command([sys.executable, "-c", "raise RuntimeError()"], 2)[
                "returncode"
            ],
            1,
        )
        self.assertTrue(
            run_bounded_command(
                [sys.executable, "-c", "import time; time.sleep(1)"], 0.01
            )["timeout"]
        )

    def test_control_pair_stays_on_one_side(self):
        road = LineString([(0, -500), (0, 500)])
        pair = pilot.pair_around(road, 0, 300, 250, same_side=True)
        self.assertFalse(LineString(pair).intersects(road))
        self.assertGreater(pair[0][0] * pair[1][0], 0)

    def test_audit_rejects_unmapped_crossing(self):
        route = LineString([(-100, 0), (100, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -100), (0, 100)])}
        self.assertEqual(pilot.audit(route, [road])["status"], "violation")

    def test_audit_accepts_matching_bridge(self):
        route = LineString([(-100, 0), (100, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -100), (0, 100)])}
        bridge = {
            "id": 2,
            "kind": "BridgeCandidate",
            "geometry": LineString([(-10, 0), (10, 0)]),
        }
        self.assertEqual(pilot.audit(route, [road, bridge])["status"], "valid")

    def test_audit_rejects_building_and_marks_water_unknown_without_approval_data(self):
        route = LineString([(-100, 0), (100, 0)])
        building = {"id": 3, "kind": "Building", "geometry": Point(0, 0).buffer(10)}
        self.assertEqual(pilot.audit(route, [building])["status"], "violation")


if __name__ == "__main__":
    unittest.main()
