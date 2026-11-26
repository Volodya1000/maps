import gzip
import tempfile
import unittest
from pathlib import Path

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
