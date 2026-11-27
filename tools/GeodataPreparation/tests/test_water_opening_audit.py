"""Independent water audits accept complete coverage by multiple valid openings."""

import sys
import unittest
from pathlib import Path

from shapely.geometry import LineString, box

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from routing_crossing_pilot import audit


class WaterOpeningAuditTests(unittest.TestCase):
    def test_two_crossings_of_one_water_body_can_use_two_mapped_bridges(self):
        route = LineString([(0, -5), (0, 15), (20, 15), (20, -5)])
        features = self.features()

        checks = audit(route, features)

        self.assertEqual("valid", checks["status"])
        self.assertNotIn(
            "water_without_opening",
            [finding["reason"] for finding in checks["findings"]],
        )

    def test_an_uncovered_second_water_crossing_remains_forbidden(self):
        route = LineString([(0, -5), (0, 15), (20, 15), (20, -5)])

        checks = audit(route, self.features()[:-1])

        self.assertEqual("violation", checks["status"])
        self.assertIn(
            {"reason": "water_without_opening", "featureId": 1}, checks["findings"]
        )

    @staticmethod
    def features():
        return [
            {"id": 1, "kind": "Lake", "geometry": box(-10, 0, 30, 10)},
            {
                "id": 2,
                "kind": "BridgeCandidate",
                "geometry": LineString([(0, -2), (0, 12)]),
            },
            {
                "id": 3,
                "kind": "BridgeCandidate",
                "geometry": LineString([(20, -2), (20, 12)]),
            },
        ]
