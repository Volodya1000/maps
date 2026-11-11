import importlib.util
import sys
import unittest
from pathlib import Path

from shapely.geometry import LineString, MultiLineString, Point

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

    def test_audit_rejects_building(self):
        route = LineString([(-100, 0), (100, 0)])
        building = {"id": 3, "kind": "Building", "geometry": Point(0, 0).buffer(10)}
        self.assertEqual(pilot.audit(route, [building])["status"], "violation")


class ContactAuditRegressionTests(unittest.TestCase):
    def test_shared_road_section_is_not_silently_accepted(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, 0), (10, 0)])}
        route = LineString([(-1, 0), (5, 0)])
        self.assertNotEqual(pilot.audit(route, [road])["status"], "valid")

    def test_proven_building_violation_beats_unknown_shared_section(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, 0), (10, 0)])}
        building = {"id": 2, "kind": "Building", "geometry": Point(3, 0).buffer(1)}
        route = LineString([(-1, 0), (5, 0)])
        self.assertEqual(pilot.audit(route, [road, building])["status"], "violation")

    def test_point_touch_is_a_road_contact(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, 0), (0, 5)])}
        route = LineString([(-1, 0), (1, 0)])
        self.assertEqual(pilot.audit(route, [road])["status"], "violation")

    def test_nearby_bridge_not_intersecting_water_does_not_open_it(self):
        water = {"id": 1, "kind": "River", "geometry": LineString([(0, -4), (0, 4)])}
        bridge = {
            "id": 2,
            "kind": "BridgeCandidate",
            "geometry": LineString([(1, -4), (1, 4)]),
        }
        route = LineString([(-5, 0), (5, 0)])
        self.assertEqual(pilot.audit(route, [water, bridge])["status"], "violation")

    def test_mixed_shared_and_point_contacts_do_not_hide_violation(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, 0), (10, 0)])}
        route = MultiLineString([[(0, 0), (3, 0)], [(7, -1), (7, 1)]])
        checks = pilot.audit(route, [road])
        self.assertEqual(checks["status"], "violation")
        self.assertEqual(
            {f["reason"] for f in checks["findings"]},
            {"shared_road_section", "road_contact"},
        )

    def test_bridge_away_from_road_does_not_authorize_contact(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -10), (0, 10)])}
        bridge = {
            "id": 2,
            "kind": "BridgeCandidate",
            "geometry": LineString([(1, -4), (1, 4)]),
        }
        route = LineString([(-5, 0), (5, 0)])
        self.assertEqual(pilot.audit(route, [road, bridge])["status"], "violation")

    def test_gas_span_must_align_with_road_contact(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -10), (0, 10)])}
        gas = {
            "id": 2,
            "kind": "GasPipelineCrossingCandidate",
            "geometry": LineString([(-5, 4), (5, 4)]),
        }
        route = LineString([(-5, 0), (5, 0)])
        self.assertEqual(pilot.audit(route, [road, gas])["status"], "violation")

    def test_water_without_opening_is_violation(self):
        water = {"id": 2, "kind": "River", "geometry": LineString([(0, -5), (0, 5)])}
        route = LineString([(-10, 0), (10, 0)])
        self.assertEqual(pilot.audit(route, [water])["status"], "violation")


