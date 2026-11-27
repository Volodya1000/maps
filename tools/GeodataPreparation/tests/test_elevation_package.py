"""Verify the elevation package CLI against small, local GeoTIFF inputs."""

import errno
import hashlib
import json
import math
import sqlite3
import struct
import subprocess
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

import numpy as np
import rasterio
from rasterio.transform import Affine

from tools.GeodataPreparation.prepare_elevation import build_package

CONVERTER = Path(__file__).resolve().parents[1] / "prepare_elevation.py"


class ElevationPackageTests(unittest.TestCase):
    def test_crop_preserves_native_affine_float_samples_and_nodata(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "slope.tif"
            output = root / "elevation.sqlite"
            values = np.arange(266, dtype=np.float32).reshape(2, 133)
            values[0, 128] = -9999
            self.write_dem(source, values, crs="EPSG:4326")

            result = self.prepare(
                source,
                output,
                "--bounds",
                "27.000244140625",
                "53.9990234375",
                "27.031982421875",
                "54",
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(
                hashlib.sha256(output.read_bytes()).hexdigest(),
                output.with_suffix(".sqlite.sha256").read_text().strip(),
            )
            with closing(sqlite3.connect(output)) as connection:
                metadata = json.loads(
                    connection.execute("SELECT json FROM metadata").fetchone()[0]
                )
                self.assertEqual("EPSG:4326", metadata["coordinate_system"])
                self.assertEqual("EGM2008", metadata["vertical_datum"])
                self.assertEqual("fixture-release", metadata["source_version"])
                self.assertEqual(30, metadata["resolution_metres"])
                self.assertTrue(metadata["attribution"])
                self.assertTrue(metadata["license_url"].startswith("https://"))
                self.assertEqual(128, metadata["block_size"])
                self.assertEqual(
                    [
                        {
                            "id": 1,
                            "width": 130,
                            "height": 2,
                            "affine": [
                                1 / 4096,
                                0,
                                27 + 1 / 4096,
                                0,
                                -1 / 2048,
                                54,
                            ],
                            "input_file": source.name,
                            "input_sha256": hashlib.sha256(
                                source.read_bytes()
                            ).hexdigest(),
                        }
                    ],
                    metadata["grids"],
                )
                blocks = connection.execute(
                    "SELECT block_column, samples FROM elevation_blocks "
                    "WHERE grid_id = 1 AND block_row = 0 ORDER BY block_column"
                ).fetchall()
                self.assertEqual([0, 1], [column for column, _ in blocks])
                left = struct.unpack("<16384f", blocks[0][1])
                right = struct.unpack("<16384f", blocks[1][1])
                self.assertEqual(1, left[0])
                self.assertEqual(134, left[128])
                self.assertTrue(math.isnan(left[127]))
                self.assertEqual((129, 130), right[:2])
                self.assertTrue(math.isnan(right[2]))

    def test_rejects_projected_input_without_replacing_existing_package(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "projected.tif"
            output = root / "elevation.sqlite"
            output.write_bytes(b"old package")
            self.write_dem(source, np.array([[100]], dtype=np.float32), crs="EPSG:3857")

            result = self.prepare(source, output)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("EPSG:4326", result.stderr)
            self.assertEqual(b"old package", output.read_bytes())
            self.assertCountEqual([source, output], root.iterdir())

    def test_rejects_explicit_feet_without_replacing_existing_package(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "height.tif"
            output = root / "elevation.sqlite"
            checksum = output.with_suffix(".sqlite.sha256")
            self.write_dem(source, np.array([[100]], dtype=np.float32), crs="EPSG:4326")
            original = self.prepare(source, output)
            self.assertEqual(0, original.returncode, original.stderr)
            old_database, old_checksum = output.read_bytes(), checksum.read_bytes()
            self.write_dem(
                source, np.array([[200]], dtype=np.float32), crs="EPSG:4326", unit="ft"
            )

            result = self.prepare(source, output)

            self.assertEqual(2, result.returncode)
            self.assertEqual(old_database, output.read_bytes())
            self.assertEqual(old_checksum, checksum.read_bytes())
            self.assertCountEqual([source, output, checksum], root.iterdir())

    def test_failed_checksum_publication_preserves_existing_package(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "height.tif"
            output = root / "elevation.sqlite"
            checksum = output.with_suffix(".sqlite.sha256")
            self.write_dem(source, np.array([[100]], dtype=np.float32), crs="EPSG:4326")
            original = self.prepare(source, output)
            self.assertEqual(0, original.returncode, original.stderr)
            old_database, old_checksum = output.read_bytes(), checksum.read_bytes()
            self.write_dem(source, np.array([[200]], dtype=np.float32), crs="EPSG:4326")
            replace = Path.replace

            def replace_with_locked_checksum(path, target):
                if target == checksum:
                    raise PermissionError("the checksum cannot be replaced")
                return replace(path, target)

            with (
                patch.object(Path, "replace", replace_with_locked_checksum),
                self.assertRaises(PermissionError),
            ):
                build_package(output, [source], "revised-release", None)

            self.assertEqual(old_database, output.read_bytes())
            self.assertEqual(old_checksum, checksum.read_bytes())
            with closing(sqlite3.connect(output)) as connection:
                metadata = json.loads(
                    connection.execute("SELECT json FROM metadata").fetchone()[0]
                )
                self.assertEqual("fixture-release", metadata["source_version"])
            self.assertCountEqual([source, output, checksum], root.iterdir())

    def test_failed_rollback_reports_recovery_copy_of_previous_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "height.tif"
            output = root / "elevation.sqlite"
            checksum = output.with_suffix(".sqlite.sha256")
            self.write_dem(source, np.array([[100]], dtype=np.float32), crs="EPSG:4326")
            original = self.prepare(source, output)
            self.assertEqual(0, original.returncode, original.stderr)
            old_database, old_checksum = output.read_bytes(), checksum.read_bytes()
            self.write_dem(source, np.array([[200]], dtype=np.float32), crs="EPSG:4326")
            publication_error = PermissionError(
                errno.EACCES, "checksum locked", checksum
            )
            rollback_error = PermissionError(errno.EACCES, "database locked", output)
            replace = Path.replace

            def replace_with_locked_files(path, target):
                if target == checksum:
                    raise publication_error
                if target == output and output.read_bytes() != old_database:
                    raise rollback_error
                return replace(path, target)

            with (
                patch.object(Path, "replace", replace_with_locked_files),
                self.assertRaises(OSError) as failure,
            ):
                build_package(output, [source], "revised-release", None)

            recovery = Path(failure.exception.filename)
            self.assertEqual(old_database, recovery.read_bytes())
            self.assertNotEqual(output, recovery)
            self.assertEqual(old_checksum, checksum.read_bytes())
            self.assertIs(rollback_error, failure.exception.__cause__)
            self.assertIs(publication_error, rollback_error.__context__)
            self.assertCountEqual(
                [source, output, checksum, recovery.parent], root.iterdir()
            )
            self.assertCountEqual([recovery], recovery.parent.iterdir())

    @staticmethod
    def write_dem(path, values, *, crs, unit=None):
        with rasterio.open(
            path,
            "w",
            driver="GTiff",
            width=values.shape[1],
            height=values.shape[0],
            count=1,
            dtype="float32",
            crs=crs,
            transform=Affine(1 / 4096, 0, 27, 0, -1 / 2048, 54),
            nodata=-9999,
        ) as dataset:
            dataset.write(values, 1)
            if unit is not None:
                dataset.set_band_unit(1, unit)

    @staticmethod
    def prepare(source, output, *arguments):
        return subprocess.run(
            [
                sys.executable,
                str(CONVERTER),
                "--dem",
                str(source),
                "--output",
                str(output),
                "--source-version",
                "fixture-release",
                *arguments,
            ],
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )


if __name__ == "__main__":
    unittest.main()
