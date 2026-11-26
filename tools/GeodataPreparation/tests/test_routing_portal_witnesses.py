"""Portal evidence audits whole witness paths and preserves read-only inputs."""

import importlib
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
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


if __name__ == "__main__":
    unittest.main()
