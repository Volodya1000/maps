"""Verify the actual distributable map, independently of the split directory."""

import gzip
import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.check_apk_map import verify


class ApkMapIntegrityTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.apk = self.root / "map.apk"
        self.database = b"SQLite format 3\0" + bytes(range(256)) * 16
        self.expected = hashlib.sha256(self.database).hexdigest()
        self.manifest_bytes = json.dumps({"database_sha256": self.expected}).encode()
        self.manifest = self.root / "manifest.json"
        self.manifest.write_bytes(self.manifest_bytes)

    def package(self, *, changed=False, gap=False, manifest=None, corrupt=False):
        blocks = [self.database[:1024], self.database[1024:]]
        if changed:
            blocks[1] += b"corruption"
        with zipfile.ZipFile(self.apk, "w") as archive:
            archive.writestr(
                "assets/Data/manifest.json",
                self.manifest_bytes if manifest is None else manifest,
            )
            # APK ZIP entry ordering must not determine map concatenation order.
            archive.writestr(
                "assets/Data/geodata.0002.gz" if gap else "assets/Data/geodata.0001.gz",
                gzip.compress(blocks[1]),
            )
            archive.writestr(
                "assets/Data/geodata.0000.gz",
                b"bad gzip" if corrupt else gzip.compress(blocks[0]),
            )

    def test_verifies_sqlite_from_actual_apk_in_canonical_chunk_order(self):
        self.package()
        report = verify(self.apk, self.manifest)
        self.assertEqual(self.expected, report["database_sha256"])
        self.assertEqual(len(self.database), report["database_bytes"])
        self.assertEqual(2, report["chunks"])
        self.assertEqual(self.apk.stat().st_size, report["apk_bytes"])
        self.assertEqual(
            hashlib.sha256(self.apk.read_bytes()).hexdigest(), report["apk_sha256"]
        )

    def test_valid_gzip_with_altered_sqlite_is_rejected(self):
        self.package(changed=True)
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)

    def test_apk_manifest_must_match_verified_source_manifest(self):
        self.package(manifest=b'{"database_sha256":"another package"}')
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)

    def test_missing_chunk_cannot_silently_shorten_the_map(self):
        self.package(gap=True)
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)

    def test_corrupt_gzip_is_rejected(self):
        self.package(corrupt=True)
        with self.assertRaises(gzip.BadGzipFile):
            verify(self.apk, self.manifest)

    def test_duplicate_chunk_is_rejected(self):
        self.package()
        with zipfile.ZipFile(self.apk, "a") as archive:
            with self.assertWarns(UserWarning):
                archive.writestr(
                    "assets/Data/geodata.0000.gz", gzip.compress(b"replaced")
                )
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)

    def test_extra_nonnumeric_gzip_consumed_by_android_is_rejected(self):
        self.package()
        with zipfile.ZipFile(self.apk, "a") as archive:
            archive.writestr(
                "assets/Data/geodata.extra.gz", gzip.compress(b"extra SQLite bytes")
            )
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)

    def test_gzip_named_directory_visible_to_android_is_rejected(self):
        self.package()
        with zipfile.ZipFile(self.apk, "a") as archive:
            archive.writestr("assets/Data/geodata.extra.gz/content", b"not a map chunk")
        with self.assertRaises(ValueError):
            verify(self.apk, self.manifest)
