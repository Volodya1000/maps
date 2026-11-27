"""Portal evidence audits whole witness paths and preserves read-only inputs."""

import copy
import importlib
import io
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing, redirect_stdout
from pathlib import Path

from shapely.geometry import LineString

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))

witnesses = importlib.import_module("routing_portal_witnesses")


class RoutingPortalWitnessTests(unittest.TestCase):
    def test_independent_synthetic_witness_requires_the_mapped_water_opening(self):
        self.assertEqual(witnesses.synthetic_witness_audit()["status"], "valid")

        without_bridge = witnesses.synthetic_witness_audit(include_bridge=False)

        self.assertEqual(without_bridge["status"], "violation")
        self.assertTrue(
            any(
                finding["reason"] == "water_without_opening"
                for finding in without_bridge["findings"]
            )
        )

    def test_full_witness_approach_is_audited_in_both_directions_without_writing_input(
        self,
    ):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            database = folder / "geodata.sqlite"
            with closing(sqlite3.connect(database)) as connection:
                connection.executescript(
                    "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
                    "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                    "CREATE TABLE underpass_roads(candidate_id,road_id);"
                    "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
                )
                features = [
                    (
                        1,
                        "Road",
                        LineString([(0.00005, -0.001), (0.00005, 0.001)]),
                        "way/unpermitted-approach",
                    ),
                    *[
                        (
                            index,
                            "BridgeCandidate",
                            LineString([(-0.00002, 0), (0.00002, 0)]),
                            source,
                        )
                        for index, source in enumerate(
                            witnesses.CASE_SOURCES.values(), start=2
                        )
                    ],
                ]
                for feature_id, kind, geometry, source in features:
                    connection.execute(
                        "INSERT INTO features VALUES(?,?,?,?,NULL)",
                        (feature_id, kind, geometry.wkb, source),
                    )
                    west, south, east, north = geometry.bounds
                    connection.execute(
                        "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                        (feature_id, west, east, south, north),
                    )
                connection.commit()
            points = [
                {"Longitude": -0.0001, "Latitude": 0},
                {"Longitude": 0.0001, "Latitude": 0},
            ]
            corpus = folder / "corpus.json"
            corpus.write_text(
                json.dumps(
                    [
                        {"id": case_id, "points": points, "routingOptions": {}}
                        for case_id in witnesses.CASE_SOURCES
                    ]
                ),
                encoding="utf-8",
            )
            coverage = folder / "coverage.json"
            coverage.write_text(
                json.dumps(
                    [
                        [-0.001, -0.001],
                        [0.001, -0.001],
                        [0.001, 0.001],
                        [-0.001, 0.001],
                    ]
                ),
                encoding="utf-8",
            )
            original_digest = witnesses.digest(database)

            report = witnesses.investigate(database, corpus, coverage)

            self.assertEqual(witnesses.digest(database), original_digest)
            self.assertEqual(report["databaseSha256"], original_digest)
            self.assertEqual(report["corpusSha256"], witnesses.digest(corpus))
            self.assertEqual(len(report["records"]), 3)
            for record in report["records"]:
                self.assertEqual(record["detourExistence"], "unknown")
                self.assertFalse(record["validWitnessFound"])
                self.assertEqual(
                    [attempt["reverseBridge"] for attempt in record["attempts"]],
                    [False, True],
                )
                for attempt in record["attempts"]:
                    self.assertEqual(attempt["audit"]["status"], "violation")
                    self.assertTrue(
                        any(
                            finding.get("roadId") == 1
                            and finding.get("allowed") is False
                            for finding in attempt["audit"]["findings"]
                        )
                    )

    def test_scenario_underpass_default_and_explicit_flag_change_actual_road_permission(
        self,
    ):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            database = folder / "geodata.sqlite"
            with closing(sqlite3.connect(database)) as connection:
                connection.executescript(
                    "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
                    "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                    "CREATE TABLE underpass_roads(candidate_id,road_id);"
                    "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
                )
                features = [
                    (
                        1,
                        "Road",
                        LineString([(0.00005, -0.001), (0.00005, 0.001)]),
                        "way/road",
                    ),
                    (
                        10,
                        "PedestrianUnderpassCandidate",
                        LineString([(-0.00008, 0), (0.00008, 0)]),
                        "way/tunnel",
                    ),
                    *[
                        (
                            index,
                            "BridgeCandidate",
                            LineString([(-0.00002, 0), (0.00002, 0)]),
                            source,
                        )
                        for index, source in enumerate(
                            witnesses.CASE_SOURCES.values(), start=2
                        )
                    ],
                ]
                for feature_id, kind, geometry, source in features:
                    connection.execute(
                        "INSERT INTO features VALUES(?,?,?,?,NULL)",
                        (feature_id, kind, geometry.wkb, source),
                    )
                    west, south, east, north = geometry.bounds
                    connection.execute(
                        "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                        (feature_id, west, east, south, north),
                    )
                connection.execute("INSERT INTO underpass_roads VALUES(10,1)")
                connection.execute(
                    "INSERT INTO underpass_topology VALUES(10,10,'[10,11]','[10,11]')"
                )
                connection.commit()
            corpus = folder / "corpus.json"
            scenarios = [
                {
                    "id": case_id,
                    "points": [
                        {"Longitude": -0.0001, "Latitude": 0},
                        {"Longitude": 0.0001, "Latitude": 0},
                    ],
                    "routingOptions": {},
                }
                for case_id in witnesses.CASE_SOURCES
            ]
            corpus.write_text(json.dumps(scenarios), encoding="utf-8")
            coverage = folder / "coverage.json"
            coverage.write_text(
                json.dumps(
                    [[-0.001, -0.001], [0.001, -0.001], [0.001, 0.001], [-0.001, 0.001]]
                ),
                encoding="utf-8",
            )

            default_report = witnesses.investigate(database, corpus, coverage)
            for scenario in scenarios:
                scenario["routingOptions"]["AllowExperimentalPedestrianUnderpasses"] = (
                    True
                )
            corpus.write_text(json.dumps(scenarios), encoding="utf-8")
            enabled_report = witnesses.investigate(database, corpus, coverage)

            for record in default_report["records"]:
                self.assertFalse(record["effectiveUnderpasses"])
                self.assertTrue(
                    all(
                        attempt["audit"]["status"] == "violation"
                        for attempt in record["attempts"]
                    )
                )
            for record in enabled_report["records"]:
                self.assertTrue(record["effectiveUnderpasses"])
                self.assertTrue(
                    all(
                        attempt["audit"]["status"] == "valid"
                        for attempt in record["attempts"]
                    )
                )
                self.assertTrue(
                    any(
                        finding.get("underpassIds")
                        for attempt in record["attempts"]
                        for finding in attempt["audit"]["findings"]
                    )
                )


