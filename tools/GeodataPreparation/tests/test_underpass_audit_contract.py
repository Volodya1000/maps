"""Independent audit and runtime planner consume the same source-topology cases."""

import copy
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from shapely.geometry import LineString, mapping

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import routing_crossing_pilot as pilot
import routing_mass_experiment as mass
import routing_mass_report as report

FIXTURE = (
    Path(__file__).resolve().parents[3]
    / "tests/CableRoutePlanner.Tests/Fixtures/Underpasses/audit-contract.json"
)


class UnderpassAuditContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = json.loads(FIXTURE.read_text(encoding="utf-8"))

    def check_case(self, identifier):
        case = next(c for c in self.contract["cases"] if c["id"] == identifier)
        raw = [
            {
                "properties": {"featureId": 1, "kind": "Road"},
                "geometry": mapping(LineString(case["road"])),
            }
        ]
        for candidate in case["candidates"]:
            raw.append(
                {
                    "properties": {"featureId": candidate["id"]}
                    | {k: v for k, v in candidate.items() if k != "coordinates"},
                    "geometry": mapping(LineString(candidate["coordinates"])),
                }
            )
        checks = pilot.audit(
            LineString(case["route"]), pilot.audit_features(raw), allow_underpasses=True
        )
        self.assertEqual(checks["status"], case["auditStatus"])
        return case, checks

    def test_connected_split_on_road_axis_uses_confirmed_source_network(self):
        _, checks = self.check_case("connected_split_on_road_axis")
        self.assertEqual(checks["findings"][0]["underpassIds"], [2, 3])

    def test_network_without_confirmed_surface_entrances_is_rejected(self):
        self.check_case("no_confirmed_surface_entrances")

    def test_equal_coordinates_do_not_join_different_source_nodes(self):
        self.check_case("different_source_nodes_at_same_coordinate")

    def test_equal_source_nodes_do_not_join_different_networks(self):
        self.check_case("different_network_at_same_coordinate")

    def test_connected_tunnel_does_not_permit_unrelated_road(self):
        self.check_case("unrelated_road_links")

    def test_gas_crossing_takes_priority_over_overlapping_tunnel(self):
        case, checks = self.check_case("gas_overlaps_connected_tunnel")
        self.assertEqual(
            report.crossing_types_used([checks]), case["crossingTypesUsed"]
        )

    def test_bridge_crossing_takes_priority_over_overlapping_tunnel(self):
        case, checks = self.check_case("bridge_overlaps_connected_tunnel")
        self.assertEqual(
            report.crossing_types_used([checks]), case["crossingTypesUsed"]
        )

    def test_missing_or_malformed_source_topology_stays_unknown(self):
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -20), (0, 20)])}
        for topology in (
            None,
            {},
            {"networkId": True},
            {"networkId": 2, "nodeIds": [10], "entranceNodeIds": [10]},
        ):
            with self.subTest(topology=topology):
                span = {
                    "id": 2,
                    "kind": "PedestrianUnderpassCandidate",
                    "geometry": LineString([(-5, 0), (5, 0)]),
                    "underpass_road_ids": [1],
                    "underpass_topology": topology,
                }
                checks = pilot.audit(
                    LineString([(-10, 0), (10, 0)]),
                    [road, span],
                    allow_underpasses=True,
                )
                self.assertEqual(checks["status"], "unknown")
                self.assertEqual(
                    checks["findings"][0]["missingUnderpassTopologyIds"], [2]
                )
                self.assertEqual(checks["findings"][0]["missingUnderpassLinkIds"], [])

    def test_topology_is_restored_from_sqlite_and_geojson(self):
        topology = {"networkId": 2, "nodeIds": [10, 11], "entranceNodeIds": [10, 11]}
        line = LineString([(-5, 0), (5, 0)])
        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy); CREATE TABLE underpass_roads(candidate_id,road_id); CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
            )
            connection.execute(
                "INSERT INTO features VALUES(2,'PedestrianUnderpassCandidate',?,'way/2',NULL)",
                (line.wkb,),
            )
            connection.execute("INSERT INTO feature_index VALUES(2,-5,5,0,0)")
            connection.execute("INSERT INTO underpass_roads VALUES(2,1)")
            connection.execute(
                "INSERT INTO underpass_topology VALUES(2,2,'[10,11]','[10,11]')"
            )
            feature = pilot.query(connection, (-10, -10, 10, 10))[0]
        self.assertEqual(feature["underpass_topology"], topology)
        raw = [
            {
                "properties": {
                    "featureId": 2,
                    "kind": "PedestrianUnderpassCandidate",
                    "underpassRoadIds": [1],
                    "underpassTopology": copy.deepcopy(topology),
                },
                "geometry": mapping(line),
            }
        ]
        self.assertEqual(pilot.audit_features(raw)[0]["underpass_topology"], topology)

    def test_missing_source_exit_topology_along_passage_is_unknown(self):
        route = LineString([(-10, 0), (10, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -20), (0, 20)])}
        first = {
            "id": 2,
            "kind": "PedestrianUnderpassCandidate",
            "geometry": LineString([(-5, 0), (2, 0)]),
            "underpass_road_ids": [1],
            "underpass_topology": {
                "networkId": 2,
                "nodeIds": [10, 11],
                "entranceNodeIds": [10],
            },
        }
        continuation = {
            "id": 3,
            "kind": "PedestrianUnderpassCandidate",
            "geometry": LineString([(2, 0), (5, 0)]),
            "underpass_road_ids": [1],
        }
        checks = pilot.audit(route, [road, first, continuation], allow_underpasses=True)
        self.assertEqual(checks["status"], "unknown")
        self.assertEqual(checks["findings"][0]["missingUnderpassTopologyIds"], [3])
        continuation["underpass_topology"] = {
            "networkId": 2,
            "nodeIds": [11, 12],
            "entranceNodeIds": [12],
        }
        self.assertEqual(
            pilot.audit(route, [road, first, continuation], allow_underpasses=True)[
                "status"
            ],
            "valid",
        )

    def test_disconnected_missing_topology_cannot_hide_missing_source_exit(self):
        route = LineString([(-10, 0), (10, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -20), (0, 20)])}
        first = {
            "id": 2,
            "kind": "PedestrianUnderpassCandidate",
            "geometry": LineString([(-5, 0), (2, 0)]),
            "underpass_road_ids": [1],
            "underpass_topology": {
                "networkId": 2,
                "nodeIds": [10, 11],
                "entranceNodeIds": [10],
            },
        }
        for coordinates in ([(4, 0), (5, 0)], [(2, 0), (2, 5)], [(20, 0), (25, 0)]):
            with self.subTest(coordinates=coordinates):
                unrelated = {
                    "id": 3,
                    "kind": "PedestrianUnderpassCandidate",
                    "geometry": LineString(coordinates),
                    "underpass_road_ids": [1],
                }
                self.assertEqual(
                    pilot.audit(
                        route, [road, first, unrelated], allow_underpasses=True
                    )["status"],
                    "violation",
                )

    def test_mass_freeze_preserves_source_topology_for_reaudit(self):
        topology = {"networkId": 2, "nodeIds": [10, 11], "entranceNodeIds": [10, 11]}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "data"
            data.mkdir()
            (data / "manifest.json").write_text('{"database_sha256":"fixture"}')
            (data / "belarus-coverage.json").write_text(
                "[[0,0],[10,0],[10,10],[0,10],[0,0]]"
            )
            with closing(sqlite3.connect(data / "geodata.sqlite")):
                pass
            output = root / "output"
            output.mkdir()
            case = {
                "id": "source-topology",
                "points": [
                    {"Longitude": 1, "Latitude": 1},
                    {"Longitude": 9, "Latitude": 1},
                ],
                "targetRoad": {"featureId": 1},
            }
            feature = {
                "id": 2,
                "kind": "PedestrianUnderpassCandidate",
                "source_id": "way/2",
                "approval_id": None,
                "geometry": LineString([(2, 1), (8, 1)]),
                "underpass_road_ids": [1],
                "underpass_topology": topology,
            }
            with (
                patch.object(mass, "ROOT", root),
                patch.object(mass, "WINDOWS", {"test": (0, 0, 10, 10)}),
                patch.object(mass, "generate", return_value=[]),
                patch.object(
                    mass, "pick_cases", return_value=([(case, [feature])], {})
                ),
            ):
                mass.freeze(data, output)
            saved = json.loads((output / case["id"] / "input.geojson").read_text())
            restored = pilot.audit_features(saved["features"])
            self.assertEqual(restored[0].get("underpass_topology"), topology)
