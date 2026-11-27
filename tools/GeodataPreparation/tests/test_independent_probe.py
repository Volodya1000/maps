"""The research probe reports audited witnesses and bounded, honest limitations."""

import hashlib
import importlib.util
import io
import json
import math
import sqlite3
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

from shapely.geometry import LineString, Polygon

ROOT = Path(__file__).resolve().parents[3]
PROBE = ROOT / "docs/evidence/routing-recovery/independent_probe.py"
METRES_PER_DEGREE = 111195.0802335


def coordinate(x, y):
    return (
        27 + x / (METRES_PER_DEGREE * math.cos(math.radians(54))),
        54 + y / METRES_PER_DEGREE,
    )


def rectangle(west, south, east, north):
    return Polygon(
        [
            coordinate(west, south),
            coordinate(east, south),
            coordinate(east, north),
            coordinate(west, north),
            coordinate(west, south),
        ]
    )


class IndependentProbeTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(PROBE.is_file(), "The portable independent probe is missing")
        spec = importlib.util.spec_from_file_location("independent_probe", PROBE)
        self.probe = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.probe)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.database = self.folder / "geodata.sqlite"
        self.connection = sqlite3.connect(self.database)
        self.addCleanup(self.connection.close)
        self.connection.executescript(
            "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
            "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            "CREATE TABLE underpass_roads(candidate_id,road_id);"
            "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
        )
        self.coverage = rectangle(-100, -100, 200, 100)
        self.scenario = {
            "id": "narrow-corridor",
            "points": [
                dict(zip(("Longitude", "Latitude"), coordinate(x, 0), strict=True))
                for x in (0, 100)
            ],
            "routingOptions": {"MaxRouteLengthMetres": 1000},
        }
        west, south, east, north = rectangle(-10, -50, 110, 50).bounds
        self.record = {
            "id": self.scenario["id"],
            "group": "frozen",
            "status": "no_route",
            "effectiveUnderpasses": False,
            "diagnostics": {
                "events": [
                    {
                        "eventId": 5,
                        "eventName": "SearchMeshLog",
                        "fields": {
                            "West": west,
                            "South": south,
                            "East": east,
                            "North": north,
                        },
                    }
                ]
            },
        }

    def add_feature(self, feature_id, kind, geometry):
        self.connection.execute(
            "INSERT INTO features VALUES(?,?,?,?,NULL)",
            (feature_id, kind, geometry.wkb, f"way/{feature_id}"),
        )
        west, south, east, north = geometry.bounds
        self.connection.execute(
            "INSERT INTO feature_index VALUES(?,?,?,?,?)",
            (feature_id, west, east, south, north),
        )
        self.connection.commit()

    def run_probe(self, **limits):
        return self.probe.probe_case(
            self.connection,
            self.coverage,
            self.scenario,
            self.record,
            margin="mesh",
            width=0.2,
            **limits,
        )

    def test_narrow_corridor_produces_an_audited_full_detour(self):
        self.add_feature(1, "Building", rectangle(-10, 2, 110, 50))
        self.add_feature(2, "Building", rectangle(-10, -50, 110, -2))
        self.add_feature(3, "Marsh", rectangle(40, -0.5, 60, 0.5))
        before = self.database.read_bytes()

        result = self.run_probe()

        self.assertEqual("witness", result["status"])
        self.assertEqual("valid", result["audit"]["status"])
        self.assertEqual([0, 0], result["audit"]["endpointErrorsMetres"])
        self.assertGreater(result["audit"]["computedLengthMetres"], 100)
        self.assertEqual(
            self.scenario["points"],
            [result["alternative"]["geometry"][index] for index in (0, -1)],
        )
        self.assertFalse(result["effectiveUnderpasses"])
        self.assertEqual(before, self.database.read_bytes())

    def test_closed_road_barrier_is_unresolved_without_an_impossibility_claim(self):
        self.add_feature(
            1, "Road", LineString([coordinate(50, -50), coordinate(50, 50)])
        )

        result = self.run_probe()

        self.assertEqual("unresolved", result["status"])
        self.assertEqual("conservative_components_disconnected", result["reason"])
        self.assertFalse(result["connected"])
        self.assertNotIn("alternative", result)

    def test_enabled_mapped_underpass_does_not_carve_a_surface_opening(self):
        self.add_feature(
            1, "Road", LineString([coordinate(50, -50), coordinate(50, 50)])
        )
        self.add_feature(
            2,
            "PedestrianUnderpassCandidate",
            LineString([coordinate(40, 0), coordinate(60, 0)]),
        )
        self.record["effectiveUnderpasses"] = True

        result = self.run_probe()

        self.assertTrue(result["effectiveUnderpasses"])
        self.assertEqual("unresolved", result["status"])
        self.assertNotIn("alternative", result)

    def test_feature_limit_is_reported_before_decoding_over_budget_geometry(self):
        for feature_id in (1, 2):
            self.connection.execute(
                "INSERT INTO features VALUES(?,?,?,?,NULL)",
                (feature_id, "Road", b"not-wkb", f"way/{feature_id}"),
            )
            self.connection.execute(
                "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                (feature_id, 27, 27.001, 54, 54.001),
            )
        self.connection.commit()

        result = self.run_probe(max_features=1)

        self.assertEqual("limited", result["status"])
        self.assertEqual("feature_limit", result["reason"])
        self.assertEqual(2, result["actualCount"])
        self.assertEqual(1, result["limit"])
        self.assertNotIn("alternative", result)

    def test_vertex_limit_reports_structured_counts_without_a_candidate(self):
        self.add_feature(1, "Building", rectangle(40, -10, 60, 10))

        result = self.run_probe(max_vertices=4)

        self.assertEqual("limited", result["status"])
        self.assertEqual("vertex_limit", result["reason"])
        self.assertEqual(5, result["actualCount"])
        self.assertEqual(4, result["limit"])
        self.assertNotIn("alternative", result)

    def arguments(self, report):
        return [
            "--data",
            str(self.folder),
            "--run-report",
            str(report),
            "--output",
            str(self.folder / "output"),
        ]

    def test_cli_rejects_nonfinite_and_nonpositive_search_parameters(self):
        for option, value in (
            ("--margin", "nan"),
            ("--margin", "-1"),
            ("--width", "inf"),
            ("--width", "0"),
        ):
            with self.subTest(option=option, value=value):
                with (
                    redirect_stderr(io.StringIO()),
                    self.assertRaises(SystemExit) as error,
                ):
                    self.probe.main(
                        self.arguments(self.folder / "unused.json") + [option, value]
                    )
                self.assertEqual(2, error.exception.code)
        self.assertFalse((self.folder / "output").exists())

    def test_cli_rejects_malformed_report_without_creating_output(self):
        _, report = self.cli_input()
        report.write_text("{", encoding="utf-8")

        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            self.probe.main(self.arguments(report))

        self.assertEqual(2, error.exception.code)
        self.assertFalse((self.folder / "output").exists())

    def cli_input(self):
        corpus_path = ROOT / "samples/routing-crossing-mass-v1.json"
        scenario = next(
            scenario
            for scenario in json.loads(corpus_path.read_text(encoding="utf-8"))
            if scenario["id"] == "mass-naliboki-bridge-02"
        )
        database_sha = hashlib.sha256(self.database.read_bytes()).hexdigest()
        source_sha = "a" * 64
        manifest = self.folder / "manifest.json"
        manifest.write_text(
            json.dumps(
                {
                    "schema_version": 3,
                    "database_sha256": database_sha,
                    "sources": [{"sha256": source_sha}],
                }
            ),
            encoding="utf-8",
        )
        (self.folder / "package.json").write_text(
            json.dumps(
                {
                    "key": "synthetic-test-package",
                    "source_sha256": source_sha,
                    "files": {
                        "manifest.json": hashlib.sha256(
                            manifest.read_bytes()
                        ).hexdigest()
                    },
                }
            ),
            encoding="utf-8",
        )
        record = self.record | {"id": scenario["id"]}
        record["diagnostics"] = {
            "events": [
                {
                    "eventId": 5,
                    "fields": dict(
                        zip(
                            ("West", "South", "East", "North"),
                            scenario["auditBounds"],
                            strict=True,
                        )
                    ),
                }
            ]
        }
        report = self.folder / "run.json"
        report.write_text(
            json.dumps(
                {
                    "provenance": {
                        "databaseSha256": database_sha,
                        "sourceSha256": source_sha,
                        "corpusSha256": hashlib.sha256(
                            corpus_path.read_bytes()
                        ).hexdigest(),
                    },
                    "results": [record],
                }
            ),
            encoding="utf-8",
        )
        return scenario, report

    def test_cli_preserves_original_input_and_database_in_reproducible_output(self):
        scenario, report = self.cli_input()
        before = self.database.read_bytes()

        with redirect_stdout(io.StringIO()):
            exit_code = self.probe.main(self.arguments(report))

        self.assertEqual(0, exit_code)
        output = json.loads(
            (self.folder / "output/independent-probe.json").read_text(encoding="utf-8")
        )
        self.assertEqual([scenario["id"]], [case["id"] for case in output["cases"]])
        self.assertEqual(scenario, output["cases"][0]["originalScenario"])
        self.assertEqual("no_route", output["cases"][0]["originalStatus"])
        self.assertEqual("valid", output["cases"][0]["audit"]["status"])
        self.assertIn("provenanceRevalidated", output)
        self.assertTrue(output["provenanceRevalidated"]["unchanged"])
        self.assertEqual(
            {
                "database",
                "corpus",
                "runReport",
                "manifest",
                "packageReceipt",
                "probe",
                "routingAuditor",
                "overlayAuditor",
                "coverage",
            },
            set(output["provenance"]["evidenceHashes"]),
        )
        self.assertEqual(before, self.database.read_bytes())

    def test_rerun_removes_only_its_stale_witness_when_the_case_becomes_unresolved(
        self,
    ):
        scenario, report = self.cli_input()
        with redirect_stdout(io.StringIO()):
            self.assertEqual(0, self.probe.main(self.arguments(report)))
        output = self.folder / "output"
        witness = output / f"{scenario['id']}-witness.json"
        self.assertTrue(witness.is_file())
        unrelated = output / "unselected-witness.json"
        unrelated.write_text("retain unselected evidence", encoding="utf-8")
        _, south, _, north = scenario["auditBounds"]
        longitude = sum(point["Longitude"] for point in scenario["points"]) / 2
        self.add_feature(
            99, "Road", LineString([(longitude, south), (longitude, north)])
        )
        self.cli_input()

        with redirect_stdout(io.StringIO()):
            self.assertEqual(0, self.probe.main(self.arguments(report)))

        result = json.loads(
            (output / "independent-probe.json").read_text(encoding="utf-8")
        )
        self.assertEqual("unresolved", result["cases"][0]["status"])
        self.assertFalse(witness.exists())
        self.assertEqual(
            "retain unselected evidence", unrelated.read_text(encoding="utf-8")
        )

    def test_changed_inputs_fail_revalidation_without_publishing_a_witness(self):
        original_probe = self.probe.probe_case
        for changed_input in ("runReport", "database", "packageReceipt"):
            with self.subTest(changed_input=changed_input):
                scenario, report = self.cli_input()

                def change_after_probe(
                    *args, changed_input=changed_input, report=report, **kwargs
                ):
                    result = original_probe(*args, **kwargs)
                    if changed_input == "database":
                        self.add_feature(
                            51, "Road", LineString([coordinate(1, 1), coordinate(2, 2)])
                        )
                    else:
                        path = (
                            report
                            if changed_input == "runReport"
                            else self.folder / "package.json"
                        )
                        path.write_bytes(path.read_bytes() + b"\n")
                    return result

                with (
                    redirect_stdout(io.StringIO()),
                    patch.object(
                        self.probe, "probe_case", side_effect=change_after_probe
                    ),
                ):
                    exit_code = self.probe.main(self.arguments(report))

                self.assertEqual(1, exit_code)
                output = self.folder / "output"
                result = json.loads(
                    (output / "independent-probe.json").read_text(encoding="utf-8")
                )
                self.assertEqual("error", result["status"])
                self.assertEqual("input_changed", result["fatalError"]["reason"])
                self.assertFalse(result["provenanceRevalidated"]["unchanged"])
                self.assertNotIn("alternative", result["cases"][0])
                self.assertFalse((output / f"{scenario['id']}-witness.json").exists())
