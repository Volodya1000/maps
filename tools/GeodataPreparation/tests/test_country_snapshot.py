"""Exercise the real country converter on a bounded local OSM fixture."""

import gzip
import hashlib
import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CONVERTER = ROOT / "tools/GeodataPreparation/prepare_country.py"


class CountrySnapshotTests(unittest.TestCase):
    def test_sha256_pbf_to_sqlite_and_deterministic_gzip(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            pbf = root / "small.osm"
            pbf.write_text(
                '<osm version="0.6"><bounds minlon="26" minlat="53" maxlon="28" maxlat="55"/>'
                '<node id="1" version="1" lon="27" lat="54">'
                '<tag k="place" v="village"/><tag k="name" v="Fixture"/>'
                "</node></osm>"
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
                timeout=60,
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
            with closing(sqlite3.connect(output / "geodata.sqlite")) as connection:
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
                timeout=60,
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
