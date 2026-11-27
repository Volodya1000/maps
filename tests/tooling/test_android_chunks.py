import gzip
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.split_android_map import split_package


class AndroidMapChunks(unittest.TestCase):
    def test_country_elevation_catalog_and_shards_survive_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            tile = source / "elevation" / "tile-1"
            tile.mkdir(parents=True)
            (source / "elevation" / "index.json").write_text(
                '{"tiles":[{"directory":"tile-1"}]}'
            )
            (source / "elevation" / "old-generation").mkdir()
            (tile / "elevation.sqlite").write_bytes(b"native heights")
            (tile / "elevation.sqlite.sha256").write_text("tile identity")
            with gzip.open(source / "geodata.sqlite.gz", "wb") as stream:
                stream.write(b"map")
            output = Path(directory) / "output"
            split_package(source, output, chunk_bytes=1024)
            self.assertFalse((output / "elevation" / "old-generation").exists())
            for relative in (
                "index.json",
                "tile-1/elevation.sqlite",
                "tile-1/elevation.sqlite.sha256",
            ):
                self.assertEqual(
                    (source / "elevation" / relative).read_bytes(),
                    (output / "elevation" / relative).read_bytes(),
                )

    def test_chunks_reconstruct_database_in_order_and_preserve_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            database = bytes(range(256)) * 40
            (source / "manifest.json").write_text('{"region_id":"belarus"}')
            with gzip.open(source / "geodata.sqlite.gz", "wb") as stream:
                stream.write(database)
            output = Path(directory) / "output"

            split_package(source, output, chunk_bytes=1024)

            chunks = sorted(output.glob("geodata.*.gz"))
            self.assertGreater(len(chunks), 1)
            self.assertEqual(
                database,
                b"".join(gzip.decompress(path.read_bytes()) for path in chunks),
            )
            self.assertEqual(
                (source / "manifest.json").read_bytes(),
                (output / "manifest.json").read_bytes(),
            )
            with self.assertRaises(ValueError):
                split_package(source, output, chunk_bytes=1024)

    def test_optional_elevation_is_preserved_with_independent_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            (source / "manifest.json").write_text('{"region_id":"unchanged"}')
            with gzip.open(source / "geodata.sqlite.gz", "wb") as stream:
                stream.write(b"map")
            (source / "elevation.sqlite").write_bytes(b"terrain")
            (source / "elevation.sqlite.sha256").write_text("independent-sha256")
            output = Path(directory) / "output"

            split_package(source, output, chunk_bytes=1024)

            self.assertEqual(b"terrain", (output / "elevation.sqlite").read_bytes())
            self.assertEqual(
                "independent-sha256",
                (output / "elevation.sqlite.sha256").read_text(),
            )

    def test_elevation_without_checksum_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            (source / "elevation.sqlite").write_bytes(b"terrain")

            with self.assertRaises(ValueError):
                split_package(source, Path(directory) / "output", chunk_bytes=1024)

    def test_compression_options_preserve_sqlite_and_deterministic_chunks(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            database = bytes(range(256)) * 33
            (source / "geodata.sqlite.gz").write_bytes(gzip.compress(database))
            for level in (1, 3, 6, 9):
                baseline = None
                for workers in (1, 2, 4):
                    with self.subTest(level=level, workers=workers):
                        output = Path(directory) / f"level-{level}-workers-{workers}"
                        split_package(
                            source,
                            output,
                            chunk_bytes=1024,
                            compresslevel=level,
                            workers=workers,
                        )
                        chunks = sorted(output.glob("geodata.*.gz"))
                        self.assertEqual(
                            [f"geodata.{i:04d}.gz" for i in range(9)],
                            [p.name for p in chunks],
                        )
                        compressed = [p.read_bytes() for p in chunks]
                        self.assertEqual(
                            database, b"".join(gzip.decompress(b) for b in compressed)
                        )
                        self.assertTrue(
                            all(len(gzip.decompress(b)) <= 1024 for b in compressed)
                        )
                        if baseline is None:
                            baseline = compressed
                        else:
                            self.assertEqual(baseline, compressed)

    def test_invalid_compression_controls_leave_destination_absent(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            for options in (
                {"compresslevel": 0},
                {"compresslevel": 10},
                {"workers": 0},
                {"workers": 5},
                {"chunk_bytes": 0},
            ):
                with self.subTest(options=options):
                    output = Path(directory) / "output"
                    with self.assertRaises(ValueError):
                        split_package(source, output, **options)
                    self.assertFalse(output.exists())

    def test_corrupt_source_is_not_reported_as_success_with_workers(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            (source / "geodata.sqlite.gz").write_bytes(b"invalid gzip")
            with self.assertRaises(gzip.BadGzipFile):
                split_package(source, Path(directory) / "output", workers=2)

    def test_benchmark_reports_verified_cases_and_preserves_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            database = bytes(range(256)) * 1000
            expected = hashlib.sha256(database).hexdigest()
            packed = gzip.compress(database)
            (source / "geodata.sqlite.gz").write_bytes(packed)
            (source / "manifest.json").write_text(
                json.dumps({"database_sha256": expected})
            )
            report = root / "report"
            result = subprocess.run(
                [
                    sys.executable,
                    "tools/benchmark_android_map.py",
                    str(source),
                    str(report),
                    "--repeats",
                    "1",
                    "--levels",
                    "1",
                    "9",
                    "--workers",
                    "1",
                    "2",
                ],
                capture_output=True,
                text=True,
                check=False,
                timeout=30,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            records = json.loads((report / "results.json").read_text())["cases"]
            self.assertEqual(4, len(records))
            self.assertEqual(
                {(1, 1), (1, 2), (9, 1), (9, 2)},
                {(r["compresslevel"], r["workers"]) for r in records},
            )
            for record in records:
                self.assertEqual(expected, record["database_sha256"])
                self.assertGreater(record["compressed_bytes"], 0)
                self.assertGreater(record["wall_seconds"], 0)
                self.assertGreater(record["peak_rss_bytes"], 0)
                self.assertGreaterEqual(record["cpu_seconds"], 0)
            self.assertEqual(packed, (source / "geodata.sqlite.gz").read_bytes())
            self.assertFalse(any(report.glob("case-*")))

    def test_worker_write_error_propagates_to_caller(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            source.mkdir()
            (source / "geodata.sqlite.gz").write_bytes(gzip.compress(b"map" * 1024))
            with patch(
                "tools.split_android_map.gzip.GzipFile.write", side_effect=OSError
            ):
                with self.assertRaises(OSError):
                    split_package(
                        source, Path(directory) / "output", chunk_bytes=1024, workers=2
                    )
