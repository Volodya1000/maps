"""Research water scope proofs use full, independently queried mesh geometry."""

import copy
import importlib
import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from shapely import to_wkb
from shapely.affinity import scale
from shapely.errors import GEOSException
from shapely.geometry import LineString, box

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
witnesses = importlib.import_module("routing_research_witnesses")
METRES_PER_DEGREE = 111195.0802335


class ResearchWaterScopeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.connection = sqlite3.connect(self.directory / "geodata.sqlite")
        self.addCleanup(self.connection.close)
        self.connection.executescript(
            "CREATE TABLE features(id INTEGER PRIMARY KEY,kind,wkb,source_id,approval_id);"
            "CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);"
        )
        self.mesh = {
            "West": -100 / METRES_PER_DEGREE,
            "South": -100 / METRES_PER_DEGREE,
            "East": 100 / METRES_PER_DEGREE,
            "North": 100 / METRES_PER_DEGREE,
        }
        self.record = {
            "diagnostics": {
                "droppedEvents": 0,
                "events": [{"eventId": 5, "fields": self.mesh}],
            }
        }
        self.alternative = {
            "geometry": [
                {"Longitude": 0, "Latitude": y / METRES_PER_DEGREE} for y in (-3, 3)
            ]
        }
        self.checks = {
            "status": "violation",
            "findings": [
                {
                    "reason": "water_without_opening",
                    "featureId": 1,
                    "sourceId": "way/target",
                }
            ],
        }
        self.add(1, "Lake", box(-0.2, 0.9, 0.2, 1.1), "way/target")
        self.add(2, "BridgeCandidate", LineString([(-10, 0), (10, 0)]), "way/bridge")

    def add(self, identifier, kind, geometry, source, approval=None):
        geographic = scale(
            geometry,
            xfact=1 / METRES_PER_DEGREE,
            yfact=1 / METRES_PER_DEGREE,
            origin=(0, 0),
        )
        west, south, east, north = geographic.bounds
        self.connection.execute(
            "INSERT INTO features VALUES(?,?,?,?,?)",
            (identifier, kind, to_wkb(geographic), source, approval),
        )
        self.connection.execute(
            "INSERT INTO feature_index VALUES(?,?,?,?,?)",
            (identifier, west, east, south, north),
        )

    def proofs(self):
        return witnesses.water_audit_scope_proofs(
            self.connection, self.record, self.alternative, self.checks
        )

    def test_neighboring_water_is_covered_by_globally_activated_bridge(self):
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        original = copy.deepcopy(self.checks)
        proofs = self.proofs()
        self.assertEqual(1, len(proofs))
        proof = proofs[0]
        self.assertEqual("auditor_model_rule_mismatch", proof["kind"])
        self.assertEqual(0, proof["findingIndex"])
        self.assertEqual(original["findings"][0], proof["originalFinding"])
        self.assertEqual(
            {"featureId": 1, "sourceId": "way/target", "kind": "Lake"}, proof["water"]
        )
        self.assertEqual("way/bridge", proof["bridges"][0]["sourceId"])
        self.assertEqual(
            "way/activator", proof["bridges"][0]["activatingWaters"][0]["sourceId"]
        )
        self.assertEqual(
            [self.mesh[key] for key in ("West", "South", "East", "North")],
            proof["meshBounds"],
        )
        self.assertEqual([0, 0], proof["projectionOrigin"])
        self.assertTrue(proof["uncoveredEmpty"])
        self.assertGreater(proof["localUncoveredLengthMetres"], 0)
        self.assertEqual(2, proof["bridgeCorridorToleranceMetres"])
        self.assertEqual(original, self.checks)
        proof["originalFinding"]["sourceId"] = "changed"
        self.assertEqual(original, self.checks)
        json.dumps(proof)

    def test_bridge_without_intersecting_water_cannot_open_neighbor(self):
        proof = self.proofs()[0]
        self.assertEqual("independent_water_violation", proof["kind"])
        self.assertFalse(proof["uncoveredEmpty"])
        self.assertGreater(proof["uncoveredLengthMetres"], 0)

    def test_partial_coverage_cannot_prove_entire_water_contact(self):
        self.connection.execute("DELETE FROM features WHERE id=1")
        self.connection.execute("DELETE FROM feature_index WHERE id=1")
        self.add(1, "Lake", box(-0.2, 0.9, 0.2, 2.5), "way/target")
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        proof = self.proofs()[0]
        self.assertEqual("independent_water_violation", proof["kind"])
        self.assertFalse(proof["uncoveredEmpty"])
        self.assertLess(proof["uncoveredLengthMetres"], proof["contactLengthMetres"])

    def test_closed_residual_water_boundary_is_an_independent_violation(self):
        self.connection.execute("DELETE FROM features WHERE id=1")
        self.connection.execute("DELETE FROM feature_index WHERE id=1")
        self.add(1, "Lake", box(-0.2, 1.9, 0.2, 2.1), "way/target")
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        self.alternative["geometry"] = [
            {"Longitude": x / METRES_PER_DEGREE, "Latitude": 2 / METRES_PER_DEGREE}
            for x in (-3, 3)
        ]
        proof = self.proofs()[0]
        self.assertEqual("independent_water_violation", proof["kind"])
        self.assertFalse(proof["uncoveredEmpty"])
        self.assertGreater(proof["uncoveredLengthMetres"], 0)

    def test_activating_water_excluded_from_mesh_query_cannot_activate_bridge(self):
        self.add(3, "River", box(120, -0.2, 121, 0.2), "way/outside")
        self.connection.execute("DELETE FROM features WHERE id=2")
        self.connection.execute("DELETE FROM feature_index WHERE id=2")
        self.add(2, "BridgeCandidate", LineString([(-10, 0), (130, 0)]), "way/bridge")
        proof = self.proofs()[0]
        self.assertEqual("independent_water_violation", proof["kind"])
        self.assertEqual([], proof["bridgeCandidates"][0]["activatingWaters"])

    def test_full_queried_geometries_are_not_clipped_to_mesh(self):
        self.add(
            3,
            "River",
            box(110, -0.2, 120, 0.2).union(box(90, 0.1, 120, 0.2)),
            "way/activator",
        )
        self.connection.execute("DELETE FROM features WHERE id=2")
        self.connection.execute("DELETE FROM feature_index WHERE id=2")
        self.add(2, "BridgeCandidate", LineString([(-10, 0), (115, 0)]), "way/bridge")
        self.assertEqual(1, len(self.proofs()))

    def test_last_validated_mesh_controls_feature_selection(self):
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        first = dict(self.mesh, Attempt=0, CalculationId="run")
        last = dict(
            self.mesh,
            West=-5 / METRES_PER_DEGREE,
            East=5 / METRES_PER_DEGREE,
            Attempt=1,
            CalculationId="run",
        )
        self.record["diagnostics"]["events"] = [
            {"eventId": 5, "fields": first},
            {"eventId": 5, "fields": last},
        ]
        self.assertEqual("independent_water_violation", self.proofs()[0]["kind"])

    def test_missing_malformed_or_incomplete_bounds_fail_closed(self):
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        for snapshot in (
            None,
            {},
            {"droppedEvents": 1, "events": []},
            {"droppedEvents": 0, "events": []},
            {
                "droppedEvents": 0,
                "events": [
                    {"eventId": 5, "fields": dict(self.mesh, West=float("nan"))}
                ],
            },
            {
                "droppedEvents": 0,
                "events": [
                    {"eventId": 5, "fields": dict(self.mesh, East=self.mesh["West"])}
                ],
            },
        ):
            with self.subTest(snapshot=snapshot):
                self.record["diagnostics"] = snapshot
                self.assertEqual([], self.proofs())

    def test_approval_requires_nonempty_identifier_to_cover_remainder(self):
        self.connection.execute("DELETE FROM features WHERE id=1")
        self.connection.execute("DELETE FROM feature_index WHERE id=1")
        self.add(1, "Lake", box(-0.2, 0.9, 0.2, 2.5), "way/target")
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        for approval in (None, "", "   "):
            with self.subTest(approval=approval):
                self.connection.execute("DELETE FROM features WHERE id=4")
                self.connection.execute("DELETE FROM feature_index WHERE id=4")
                self.add(
                    4,
                    "ApprovedCrossing",
                    box(-0.3, 1.8, 0.3, 2.6),
                    "way/approved",
                    approval,
                )
                self.assertEqual(
                    "independent_water_violation", self.proofs()[0]["kind"]
                )
        self.connection.execute("UPDATE features SET approval_id='permit' WHERE id=4")
        self.assertEqual(
            "way/approved", self.proofs()[0]["approvedCrossings"][0]["sourceId"]
        )

    def test_threshold_is_read_strictly_from_current_public_constant(self):
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        source = self.directory / "PreparedTerrainData.cs"
        for declaration in (
            "",
            "private const double BridgeCorridorToleranceMetres = 2;",
            "public const double BridgeCorridorToleranceMetres = 1 + 1;",
            "public const double BridgeCorridorToleranceMetres = -2;",
            "public const double BridgeCorridorToleranceMetres = 2;\npublic const double BridgeCorridorToleranceMetres = 3;",
        ):
            with self.subTest(declaration=declaration):
                source.write_text(declaration)
                with patch.object(witnesses, "WATER_RULE_SOURCE", source):
                    self.assertEqual([], self.proofs())
        with patch.object(
            witnesses, "WATER_RULE_SOURCE", self.directory / "missing.cs"
        ):
            self.assertEqual([], self.proofs())
        source.write_text("public const double BridgeCorridorToleranceMetres = 0.5;")
        with patch.object(witnesses, "WATER_RULE_SOURCE", source):
            self.assertEqual("independent_water_violation", self.proofs()[0]["kind"])
        source.write_text("public const double BridgeCorridorToleranceMetres = 3;")
        with patch.object(witnesses, "WATER_RULE_SOURCE", source):
            self.assertEqual(3, self.proofs()[0]["bridgeCorridorToleranceMetres"])

    def test_database_errors_propagate_for_technical_classification(self):
        self.connection.execute("DROP TABLE feature_index")
        with self.assertRaises(sqlite3.OperationalError):
            self.proofs()

    def test_geometry_operation_errors_propagate_for_technical_classification(self):
        with (
            patch.object(
                witnesses, "project", side_effect=GEOSException("operation failed")
            ),
            self.assertRaises(GEOSException),
        ):
            self.proofs()

    def test_inconsistent_finding_and_outside_mesh_route_cannot_receive_proof(self):
        self.add(3, "River", box(8, -0.2, 9, 0.2), "way/activator")
        self.checks["findings"][0]["sourceId"] = "wrong"
        self.assertEqual([], self.proofs())
        self.checks["findings"][0]["sourceId"] = "way/target"
        self.alternative["geometry"][0]["Latitude"] = 1
        self.assertEqual([], self.proofs())
