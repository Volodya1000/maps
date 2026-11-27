"""Planned highways must not become existing road barriers or bridge openings."""

import hashlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from tools.GeodataPreparation.feature_classification import classify

ROOT = Path(__file__).resolve().parents[3]


class HighwayLifecycleTests(unittest.TestCase):
    def test_proposed_highways_do_not_create_roads_or_bridge_openings(self):
        for tags in (
            {"highway": "proposed", "proposed": "trunk_link"},
            {"highway": "proposed", "proposed": "primary", "bridge": "yes"},
            {"proposed:highway": "service"},
        ):
            with self.subTest(tags=tags):
                self.assertIsNone(classify(tags))

    def test_existing_roads_keep_crossing_restrictions(self):
        for highway in ("track", "service", "unclassified", "primary", "construction"):
            with self.subTest(highway=highway):
                self.assertEqual("Road", classify({"highway": highway}))
        self.assertEqual(
            "BridgeCandidate", classify({"highway": "primary", "bridge": "yes"})
        )

    def test_planned_highway_does_not_hide_independent_physical_features(self):
        for physical, expected in (
            ({"natural": "water"}, "Lake"),
            ({"building": "yes"}, "Building"),
            ({"natural": "wood"}, "Forest"),
            ({"railway": "rail"}, "Railway"),
        ):
            with self.subTest(expected=expected):
                self.assertEqual(
                    expected, classify({"highway": "proposed", **physical})
                )

    def test_both_importers_exclude_planned_barriers_and_keep_real_roads(self):
        osm = """<osm version="0.6">
        <bounds minlon="26" minlat="53" maxlon="28" maxlat="55"/>
        <node id="1" version="1" lon="27" lat="54"/>
        <node id="2" version="1" lon="27.001" lat="54.001"/>
        <way id="1" version="1"><nd ref="1"/><nd ref="2"/>
          <tag k="highway" v="proposed"/><tag k="proposed" v="trunk_link"/></way>
        <way id="2" version="1"><nd ref="1"/><nd ref="2"/>
          <tag k="highway" v="proposed"/><tag k="bridge" v="yes"/></way>
        <way id="3" version="1"><nd ref="1"/><nd ref="2"/>
          <tag k="highway" v="track"/></way>
        </osm>"""
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "lifecycle.osm"
            source.write_text(osm, encoding="utf-8")
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            for importer in ("prepare.py", "prepare_country.py"):
                with self.subTest(importer=importer):
                    output = folder / importer.removesuffix(".py")
                    command = [
                        sys.executable,
                        str(ROOT / "tools/GeodataPreparation" / importer),
                        "--osm" if importer == "prepare.py" else "--pbf",
                        str(source),
                        "--expected-sha256",
                        digest,
                        "--output",
                        str(output),
                    ]
                    if importer == "prepare.py":
                        command += ["--bounds", "26", "53", "28", "55"]
                    result = subprocess.run(
                        command, capture_output=True, timeout=60, check=False
                    )
                    self.assertEqual(0, result.returncode, result.stderr.decode())
                    with closing(sqlite3.connect(output / "geodata.sqlite")) as db:
                        self.assertEqual(
                            [("Road", "way/3")],
                            db.execute(
                                "SELECT kind,source_id FROM features ORDER BY source_id"
                            ).fetchall(),
                        )