class AcceptanceTests(unittest.TestCase):
    def test_v2_preserves_old_failures_and_records_witness_and_complex(self):
        import json

        root = MODULE.parents[1] / "samples"
        v1 = json.loads((root / "routing-crossing-pilot.json").read_text())
        v2 = json.loads((root / "routing-crossing-pilot-v2.json").read_text())
        by_id = {case["id"]: case for case in v2}
        self.assertEqual(len(v2), 10)
        for case_id in (
            "naliboki-02",
            "pukhovichi-01",
            "pukhovichi-02",
            "pukhovichi-03",
            "smolevichi-01",
            "smolevichi-02",
        ):
            self.assertEqual(
                by_id[case_id]["points"],
                next(case["points"] for case in v1 if case["id"] == case_id),
            )
        self.assertEqual(by_id["smolevichi-01"]["category"], "E")
        self.assertGreaterEqual(
            len(by_id["smolevichi-01"]["classificationEvidence"]["directRoadIds"]), 2
        )
        self.assertEqual(by_id["smolevichi-04"]["witness"]["kind"], "direct_bridge")

    def test_window_preserves_proven_violation(self):
        checks = {"status": "violation", "findings": [{"reason": "blocked_terrain"}]}
        pilot.note_incomplete_audit(checks, "outside_audit_window")
        self.assertEqual(checks["status"], "violation")
        self.assertEqual(checks["findings"][-1]["reason"], "outside_audit_window")

    def test_verified_witness_missed_and_profile_reasons_are_reported(self):
        case = {
            "id": "bridge",
            "category": "A",
            "quality": "verified",
            "witness": {"kind": "direct_bridge"},
        }
        result = {
            "id": "bridge",
            "status": "no_route",
            "audit": {"status": "unknown", "alternatives": []},
            "alternatives": [],
            "error": {
                "searchFailures": [
                    {"profile": "Короче", "reason": "NoRouteInSearchArea"}
                ]
            },
        }
        acceptance = pilot.pilot_acceptance([case], [result])
        self.assertEqual(acceptance["confirmedMissedPaths"], ["bridge"])
        self.assertEqual(
            acceptance["failureReasonsByProfile"]["Короче"]["NoRouteInSearchArea"], 1
        )
        self.assertEqual(acceptance["harnessIntegrity"], "pass")
        self.assertFalse(acceptance["pilotAccepted"])

    def test_success_without_route_or_matching_audit_fails_integrity(self):
        case = {"id": "a", "category": "F", "quality": "verified"}
        result = {
            "id": "a",
            "status": "success",
            "alternatives": [],
            "audit": {"status": "valid", "alternatives": []},
        }
        self.assertNotEqual(
            pilot.pilot_acceptance([case], [result])["harnessIntegrity"], "pass"
        )
        result["alternatives"] = [{"name": "Короче", "geometry": []}]
        self.assertNotEqual(
            pilot.pilot_acceptance([case], [result])["harnessIntegrity"], "pass"
        )

    def test_duplicate_result_ids_and_bad_audit_fail_integrity(self):
        cases = [
            {"id": x, "category": "B", "quality": "exploratory"} for x in ("a", "b")
        ]
        result = {
            "id": "a",
            "status": "no_route",
            "alternatives": [],
            "audit": {"status": "unknown", "alternatives": []},
        }
        self.assertNotEqual(
            pilot.pilot_acceptance(cases, [result, result])["harnessIntegrity"], "pass"
        )
        result = {
            "id": "a",
            "status": "success",
            "alternatives": [
                {
                    "name": "Короче",
                    "geometry": [
                        {"Latitude": 1, "Longitude": 1},
                        {"Latitude": 2, "Longitude": 2},
                    ],
                }
            ],
            "audit": {"status": "valid", "alternatives": []},
        }
        self.assertNotEqual(
            pilot.pilot_acceptance(cases[:1], [result])["harnessIntegrity"], "pass"
        )

    def test_inconsistent_valid_audit_does_not_pass_integrity(self):
        case = {"id": "a", "category": "B", "quality": "exploratory"}
        alt = {
            "name": "Короче",
            "geometry": [
                {"Latitude": 1, "Longitude": 1},
                {"Latitude": 2, "Longitude": 2},
            ],
        }
        checks = {
            "status": "valid",
            "findings": [
                {
                    "reason": "road_contact",
                    "allowed": True,
                    "bridgeIds": [],
                    "gasIds": [],
                }
            ],
        }
        result = {
            "id": "a",
            "status": "success",
            "alternatives": [alt],
            "audit": {"status": "valid", "alternatives": [checks]},
        }
        self.assertNotEqual(
            pilot.pilot_acceptance([case], [result])["harnessIntegrity"], "pass"
        )

    def test_unchecked_evidence_is_unverified_not_unconditionally_unmet(self):
        case = {"id": "a", "category": "F", "quality": "verified"}
        result = {
            "id": "a",
            "status": "no_route",
            "alternatives": [],
            "audit": {"status": "unknown", "alternatives": []},
        }
        acceptance = pilot.pilot_acceptance([case], [result])
        self.assertIn(
            "manual review of all cases", acceptance["unverifiedRequirements"]
        )
        self.assertNotIn("manual review of all cases", acceptance["unmetRequirements"])
        self.assertIn(
            "two independent positive A/C witnesses", acceptance["unmetRequirements"]
        )

    def test_explicit_evidence_changes_only_its_requirement(self):
        case = {"id": "a", "category": "F", "quality": "verified"}
        result = {
            "id": "a",
            "status": "no_route",
            "alternatives": [],
            "audit": {"status": "unknown", "alternatives": []},
        }
        evidence = {
            "repeat with semantic comparison": {
                "status": "met",
                "source": "repeat-comparison.json",
            }
        }
        acceptance = pilot.pilot_acceptance([case], [result], evidence=evidence)
        self.assertNotIn(
            "repeat with semantic comparison", acceptance["unverifiedRequirements"]
        )
        self.assertEqual(
            acceptance["evidenceAssessments"]["repeat with semantic comparison"][
                "status"
            ],
            "met",
        )
        self.assertIn(
            "manual review of all cases", acceptance["unverifiedRequirements"]
        )

    def test_missing_results_do_not_prove_absence_of_a_successful_crossing(self):
        cases = [{"id": "a", "category": "A", "quality": "exploratory"}]
        acceptance = pilot.pilot_acceptance(cases, [])
        self.assertIn(
            "successful model-eligible road crossing",
            acceptance["unverifiedRequirements"],
        )
        self.assertNotIn(
            "successful model-eligible road crossing", acceptance["unmetRequirements"]
        )
        self.assertNotEqual(acceptance["harnessIntegrity"], "pass")

    def test_measured_criteria_can_pass_without_claiming_manual_acceptance(self):
        cases = []
        results = []
        for i in range(10):
            case = {
                "id": str(i),
                "category": (
                    "A" if i < 2 else "E" if i == 2 else "F" if i == 3 else "B"
                ),
                "quality": "verified" if i < 4 else "exploratory",
                "area": "one" if i < 5 else "two",
                "distanceClass": "local" if i < 6 else "full",
            }
            if i < 2:
                case["witness"] = {"kind": "direct_bridge"}
            cases.append(case)
            finding = {
                "reason": "road_contact",
                "roadId": 1,
                "allowed": True,
                "bridgeIds": [2],
                "gasIds": [],
                "coordinate": [0.0, 0.0],
            }
            alt = {
                "name": "Короче",
                "geometry": [
                    {"Latitude": 1, "Longitude": 1},
                    {"Latitude": 2, "Longitude": 2},
                ],
            }
            results.append(
                {
                    "id": str(i),
                    "status": "success",
                    "alternatives": [alt],
                    "audit": {
                        "status": "valid",
                        "alternatives": [{"status": "valid", "findings": [finding]}],
                    },
                }
            )
        acceptance = pilot.pilot_acceptance(cases, results)
        self.assertEqual(acceptance["harnessIntegrity"], "pass")
        self.assertEqual(acceptance["unmetRequirements"], [])
        self.assertIsNone(acceptance["pilotAccepted"])
        self.assertTrue(acceptance["unverifiedRequirements"])

    def test_missing_categories_and_witness_block_acceptance(self):
        cases = [{"id": "a", "category": "F", "quality": "verified"}]
        results = [
            {
                "id": "a",
                "status": "success",
                "audit": {
                    "status": "valid",
                    "alternatives": [{"status": "valid", "findings": []}],
                },
                "alternatives": [],
                "error": None,
            }
        ]
        acceptance = pilot.pilot_acceptance(cases, results)
        self.assertFalse(acceptance["pilotAccepted"])
        self.assertIn("E", acceptance["missingCategories"])
        self.assertEqual(acceptance["successfulRoutesWithAllowedRoadContact"], 0)
        self.assertEqual(acceptance["verifiedPositiveWitnesses"], 0)


if __name__ == "__main__":
    unittest.main()