class PortalRunComparisonTests(unittest.TestCase):
    def setUp(self):
        self.witness_report = {
            "databaseSha256": "same-db",
            "corpusSha256": "same-corpus",
            "records": [
                {
                    "id": case_id,
                    "effectiveUnderpasses": False,
                    "attempts": [
                        {
                            "geometry": [
                                {"Longitude": 27, "Latitude": 54},
                                {"Longitude": 27.0001, "Latitude": 54},
                            ],
                            "audit": {"status": "valid"},
                        }
                    ],
                }
                for case_id in witnesses.CASE_SOURCES
            ],
        }
        self.head = {
            "provenance": {"databaseSha256": "same-db", "corpusSha256": "same-corpus"},
            "results": [
                {
                    "id": case_id,
                    "group": "frozen",
                    "status": "no_route",
                    "auditStatus": "unknown",
                    "effectiveUnderpasses": False,
                    "diagnostics": {
                        "droppedEvents": 0,
                        "events": [
                            {
                                "eventId": 5,
                                "fields": {
                                    "West": 26,
                                    "East": 28,
                                    "South": 53,
                                    "North": 55,
                                },
                            }
                        ],
                    },
                }
                for case_id in witnesses.CASE_SOURCES
            ],
        }

    def test_in_bounds_valid_witness_missed_by_head_fails_with_case_ids(self):
        comparison = witnesses.compare_run_report(self.witness_report, self.head)

        self.assertFalse(comparison["gatePassed"])
        self.assertEqual(
            set(comparison["missedWitnessIds"]), set(witnesses.CASE_SOURCES)
        )
        self.assertTrue(
            all(case["reason"] == "missed_valid_path" for case in comparison["cases"])
        )

    def test_cli_persists_confirmed_misses_before_returning_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            database = folder / "geodata.sqlite"
            with closing(sqlite3.connect(database)) as connection:
                connection.executescript(
                    "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
                    "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                    "CREATE TABLE underpass_roads(candidate_id,road_id);"
                    "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
                )
                geometry = LineString([(-0.00002, 0), (0.00002, 0)])
                for feature_id, source in enumerate(
                    witnesses.CASE_SOURCES.values(), start=1
                ):
                    connection.execute(
                        "INSERT INTO features VALUES(?,?,?,?,NULL)",
                        (feature_id, "BridgeCandidate", geometry.wkb, source),
                    )
                    connection.execute(
                        "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                        (feature_id, -0.00002, 0.00002, 0, 0),
                    )
                connection.commit()
            corpus = folder / "corpus.json"
            corpus.write_text(
                json.dumps(
                    [
                        {
                            "id": case_id,
                            "points": [
                                {"Longitude": -0.0001, "Latitude": 0},
                                {"Longitude": 0.0001, "Latitude": 0},
                            ],
                            "routingOptions": {},
                        }
                        for case_id in witnesses.CASE_SOURCES
                    ]
                ),
                encoding="utf-8",
            )
            coverage = folder / "coverage.json"
            coverage.write_text(
                json.dumps(
                    [[-0.001, -0.001], [0.001, -0.001], [0.001, 0.001], [-0.001, 0.001]]
                ),
                encoding="utf-8",
            )
            head = copy.deepcopy(self.head)
            head["provenance"] = {
                "databaseSha256": witnesses.digest(database),
                "corpusSha256": witnesses.digest(corpus),
            }
            for result in head["results"]:
                result["diagnostics"]["events"][0]["fields"].update(
                    West=-0.001, South=-0.001, East=0.001, North=0.001
                )
            run_report = folder / "results.json"
            run_report.write_text(json.dumps(head), encoding="utf-8")
            output = folder / "witnesses.json"

            with redirect_stdout(io.StringIO()):
                exit_code = witnesses.main(
                    [
                        "--data",
                        str(folder),
                        "--corpus",
                        str(corpus),
                        "--coverage",
                        str(coverage),
                        "--run-report",
                        str(run_report),
                        "--output",
                        str(output),
                    ]
                )

            self.assertEqual(exit_code, 1)
            saved = json.loads(output.read_text(encoding="utf-8"))
            self.assertFalse(saved["comparison"]["gatePassed"])
            self.assertEqual(
                set(saved["comparison"]["missedWitnessIds"]),
                set(witnesses.CASE_SOURCES),
            )
            self.assertEqual(
                saved["comparison"]["runReportSha256"], witnesses.digest(run_report)
            )
            self.assertTrue(
                all(record["validWitnessFound"] for record in saved["records"])
            )

    def test_valid_witness_outside_actual_search_bounds_remains_unresolved(self):
        for record in self.witness_report["records"]:
            record["attempts"][0]["geometry"].insert(
                1, {"Longitude": 25.99, "Latitude": 54}
            )

        comparison = witnesses.compare_run_report(self.witness_report, self.head)

        self.assertTrue(comparison["gatePassed"])
        self.assertEqual(comparison["missedWitnessIds"], [])
        self.assertTrue(
            all(
                case["reason"] == "witness_outside_search_bounds"
                for case in comparison["cases"]
            )
        )

    def test_valid_audited_head_success_satisfies_in_bounds_witness(self):
        for result in self.head["results"]:
            result.update(status="success", auditStatus="valid")

        comparison = witnesses.compare_run_report(self.witness_report, self.head)

        self.assertTrue(comparison["gatePassed"])
        self.assertEqual(comparison["missedWitnessIds"], [])
        self.assertTrue(
            all(case["reason"] == "valid_route_found" for case in comparison["cases"])
        )

    def test_provenance_inventory_and_flag_mismatches_fail_comparison(self):
        for mutation, expected in (
            ("databaseSha256", "provenance_mismatch"),
            ("corpusSha256", "provenance_mismatch"),
            ("inventory", "inventory_mismatch"),
            ("flag", "underpass_flag_mismatch"),
        ):
            with self.subTest(mutation=mutation):
                head = copy.deepcopy(self.head)
                if mutation == "inventory":
                    head["results"].pop()
                elif mutation == "flag":
                    head["results"][0]["effectiveUnderpasses"] = True
                else:
                    head["provenance"][mutation] = "different"

                comparison = witnesses.compare_run_report(self.witness_report, head)

                self.assertFalse(comparison["gatePassed"])
                self.assertIn(
                    expected, [failure["reason"] for failure in comparison["failures"]]
                )

    def test_missing_or_invalid_mesh_diagnostics_cannot_establish_a_missed_witness(
        self,
    ):
        for invalid in (None, {"droppedEvents": 1, "events": []}):
            with self.subTest(diagnostics=invalid):
                head = copy.deepcopy(self.head)
                head["results"][0]["diagnostics"] = invalid

                comparison = witnesses.compare_run_report(self.witness_report, head)

                self.assertFalse(comparison["gatePassed"])
                self.assertIn(
                    "mesh_bounds_unavailable",
                    [failure["reason"] for failure in comparison["failures"]],
                )

    def test_ordered_recovery_attempts_use_final_search_bounds(self):
        result = copy.deepcopy(self.head["results"][0])
        first = result["diagnostics"]["events"][0]["fields"]
        first.update(CalculationId="same", Attempt=0)
        final = first | {"Attempt": 1, "West": 25, "East": 29}
        result["diagnostics"]["events"].append({"eventId": 5, "fields": final})

        self.assertEqual(witnesses.search_bounds(result), [25, 53, 29, 55])

    def test_malformed_recovery_mesh_sequence_never_supplies_bounds(self):
        for mutation in (
            "gap",
            "reverse",
            "mixed_id",
            "missing_attempt",
            "bool_attempt",
            "duplicate",
        ):
            with self.subTest(mutation=mutation):
                result = copy.deepcopy(self.head["results"][0])
                first = result["diagnostics"]["events"][0]["fields"]
                first.update(CalculationId="same", Attempt=0)
                final = first | {"Attempt": 1}
                if mutation == "gap":
                    final["Attempt"] = 2
                elif mutation == "reverse":
                    first["Attempt"], final["Attempt"] = 1, 0
                elif mutation == "mixed_id":
                    final["CalculationId"] = "different"
                elif mutation == "missing_attempt":
                    del final["Attempt"]
                elif mutation == "bool_attempt":
                    final["Attempt"] = True
                else:
                    final["Attempt"] = 0
                result["diagnostics"]["events"].append({"eventId": 5, "fields": final})

                self.assertIsNone(witnesses.search_bounds(result))

    def test_single_legacy_mesh_stays_compatible(self):
        self.assertEqual(
            witnesses.search_bounds(self.head["results"][0]), [26, 53, 28, 55]
        )


if __name__ == "__main__":
    unittest.main()
