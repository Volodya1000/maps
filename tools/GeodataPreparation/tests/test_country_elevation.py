"""Country elevation coverage, integrity and transactional publication contracts."""

import hashlib
import io
import json
import sqlite3
import tempfile
import unittest
import zlib
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

import numpy as np
import rasterio
from rasterio.transform import from_origin

from tools.GeodataPreparation.prepare_country_elevation import (
    build_country_package,
    download_source,
    ownership_tiles,
    verify_country_package,
)


class CountryElevationTests(unittest.TestCase):
    def test_ownership_covers_country_bounds_without_interior_overlap(self):
        bounds = (23.174801, 51.234408, 32.76947, 56.173846)
        tiles = ownership_tiles(bounds)
        self.assertEqual(840, len(tiles))
        self.assertAlmostEqual(
            (bounds[2] - bounds[0]) * (bounds[3] - bounds[1]),
            sum((b[2] - b[0]) * (b[3] - b[1]) for _, b in tiles),
        )
        for point in (
            (27.430907, 53.917148),
            (27.434188, 53.917765),
            (23.175, 51.235),
            (32.769, 56.173),
        ):
            self.assertEqual(
                1,
                sum(
                    w <= point[0] < e and s <= point[1] < n for _, (w, s, e, n) in tiles
                ),
            )
        self.assertTrue(
            all(
                name.isascii() and "/" not in name and ".." not in name
                for name, _ in tiles
            )
        )

    def test_native_seam_padding_nodata_and_quantization(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sources = []
            for west, values in (
                (0, [[1.023, 2.047], [3.001, -9999]]),
                (1, [[5.029, 6.01], [7.001, 8.032]]),
            ):
                path = root / f"{west}.tif"
                with rasterio.open(
                    path,
                    "w",
                    driver="GTiff",
                    width=2,
                    height=2,
                    count=1,
                    dtype="float32",
                    crs="EPSG:4326",
                    transform=from_origin(west - 0.25, 1.25, 0.5, 0.5),
                    nodata=-9999,
                ) as dataset:
                    dataset.write(np.array(values, dtype="float32"), 1)
                sources.append(
                    dict(
                        file=path.name,
                        sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    )
                )
            lock = dict(
                bounds=[0.25, 0.25, 1.25, 0.75],
                source_version="fixture",
                sources=sources,
            )
            build_country_package(root / "output", root, lock, shard_degrees=0.5)
            index = json.loads((root / "output/elevation/index.json").read_text())
            self.assertEqual(6, len(index["tiles"]))
            with closing(
                sqlite3.connect(
                    root
                    / "output/elevation"
                    / index["tiles"][1]["directory"]
                    / "elevation.sqlite"
                )
            ) as db:
                metadata = json.loads(
                    db.execute("SELECT json FROM metadata").fetchone()[0]
                )
                self.assertEqual(0.1, metadata["vertical_quantization_metres"])
                self.assertEqual(2, metadata["schema_version"])
                self.assertEqual("zlib-float32-le", metadata["block_encoding"])
                grid = metadata["grids"][0]
                self.assertEqual(0.5, grid["affine"][0])
                self.assertLess(grid["affine"][2], 0.75)
                samples = np.frombuffer(
                    zlib.decompress(
                        db.execute("SELECT samples FROM elevation_blocks").fetchone()[0]
                    ),
                    dtype="<f4",
                ).reshape(128, 128)
                self.assertTrue(np.isnan(samples[1, 1]))
                self.assertAlmostEqual(5.0, float(samples[0, 2]))
                self.assertLessEqual(abs(float(samples[0, 2]) - 5.029), 0.05)

    def test_corrupt_download_is_rejected_and_old_cache_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "tile.tif"
            path.write_bytes(b"old")
            source = dict(
                file=path.name,
                url="https://example.test/tile",
                sha256=hashlib.sha256(b"expected").hexdigest(),
            )
            with patch(
                "tools.GeodataPreparation.prepare_country_elevation.urlopen",
                return_value=io.BytesIO(b"corrupt"),
            ):
                with self.assertRaises(ValueError):
                    download_source(source, root)
            self.assertEqual(b"old", path.read_bytes())
            self.assertEqual([path], list(root.iterdir()))

    def test_integer_latitude_band_seam_keeps_both_native_rows(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sources = []
            for south, values in ((0, [[10, 10], [20, 20]]), (1, [[30, 30], [40, 40]])):
                path = root / f"latitude-{south}.tif"
                with rasterio.open(
                    path,
                    "w",
                    driver="GTiff",
                    width=2,
                    height=2,
                    count=1,
                    dtype="float32",
                    crs="EPSG:4326",
                    transform=from_origin(-0.25, south + 1.25, 0.5, 0.5),
                ) as dataset:
                    dataset.write(np.array(values, dtype="float32"), 1)
                sources.append(
                    dict(
                        file=path.name,
                        sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    )
                )
            lock = dict(
                bounds=[0.25, 0.75, 0.75, 1.25],
                source_version="fixture",
                sources=sources,
            )
            index = build_country_package(
                root / "output", root, lock, shard_degrees=0.5
            )
            upper = next(
                tile
                for tile in index["tiles"]
                if tile["bounds"] == [0.5, 1, 0.75, 1.25]
            )
            with closing(
                sqlite3.connect(
                    root / "output/elevation" / upper["directory"] / "elevation.sqlite"
                )
            ) as database:
                metadata = json.loads(
                    database.execute("SELECT json FROM metadata").fetchone()[0]
                )
                self.assertEqual(
                    {"latitude-0.tif", "latitude-1.tif"},
                    {source["file"] for source in metadata["source_inputs"]},
                )
                samples = np.frombuffer(
                    zlib.decompress(
                        database.execute(
                            "SELECT samples FROM elevation_blocks"
                        ).fetchone()[0]
                    ),
                    dtype="<f4",
                ).reshape(128, 128)
                self.assertEqual(40, samples[0, 1])
                self.assertEqual(10, samples[1, 1])

    def test_failed_build_keeps_previous_catalog_and_shards(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            elevation = root / "output/elevation"
            elevation.mkdir(parents=True)
            (elevation / "index.json").write_text("previous")
            lock = dict(
                bounds=[0, 0, 1, 1],
                source_version="fixture",
                sources=[dict(file="missing.tif", sha256="0" * 64)],
            )
            with self.assertRaises(OSError):
                build_country_package(root / "output", root, lock)
            self.assertEqual("previous", (elevation / "index.json").read_text())
            self.assertEqual([elevation], list((root / "output").iterdir()))

    def test_catalog_publication_failure_preserves_previous_generation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            lock = self.small_country(root)
            output = root / "output"
            build_country_package(output, root, lock)
            previous = (output / "elevation/index.json").read_bytes()
            previous_files = sorted(
                path.relative_to(output) for path in output.rglob("*")
            )
            lock["source_version"] = "updated-fixture"
            replace = Path.replace

            def fail_catalog_publication(path, target):
                if target == output / "elevation/index.json":
                    raise PermissionError("catalog locked")
                return replace(path, target)

            with patch.object(Path, "replace", fail_catalog_publication):
                with self.assertRaises(PermissionError):
                    build_country_package(output, root, lock)
            self.assertEqual(previous, (output / "elevation/index.json").read_bytes())
            self.assertEqual(
                previous_files,
                sorted(path.relative_to(output) for path in output.rglob("*")),
            )

    def test_verify_rejects_missing_coverage_and_corrupt_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            lock = self.small_country(root)
            output = root / "output"
            index = build_country_package(output, root, lock)
            self.assertEqual(index, verify_country_package(output, lock))
            catalog = output / "elevation/index.json"
            incomplete = dict(index, tiles=index["tiles"][:-1])
            catalog.write_text(json.dumps(incomplete), encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_country_package(output, lock)
            catalog.write_text(json.dumps(index), encoding="utf-8")
            database = (
                output
                / "elevation"
                / index["tiles"][0]["directory"]
                / "elevation.sqlite"
            )
            with database.open("ab") as stream:
                stream.write(b"corruption")
            with self.assertRaises(ValueError):
                verify_country_package(output, lock)

    def test_verify_rejects_wrong_provenance_with_valid_database_checksum(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            lock = self.small_country(root)
            output = root / "output"
            index = build_country_package(output, root, lock)
            database = (
                output
                / "elevation"
                / index["tiles"][0]["directory"]
                / "elevation.sqlite"
            )
            with closing(sqlite3.connect(database)) as connection, connection:
                metadata = json.loads(
                    connection.execute("SELECT json FROM metadata").fetchone()[0]
                )
                metadata["vertical_datum"] = "wrong-datum"
                connection.execute(
                    "UPDATE metadata SET json = ?", (json.dumps(metadata),)
                )
            database.with_name(database.name + ".sha256").write_text(
                hashlib.sha256(database.read_bytes()).hexdigest()
            )
            with self.assertRaises(ValueError):
                verify_country_package(output, lock)

    @staticmethod
    def small_country(root):
        path = root / "source.tif"
        with rasterio.open(
            path,
            "w",
            driver="GTiff",
            width=4,
            height=4,
            count=1,
            dtype="float32",
            crs="EPSG:4326",
            transform=from_origin(-0.25, 1.25, 0.5, 0.5),
        ) as dataset:
            dataset.write(np.arange(16, dtype="float32").reshape(4, 4), 1)
        return dict(
            bounds=[0.25, 0.25, 0.75, 0.75],
            source_version="fixture",
            sources=[
                dict(
                    file=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest()
                )
            ],
        )


if __name__ == "__main__":
    unittest.main()
