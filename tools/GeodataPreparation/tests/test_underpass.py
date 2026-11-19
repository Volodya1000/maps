import unittest
from contextlib import closing

from shapely.geometry import LineString

from tools.GeodataPreparation.underpass import compatible_road, crossing, is_underpass


class UnderpassTests(unittest.TestCase):
    def test_closed_network_can_cross_the_same_road_twice(self):
        ring = LineString([(130, 130), (170, 130), (170, 170), (130, 170), (130, 130)])
        road = LineString([(-500, 150), (1000, 150)])
        self.assertTrue(crossing(ring, road))
        # Sharing a section is still not proof of a transverse crossing.
        self.assertFalse(crossing(ring, LineString([(100, 130), (200, 130)])))

    def test_network_keeps_internal_ways_and_only_source_confirmed_surface_entrances(
        self,
    ):
        import json
        import sqlite3

        from shapely import to_wkb

        from tools.GeodataPreparation.underpass import (
            create_staging,
            import_candidates,
            stage,
        )

        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript("""
                CREATE TABLE features(id INTEGER PRIMARY KEY,kind,source_id,wkb);
                CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);
            """)
            create_staging(connection)

            def add(kind, geometry, tags, source):
                cursor = connection.execute(
                    "INSERT INTO features(kind,source_id,wkb) VALUES(?,?,?)",
                    (kind, source, to_wkb(geometry)),
                )
                row = cursor.lastrowid
                w, s, e, n = geometry.bounds
                connection.execute(
                    "INSERT INTO feature_index VALUES(?,?,?,?,?)", (row, w, e, s, n)
                )
                stage(connection, kind, geometry, tags, source, row)
                return row

            add(
                "Road", LineString([(-10, 0), (10, 0)]), {"highway": "primary"}, "way/1"
            )
            tags = {"highway": "footway", "tunnel": "yes", "layer": "-1"}
            for source, points, nodes in (
                ("way/2", [(0, -2), (0, 2)], [10, 11]),
                ("way/3", [(0, 2), (2, 2)], [11, 12]),
                # Identical coordinates without shared OSM identity do not connect.
                ("way/4", [(0, 2), (-2, 2)], [20, 21]),
            ):
                stage(connection, None, LineString(points), tags, source)
                connection.execute(
                    "INSERT INTO underpass_source_nodes VALUES(?,?)",
                    (source, json.dumps(nodes)),
                )
            connection.executemany(
                "INSERT INTO underpass_surface_nodes VALUES(?)", [(10,), (12,), (21,)]
            )
            import_candidates(connection, add)
            rows = connection.execute("""
                SELECT f.source_id,t.network_id,t.node_ids,t.entrance_node_ids
                FROM underpass_topology t JOIN features f ON f.id=t.candidate_id
                ORDER BY f.source_id
            """).fetchall()
            self.assertEqual(["way/2", "way/3"], [row[0] for row in rows])
            self.assertEqual(rows[0][1], rows[1][1])
            self.assertEqual([[10], [12]], [json.loads(row[3]) for row in rows])
            self.assertEqual([[10, 11], [11, 12]], [json.loads(row[2]) for row in rows])

    def test_tags_require_unambiguous_pedestrian_tunnel(self):
        tags = {"highway": "footway", "tunnel": "yes", "layer": "-1"}
        self.assertTrue(is_underpass(tags))
        for change in (
            {"tunnel": "no"},
            {"layer": "0"},
            {"layer": "-1;0"},
            {"bridge": "yes"},
            {"highway": "residential"},
            {"footway": "crossing"},
            {"crossing": "traffic_signals"},
            {"crossing": "marked"},
        ):
            self.assertFalse(is_underpass(tags | change))
        self.assertFalse(is_underpass({"highway": "footway"}))

    def test_road_level_must_be_above_tunnel_and_unambiguous(self):
        self.assertTrue(compatible_road({"highway": "secondary"}, -1))
        for tags in (
            {"highway": "secondary", "layer": "-1"},
            {"highway": "secondary", "layer": "0;1"},
            {"highway": "secondary", "tunnel": "yes"},
            {"highway": "footway"},
        ):
            self.assertFalse(compatible_road(tags, -1))

    def test_crossing_requires_interior_intersection(self):
        tunnel = LineString([(27, 54), (27, 54.001)])
        self.assertTrue(
            crossing(tunnel, LineString([(26.99, 54.0005), (27.01, 54.0005)]))
        )
        self.assertFalse(crossing(tunnel, LineString([(26.99, 54), (27, 54)])))
        self.assertFalse(
            crossing(tunnel, LineString([(26.99, 54.002), (27.01, 54.002)]))
        )

    def test_country_pipeline_links_only_verified_roads(self):
        import hashlib
        import sqlite3
        import subprocess
        import sys
        import tempfile
        from pathlib import Path

        osm = """<osm version="0.6"><bounds minlon="26.9" minlat="53.9" maxlon="27.1" maxlat="54.1"/>
        <node id="1" version="1" lon="27" lat="54"/><node id="2" version="1" lon="27" lat="54.001"/>
        <node id="3" version="1" lon="26.99" lat="54.0005"/><node id="4" version="1" lon="27.01" lat="54.0005"/>
        <way id="1" version="1"><nd ref="1"/><nd ref="2"/><tag k="highway" v="footway"/><tag k="tunnel" v="yes"/><tag k="layer" v="-1"/></way>
        <way id="2" version="1"><nd ref="3"/><nd ref="4"/><tag k="highway" v="secondary"/></way>
        <way id="3" version="1"><nd ref="3"/><nd ref="4"/><tag k="highway" v="secondary"/><tag k="layer" v="-1"/></way>
        <way id="4" version="1"><nd ref="1"/><nd ref="2"/><tag k="highway" v="footway"/></way>
        </osm>"""
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.osm"
            source.write_text(osm)
            output = Path(directory) / "package"
            subprocess.run(
                [
                    sys.executable,
                    str(Path(__file__).resolve().parents[1] / "prepare_country.py"),
                    "--pbf",
                    str(source),
                    "--expected-sha256",
                    hashlib.sha256(source.read_bytes()).hexdigest(),
                    "--output",
                    str(output),
                ],
                check=True,
                capture_output=False,
                timeout=60,
            )
            with closing(sqlite3.connect(output / "geodata.sqlite")) as connection:
                self.assertEqual(
                    [("way/1", "way/2", "PedestrianUnderpassCandidate", "Road")],
                    connection.execute(
                        "SELECT u.tunnel_osm_id,u.road_osm_id,c.kind,r.kind FROM underpass_roads u JOIN features c ON c.id=u.candidate_id JOIN features r ON r.id=u.road_id"
                    ).fetchall(),
                )
