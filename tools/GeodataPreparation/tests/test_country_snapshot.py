"""Exercise the real country converter on a bounded local OSM fixture."""

import gzip
import hashlib
import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import osmium

ROOT = Path(__file__).resolve().parents[3]
CONVERTER = ROOT / "tools/GeodataPreparation/prepare_country.py"


class CountrySnapshotTests(unittest.TestCase):
    def test_sha256_pbf_to_sqlite_and_deterministic_gzip(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            pbf = root / "small.osm.pbf"
            header = osmium.io.Header()
            header.add_box(
                osmium.osm.Box(osmium.osm.Location(26, 53), osmium.osm.Location(28, 55))
            )
            with osmium.SimpleWriter(str(pbf), header=header) as writer:
                writer.add_node(
                    osmium.osm.mutable.Node(
                        id=1,
                        location=(27, 54),
                        tags={"place": "village", "name": "Fixture"},
                    )
                )
            digest = hashlib.sha256(pbf.read_bytes()).hexdigest()
            output = root / "country"
            subprocess.run(
                [
                    sys.executable,
                    str(CONVERTER),
                    "--pbf",
                    str(pbf),
                    "--expected-sha256",
                    digest,
                    "--output",
                    str(output),
                    "--compress",
                ],
                check=True,
                capture_output=True,
            )
            manifest = json.loads((output / "manifest.json").read_text())
            self.assertEqual(digest, manifest["sources"][0]["sha256"])
            self.assertEqual({"Settlement": 1}, manifest["feature_counts"])
            database = (output / "geodata.sqlite").read_bytes()
            self.assertEqual(
                database, gzip.decompress((output / "geodata.sqlite.gz").read_bytes())
            )
            self.assertEqual(
                hashlib.sha256(database).hexdigest(), manifest["database_sha256"]
            )
            with sqlite3.connect(output / "geodata.sqlite") as connection:
                self.assertEqual(
                    [("Settlement", "Fixture")],
                    connection.execute("SELECT kind,name FROM features").fetchall(),
                )
            first = (output / "geodata.sqlite.gz").read_bytes()
            subprocess.run(
                [
                    sys.executable,
                    str(CONVERTER),
                    "--pbf",
                    str(pbf),
                    "--expected-sha256",
                    digest,
                    "--output",
                    str(output),
                    "--compress",
                ],
                check=True,
                capture_output=True,
            )
            self.assertEqual(first, (output / "geodata.sqlite.gz").read_bytes())

    def test_incorrect_sha256_rejects_before_creating_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "bad.pbf"
            source.write_bytes(b"truncated input")
            output = root / "country"
            result = subprocess.run(
                [
                    sys.executable,
                    str(CONVERTER),
                    "--pbf",
                    str(source),
                    "--expected-sha256",
                    "0" * 64,
                    "--output",
                    str(output),
                ],
                capture_output=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(output.exists())
