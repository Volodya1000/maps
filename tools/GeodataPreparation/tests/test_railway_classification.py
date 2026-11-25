import hashlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from shapely import from_wkb

from tools.GeodataPreparation.feature_classification import classify


class RailwayClassificationTests(unittest.TestCase):
    def test_active_tracks_include_bridges_and_tunnels(self):
        for railway in ("rail", "light_rail", "narrow_gauge", "tram"):
            for structure, expected in (
                ({}, "Railway"),
                ({"bridge": "yes"}, "RailwayBridge"),
                ({"tunnel": "yes"}, "RailwayTunnel"),
            ):
                with self.subTest(railway=railway, structure=structure):
                    self.assertEqual(
                        expected, classify({"railway": railway, **structure})
                    )

    def test_inactive_tracks_do_not_enter_map(self):
        for railway in ("abandoned", "disused", "construction", "proposed"):
            self.assertIsNone(classify({"railway": railway}))

    def test_highway_and_railway_tag_keeps_road_restrictions(self):
        self.assertEqual("Road", classify({"railway": "rail", "highway": "primary"}))

    def test_bounded_osm_import_retains_parallel_bridge_and_tunnel_tracks(self):
        osm = """<osm version="0.6">
        <node id="1" lon="27" lat="54"/><node id="2" lon="27" lat="54.01"/>
        <node id="3" lon="27.001" lat="54"/><node id="4" lon="27.001" lat="54.01"/>
        <way id="1"><nd ref="1"/><nd ref="2"/><tag k="railway" v="rail"/><tag k="bridge" v="yes"/></way>
        <way id="2"><nd ref="3"/><nd ref="4"/><tag k="railway" v="rail"/><tag k="tunnel" v="yes"/></way>
        </osm>"""
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.osm"
            source.write_text(osm)
            output = Path(directory) / "data"
            subprocess.run(
                [
                    sys.executable,
                    str(Path(__file__).resolve().parents[1] / "prepare.py"),
                    "--osm",
                    str(source),
                    "--bounds",
                    "26.99",
                    "53.99",
                    "27.02",
                    "54.02",
                    "--output",
                    str(output),
                    "--expected-sha256",
                    hashlib.sha256(source.read_bytes()).hexdigest(),
                ],
                check=True,
                capture_output=True,
            )
            with closing(sqlite3.connect(output / "geodata.sqlite")) as connection:
                tracks = connection.execute(
                    "SELECT kind,wkb FROM features ORDER BY source_id"
                ).fetchall()
            self.assertEqual(
                ["RailwayBridge", "RailwayTunnel"], [kind for kind, _ in tracks]
            )
            self.assertEqual(
                [27, 27.001], [from_wkb(raw).coords[0][0] for _, raw in tracks]
            )
