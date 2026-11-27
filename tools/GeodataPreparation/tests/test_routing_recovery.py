"""Known source witnesses gate head routes without rewriting the frozen corpus."""

import copy
import gzip
import hashlib
import importlib
import io
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing, redirect_stdout
from pathlib import Path
from unittest.mock import patch

from shapely.geometry import Polygon

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
IDS = (
    "mass-naliboki-bridge-02",
    "mass-gomel-bridge-01",
    "mass-gomel-bridge-03",
    "mass-gomel-bridge-05",
    "mass-naliboki-multi-06",
    "mass-smolevichi-no_candidate-11",
    "mass-pukhovichi-multi-07",
)


def digest_bytes(value):
    return hashlib.sha256(value).hexdigest()


def canonical(value):
    return digest_bytes(
        json.dumps(
            value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode()
    )


class RoutingRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.recovery = importlib.import_module("routing_recovery")
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.data = self.folder / "data"
        self.data.mkdir()
        self.database = self.data / "geodata.sqlite"
        with closing(sqlite3.connect(self.database)) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
                "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                "CREATE TABLE underpass_roads(candidate_id,road_id);"
                "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
            )
        self.db_hash = digest_bytes(self.database.read_bytes())
        self.source_hash = "a" * 64
        (self.data / "manifest.json").write_text(
            json.dumps(
                {
                    "schema_version": 3,
                    "database_sha256": self.db_hash,
                    "sources": [{"sha256": self.source_hash}],
                }
            ),
            encoding="utf-8",
        )
        self.points = [
            {"Longitude": 27, "Latitude": 54},
            {"Longitude": 27.0001, "Latitude": 54},
        ]
        scenarios = [
            {
                "id": case_id,
                "points": self.points,
                "routingOptions": {"MaxRouteLengthMetres": 1000},
            }
            for case_id in IDS
        ]
        self.corpus = self.folder / "corpus.json"
        self.corpus.write_text(json.dumps(scenarios), encoding="utf-8")
        self.coverage = self.folder / "coverage.json"
        self.coverage.write_text(
            json.dumps([[26, 53], [28, 53], [28, 55], [26, 55]]), encoding="utf-8"
        )
        records = []
        for scenario in scenarios:
            relative = f"fixtures/{scenario['id']}.json.gz"
            fixture = {
                "originalScenario": scenario,
                "independentWitness": {"alternative": {"geometry": self.points}},
            }
            raw = json.dumps(fixture).encode()
            path = self.folder / relative
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(gzip.compress(raw, mtime=0))
            records.append(
                {
                    "id": scenario["id"],
                    "geometry": self.points,
                    "originalScenarioSha256": canonical(scenario),
                    "fixture": relative,
                    "fixtureSha256": digest_bytes(path.read_bytes()),
                    "fixtureJsonSha256": digest_bytes(raw),
                    "stableSourceIds": [],
                    "sourceQueryBounds": [26, 53, 28, 55],
                }
            )
        self.catalogue = self.folder / "witnesses.json"
        self.catalogue.write_text(
            json.dumps(
                {
                    "schema": "maps.routing-recovery-witnesses.v1",
                    "corpusSha256": digest_bytes(self.corpus.read_bytes()),
                    "sourceSha256": self.source_hash,
                    "records": records,
                }
            ),
            encoding="utf-8",
        )
        self.results = self.folder / "head/results.json"
        self.results.parent.mkdir()
        results = []
        for scenario in scenarios:
            folder = self.results.parent / "frozen" / scenario["id"]
            folder.mkdir(parents=True)
            original, invocation = (
                folder / "original-scenario.json",
                folder / "scenario.json",
            )
            original.write_text(json.dumps(scenario), encoding="utf-8")
            invocation.write_text(
                json.dumps(scenario | {"includeDiagnostics": True}), encoding="utf-8"
            )
            diagnostic = {
                "droppedEvents": 0,
                "events": [
                    {
                        "eventId": 5,
                        "fields": {
                            "CalculationId": "same",
                            "Attempt": 0,
                            "West": 26,
                            "South": 53,
                            "East": 28,
                            "North": 55,
                            "CellSizeMetres": 25,
                            "RequestedCellSizeMetres": 25,
                            "NodeCount": 4,
                            "Columns": 2,
                            "Rows": 2,
                            "PortalCount": 0,
                        },
                    },
                    {
                        "eventId": 6,
                        "fields": {
                            "CalculationId": "same",
                            "Profile": "test",
                            "LegIndex": 0,
                            "StartConnections": 1,
                            "DestinationConnections": 1,
                            "PortalNodeCount": 0,
                            "PortalGridConnections": 0,
                            "PortalEdges": 0,
                            "RejectedConnections": 0,
                            "DirectConnection": True,
                        },
                    },
                    {
                        "eventId": 7,
                        "fields": {
                            "CalculationId": "same",
                            "Profile": "test",
                            "LegIndex": 0,
                            "ExpandedNodes": 1,
                            "EdgeEvaluations": 1,
                            "Reason": None,
                            "Cancelled": False,
                            "DirectRoute": True,
                        },
                    },
                ],
            }
            raw = {
                "status": "success",
                "alternatives": [{"geometry": self.points}],
                "diagnostics": diagnostic,
            }
            (folder / "result.json").write_text(json.dumps(raw), encoding="utf-8")
            results.append(
                {
                    "id": scenario["id"],
                    "group": "frozen",
                    "status": "success",
                    "auditStatus": "valid",
                    "effectiveUnderpasses": False,
                    "gatePassed": True,
                    "diagnostics": diagnostic,
                    "process": {"returncode": 0, "timeout": False},
                    "rawResult": f"frozen/{scenario['id']}/result.json",
                    "originalScenarioSha256": digest_bytes(original.read_bytes()),
                    "invocationSha256": digest_bytes(invocation.read_bytes()),
                }
            )
        self.head = {
            "provenanceRevalidated": {"unchanged": True},
            "provenance": {
                "databaseSha256": self.db_hash,
                "sourceSha256": self.source_hash,
                "corpusSha256": digest_bytes(self.corpus.read_bytes()),
                "effectiveDefaultUnderpasses": False,
                "knownWitnessCatalogueSha256": digest_bytes(
                    self.catalogue.read_bytes()
                ),
            },
            "results": results,
        }

    def invoke(self):
        self.results.write_text(json.dumps(self.head), encoding="utf-8")
        output = self.folder / "output"
        with redirect_stdout(io.StringIO()):
            code = self.recovery.main(
                [
                    "--data",
                    str(self.data),
                    "--corpus",
                    str(self.corpus),
                    "--coverage",
                    str(self.coverage),
                    "--catalogue",
                    str(self.catalogue),
                    "--run-report",
                    str(self.results),
                    "--output",
                    str(output),
                ],
                root=self.folder,
            )
        return code, json.loads((output / "results.json").read_text(encoding="utf-8"))

    def test_seven_full_database_witnesses_and_raw_head_routes_pass_without_baseline_success(
        self,
    ):
        before = self.database.read_bytes()
        self.head["baselineResults"] = [
            {"id": case_id, "status": "no_route"} for case_id in IDS
        ]

        code, report = self.invoke()

        self.assertEqual(code, 0)
        self.assertTrue(report["gatePassed"])
        self.assertEqual(len(report["records"]), 7)
        self.assertTrue(
            all(
                record["witnessAudit"]["status"] == "valid"
                for record in report["records"]
            )
        )
        self.assertEqual(self.database.read_bytes(), before)
        self.assertTrue((self.folder / "output/summary.md").is_file())

    def test_known_valid_witness_head_no_route_is_persisted_as_confirmed_miss(self):
        record = self.head["results"][0]
        record.update(
            status="no_route",
            auditStatus="unknown",
            gatePassed=False,
            failureReason="known_positive_missing",
        )
        raw_path = self.results.parent / record["rawResult"]
        raw = json.loads(raw_path.read_text())
        raw.update(
            status="no_route",
            alternatives=[],
            error={
                "reason": "NoRouteInSearchArea",
                "searchFailures": [
                    {"profile": "test", "reason": "NoRouteInSearchArea"}
                ],
            },
        )
        raw["diagnostics"]["events"][-1]["fields"].update(
            Reason="NoRouteInSearchArea", DirectRoute=False
        )
        record["diagnostics"] = copy.deepcopy(raw["diagnostics"])
        raw_path.write_text(json.dumps(raw))

        code, report = self.invoke()

        self.assertEqual(code, 1)
        self.assertEqual(report["missedWitnessIds"], [IDS[0]])
        self.assertIn(
            "missed_valid_path", {failure["reason"] for failure in report["failures"]}
        )

    def test_timeout_crash_invalid_raw_route_and_stale_catalogue_never_pass(self):
        for mutation in (
            "timeout",
            "crash",
            "raw_route",
            "catalogue",
            "fixture",
            "flag",
        ):
            with self.subTest(mutation=mutation):
                self.setUp()
                record = self.head["results"][0]
                if mutation == "timeout":
                    record["process"]["timeout"] = True
                elif mutation == "crash":
                    record["process"]["returncode"] = 1
                elif mutation == "raw_route":
                    path = self.results.parent / record["rawResult"]
                    raw = json.loads(path.read_text())
                    raw["alternatives"][0]["geometry"][1] = {
                        "Longitude": 27.1,
                        "Latitude": 54,
                    }
                    path.write_text(json.dumps(raw))
                elif mutation == "catalogue":
                    self.head["provenance"]["knownWitnessCatalogueSha256"] = "stale"
                elif mutation == "fixture":
                    (self.folder / f"fixtures/{IDS[0]}.json.gz").write_bytes(b"changed")
                else:
                    record["effectiveUnderpasses"] = True
                code, report = self.invoke()
                self.assertEqual(code, 1)
                self.assertFalse(report["gatePassed"])
                self.assertTrue(report["failures"])

    def test_witness_is_reaudited_against_all_full_database_obstacles(self):
        obstacle = Polygon(
            [
                (26.99999, 53.99999),
                (27.00011, 53.99999),
                (27.00011, 54.00001),
                (26.99999, 54.00001),
            ]
        )
        with closing(sqlite3.connect(self.database)) as connection:
            connection.execute(
                "INSERT INTO features VALUES(1,'Building',?,'way/1',NULL)",
                (obstacle.wkb,),
            )
            connection.execute(
                "INSERT INTO feature_index VALUES(1,26.99999,27.00011,53.99999,54.00001)"
            )
            connection.commit()
        checksum = digest_bytes(self.database.read_bytes())
        manifest = json.loads((self.data / "manifest.json").read_text())
        manifest["database_sha256"] = checksum
        (self.data / "manifest.json").write_text(json.dumps(manifest))
        self.head["provenance"]["databaseSha256"] = checksum

        code, report = self.invoke()

        self.assertEqual(code, 1)
        self.assertEqual(len(report["records"]), 7)
        self.assertTrue(
            all(
                record["witnessAudit"]["status"] == "violation"
                for record in report["records"]
            )
        )
        self.assertEqual(
            {failure["reason"] for failure in report["failures"]}, {"witness_not_valid"}
        )

    def test_post_audit_input_mutation_is_reported_as_provenance_failure(self):
        audit = self.recovery.audit_alternative

        def changing_input(*args, **kwargs):
            self.catalogue.write_text(self.catalogue.read_text() + " ")
            return audit(*args, **kwargs)

        with patch.object(
            self.recovery, "audit_alternative", side_effect=changing_input
        ):
            code, report = self.invoke()

        self.assertEqual(code, 1)
        self.assertFalse(report["gatePassed"])
        self.assertIn(
            "provenance_or_input_failure",
            {failure["reason"] for failure in report["failures"]},
        )

    def test_unvalidated_head_snapshot_cannot_pass_known_witness_gate(self):
        self.head["provenanceRevalidated"] = {"unchanged": False}

        code, report = self.invoke()

        self.assertEqual(code, 1)
        self.assertFalse(report["gatePassed"])
        self.assertTrue(report["failures"])


if __name__ == "__main__":
    unittest.main()
