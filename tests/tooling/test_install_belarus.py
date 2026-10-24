"""Package installation uses tiny synthetic archives; real country probes are separate."""

import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location("install_belarus", Path(__file__).resolve().parents[2] / "tools/install_belarus.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class CountryInstallation(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.apk = self.root / "synthetic.apk"
        self.destination = self.root / "country"
        self.data = b"synthetic SQLite payload for installer integrity tests"

    def archive(self, *, database_checksum=None, region="belarus", licenses=True):
        manifest = {"region_id": region, "schema_version": 2, "coordinate_system": "EPSG:4326",
                    "database_sha256": database_checksum or hashlib.sha256(self.data).hexdigest()}
        with zipfile.ZipFile(self.apk, "w") as archive:
            archive.writestr("assets/Data/manifest.json", json.dumps(manifest))
            archive.writestr("assets/Data/geodata.sqlite.gz", gzip.compress(self.data))
            for name in installer.LICENSE_FILES if licenses else ():
                archive.writestr(f"assets/Data/{name}", "License notice")
            archive.writestr("assets/Data/../../outside.txt", "Must never be extracted")
        return installer.digest(self.apk)

    def test_installs_verified_data_and_licenses_without_zip_path_traversal(self):
        expected = self.archive()
        installer.install(self.apk, self.destination, expected)
        self.assertEqual(self.data, (self.destination / "geodata.sqlite").read_bytes())
        self.assertTrue((self.destination / "licenses/ODbL-1.0.html").is_file())
        self.assertFalse((self.root / "outside.txt").exists())
        self.assertEqual(self.destination, installer.install(self.apk, self.destination, expected))

    def test_apk_and_database_corruption_never_publish_partial_package(self):
        expected = self.archive(database_checksum="0" * 64)
        with self.assertRaises(ValueError):
            installer.install(self.apk, self.destination, expected)
        self.assertFalse(self.destination.exists())
        with self.assertRaises(ValueError):
            installer.install(self.apk, self.destination, "0" * 64)
        self.assertFalse(self.destination.exists())
        self.assertEqual([], list(self.root.glob(".belarus-*")))

    def test_wrong_region_is_rejected_and_existing_directory_is_preserved(self):
        expected = self.archive(region="sample-only")
        with self.assertRaises(ValueError):
            installer.install(self.apk, self.destination, expected)
        expected = self.archive()
        self.destination.mkdir()
        marker = self.destination / "user-file.txt"
        marker.write_text("Keep me", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            installer.install(self.apk, self.destination, expected)
        self.assertEqual("Keep me", marker.read_text(encoding="utf-8"))

    def test_missing_license_does_not_publish_a_package(self):
        expected = self.archive(licenses=False)
        with self.assertRaises(KeyError):
            installer.install(self.apk, self.destination, expected)
        self.assertFalse(self.destination.exists())

    def test_existing_untrusted_manifest_does_not_replace_verified_map_identity(self):
        expected = self.archive()
        installer.install(self.apk, self.destination, expected)
        database = self.destination / "geodata.sqlite"
        database.write_bytes(b"different local data")
        manifest_path = self.destination / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["database_sha256"] = installer.digest(database)
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(FileExistsError):
            installer.install(self.apk, self.destination, expected)
        self.assertEqual(b"different local data", database.read_bytes())


if __name__ == "__main__":
    unittest.main()
