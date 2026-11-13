import unittest

from shapely.geometry import LineString

from tools.GeodataPreparation.underpass import compatible_road, crossing, is_underpass


class UnderpassTests(unittest.TestCase):
    def test_tags_require_unambiguous_pedestrian_tunnel(self):
        tags = {"highway": "footway", "tunnel": "yes", "layer": "-1"}
        self.assertTrue(is_underpass(tags))
        for change in (
            {"tunnel": "no"},
            {"layer": "0"},
            {"layer": "-1;0"},
            {"bridge": "yes"},
            {"highway": "residential"},
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
            )
            with sqlite3.connect(output / "geodata.sqlite") as connection:
                self.assertEqual(
                    [("way/1", "way/2", "PedestrianUnderpassCandidate", "Road")],
                    connection.execute(
                        "SELECT u.tunnel_osm_id,u.road_osm_id,c.kind,r.kind FROM underpass_roads u JOIN features c ON c.id=u.candidate_id JOIN features r ON r.id=u.road_id"
                    ).fetchall(),
                )
