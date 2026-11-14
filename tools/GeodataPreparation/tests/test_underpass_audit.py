"""Independent audit must reject plausible but unrelated underpass evidence."""

import sqlite3
import sys
import unittest
from pathlib import Path

from shapely.geometry import LineString, Point

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from routing_crossing_pilot import audit, query  # noqa: E402


class UnderpassAuditTests(unittest.TestCase):
    def setUp(self):
        self.route = LineString([(-10, 0), (10, 0)])
        self.road = dict(id=1, kind="Road", geometry=LineString([(0, -20), (0, 20)]))
        self.span = dict(
            id=2,
            kind="PedestrianUnderpassCandidate",
            geometry=LineString([(-5, 0), (5, 0)]),
            underpass_road_ids=[1],
        )

    def check(self, expected, *, enabled=True, route=None, extra=()):
        result = audit(
            route if route is not None else self.route,
            [self.road, self.span, *extra],
            allow_underpasses=enabled,
        )
        self.assertEqual(result["status"], expected)
        return result

    def test_exact_link_and_aligned_contact_are_accepted(self):
        result = self.check("valid")
        self.assertEqual(result["findings"][0]["underpassIds"], [2])

    def test_disabled_flag_does_not_permit_underpass(self):
        self.check("violation", enabled=False)

    def test_geometrically_matching_but_unlinked_road_is_rejected(self):
        self.span["underpass_road_ids"] = [99]
        self.check("violation")

    def test_crossing_point_inside_corridor_but_wrong_local_direction_is_rejected(self):
        self.check("violation", route=LineString([(-10, -10), (10, 10)]))

    def test_offset_contact_is_rejected(self):
        self.check("violation", route=LineString([(-10, 2), (10, 2)]))

    def test_span_touching_road_is_rejected(self):
        self.span["geometry"] = LineString([(-5, 0), (0, 0)])
        self.check("violation")

    def test_other_road_without_link_still_blocks(self):
        self.check("violation", extra=[dict(self.road, id=3)])

    def test_obstacles_are_not_waived_by_underpass(self):
        for kind in ("Building", "Marsh", "River", "Lake"):
            with self.subTest(kind=kind):
                self.check(
                    "violation",
                    extra=[dict(id=3, kind=kind, geometry=Point(0, 0).buffer(2))],
                )

    def test_missing_link_is_rejected(self):
        del self.span["underpass_road_ids"]
        self.check("violation")

    def test_off_axis_turn_after_contact_is_rejected(self):
        self.check("violation", route=LineString([(-10, 0), (0, 0), (10, 10)]))

    def test_turn_at_next_vertex_inside_road_window_is_rejected(self):
        self.check("violation", route=LineString([(-10, 0), (1, 0), (2, 10), (10, 10)]))

    def test_separate_contacts_are_audited_locally(self):
        self.road["geometry"] = LineString([(0, -20), (0, 20), (10, 20), (10, -20)])
        second = dict(self.span, id=3, geometry=LineString([(5, 0), (15, 0)]))
        self.check("valid", route=LineString([(-10, 0), (20, 0)]), extra=[second])

    def test_shared_road_section_cannot_be_certified(self):
        self.check("unknown", route=LineString([(0, -5), (0, 5)]))

    def test_sqlite_query_loads_exact_candidate_links(self):
        with sqlite3.connect(":memory:") as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
                "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                "CREATE TABLE underpass_roads(candidate_id,road_id);"
            )
            connection.execute(
                "INSERT INTO features VALUES(2,?,?,?,NULL)",
                (self.span["kind"], self.span["geometry"].wkb, "way/24422904"),
            )
            connection.execute("INSERT INTO feature_index VALUES(2,-5,5,0,0)")
            connection.executemany(
                "INSERT INTO underpass_roads VALUES(?,?)", [(2, 1), (2, 7), (9, 99)]
            )
            result = query(connection, (-10, -10, 10, 10))
            self.assertEqual(len(result), 1)
            self.assertEqual(result[0]["underpass_road_ids"], [1, 7])


class UnderpassEvidenceAcceptanceTests(unittest.TestCase):
    def test_confirmation_requires_no_route_off_and_valid_real_candidate_on(self):
        from routing_underpass_study import confirmed

        off = dict(status="no_route", alternatives=[], audits=[])
        on = dict(
            status="success",
            alternatives=[{}],
            audits=[dict(status="valid", findings=[dict(underpassIds=[2])])],
        )
        self.assertTrue(confirmed(off, on, 2))
        for status in (
            "timeout",
            "error",
            "resource_limit",
            "invalid_input",
            "success",
        ):
            self.assertFalse(confirmed(off | {"status": status}, on, 2))
        for status in ("unknown", "violation"):
            self.assertFalse(
                confirmed(off, on | {"audits": [dict(status=status, findings=[])]}, 2)
            )
        self.assertFalse(confirmed(off, on, 99))
        self.assertFalse(confirmed(off, on | {"alternatives": []}, 2))


if __name__ == "__main__":
    unittest.main()
