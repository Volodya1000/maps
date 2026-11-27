"""Verify the bytes users download, independently of the publish directory."""

import gzip
import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.verify_offline_demo import verify_archive


class OfflineDemoTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.reference = self.root / "reference"
        self.reference.mkdir()
        self.database = b"fixture database bytes"
        self.manifest = {
            "region_id": "belarus",
            "schema_version": 3,
            "coordinate_system": "EPSG:4326",
            "database_sha256": hashlib.sha256(self.database).hexdigest(),
        }
        self.files = {
            "manifest.json": json.dumps(self.manifest).encode(),
            "NOTICE.txt": b"OSM notice",
            "licenses/NOTICE.txt": b"OSM notice",
            "licenses/ODbL-1.0.html": b"ODbL",
            "licenses/DbCL-1.0.html": b"DbCL",
            "elevation/index.json": b'{"schema_version":1}',
            "elevation/NOTICE.txt": b"Copernicus notice",
            "elevation/tile/elevation.sqlite": b"height data",
            "elevation/tile/elevation.sqlite.sha256": b"height checksum",
        }
        for name, content in self.files.items():
            target = self.reference / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        # Producer bookkeeping is deliberately not an Android application asset.
        (self.reference / "package.json").write_text('{"receipt":true}')

    def archive(self, windows, changes=None):
        prefix = "Data/" if windows else "assets/Data/"
        files = {prefix + name: content for name, content in self.files.items()}
        if windows:
            files.update(
                {
                    "CableRoutePlanner.Desktop.exe": b"apphost",
                    "hostfxr.dll": b"host",
                    "coreclr.dll": b"runtime",
                    "Data/geodata.sqlite": self.database,
                }
            )
        else:
            files[prefix + "geodata.0000.gz"] = gzip.compress(self.database[:10])
            files[prefix + "geodata.0001.gz"] = gzip.compress(self.database[10:])
        for name, content in (changes or {}).items():
            if content is None:
                del files[name]
            else:
                files[name] = content
        path = self.root / ("app.zip" if windows else "app.apk")
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in files.items():
                archive.writestr(name, content)
        return path

    def test_exact_windows_and_chunked_android_data_are_extracted(self):
        for windows in (True, False):
            with self.subTest(windows=windows):
                destination = self.root / str(windows)
                verify_archive(
                    self.archive(windows), self.reference, destination, windows=windows
                )
                self.assertEqual(
                    self.database, (destination / "Data/geodata.sqlite").read_bytes()
                )
                for name, content in self.files.items():
                    self.assertEqual(
                        content, (destination / "Data" / name).read_bytes()
                    )
                if windows:
                    self.assertTrue(
                        (destination / "CableRoutePlanner.Desktop.exe").is_file()
                    )

    def test_minsk_reference_cannot_authorize_a_demo(self):
        self.manifest["region_id"] = "minsk"
        content = json.dumps(self.manifest).encode()
        (self.reference / "manifest.json").write_bytes(content)
        with self.assertRaises(ValueError):
            verify_archive(
                self.archive(True, {"Data/manifest.json": content}),
                self.reference,
                self.root / "unpacked",
                windows=True,
            )

    def test_altered_or_missing_final_package_data_is_rejected(self):
        for windows in (True, False):
            prefix = "Data/" if windows else "assets/Data/"
            corruptions = [
                {prefix + "manifest.json": b'{"region_id":"minsk"}'},
                {prefix + "licenses/ODbL-1.0.html": None},
                {prefix + "elevation/tile/elevation.sqlite": b"changed"},
                {prefix + "elevation/NOTICE.txt": None},
                {"../escaped.txt": b"unsafe"},
                {"./" + prefix + "licenses/NOTICE.txt": b"alias overwrite"},
            ]
            if windows:
                corruptions += [
                    {"Data/geodata.sqlite": b"changed"},
                    {"hostfxr.dll": None},
                    {"data/licenses/notice.TXT": b"case alias overwrite"},
                ]
            else:
                corruptions += [
                    {prefix + "geodata.0001.gz": gzip.compress(b"changed")},
                    {prefix + "geodata.0000.gz": None},
                ]
            for index, changes in enumerate(corruptions):
                with self.subTest(windows=windows, corruption=index):
                    with self.assertRaises((ValueError, KeyError)):
                        verify_archive(
                            self.archive(windows, changes),
                            self.reference,
                            self.root / f"bad-{windows}-{index}",
                            windows=windows,
                        )
        self.assertFalse((self.root / "escaped.txt").exists())
