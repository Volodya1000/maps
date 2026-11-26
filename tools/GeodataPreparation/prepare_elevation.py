"""Build an offline elevation.sqlite package from local Copernicus GLO-30 GeoTIFFs."""

import argparse
import hashlib
import json
import math
import shutil
import sqlite3
import tempfile
from contextlib import closing
from pathlib import Path

import numpy as np
import rasterio
from rasterio.crs import CRS
from rasterio.errors import RasterioError
from rasterio.windows import Window, from_bounds

BLOCK_SIZE = 128
COORDINATE_SYSTEM = "EPSG:4326"
ATTRIBUTION = (
    "produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and "
    "© Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS "
    "by the European Union and ESA; all rights reserved"
)
LICENSE_URL = (
    "https://spacedata.copernicus.eu/collections/copernicus-digital-elevation-model"
)

SCHEMA = """
CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL);
CREATE TABLE elevation_blocks (
    grid_id INTEGER NOT NULL,
    block_row INTEGER NOT NULL,
    block_column INTEGER NOT NULL,
    samples BLOB NOT NULL,
    PRIMARY KEY (grid_id, block_row, block_column)
) WITHOUT ROWID;
"""


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def validate_bounds(bounds):
    west, south, east, north = bounds
    if not (
        all(map(math.isfinite, bounds))
        and -180 <= west < east <= 180
        and -90 <= south < north <= 90
    ):
        raise ValueError(f"bounds {bounds} must be ordered within EPSG:4326")


def validate_dataset(dataset):
    if dataset.crs != CRS.from_string(COORDINATE_SYSTEM):
        raise ValueError(
            f"{dataset.name}: crs {dataset.crs} must be {COORDINATE_SYSTEM}"
        )
    transform = dataset.transform
    if not (
        all(map(math.isfinite, tuple(transform)[:6]))
        and transform.a > 0
        and transform.e < 0
        and transform.b == transform.d == 0
    ):
        raise ValueError(
            f"{dataset.name}: affine {tuple(transform)[:6]} must be north-up and unrotated"
        )
    validate_bounds(dataset.bounds)
    if (
        dataset.driver != "GTiff"
        or dataset.count != 1
        or np.dtype(dataset.dtypes[0]).kind not in "iuf"
        or dataset.scales[0] != 1
        or dataset.offsets[0] != 0
    ):
        raise ValueError(
            f"{dataset.name}: driver={dataset.driver}, bands={dataset.dtypes}, "
            f"scales={dataset.scales}, offsets={dataset.offsets}; "
            "expected one unscaled numeric GeoTIFF band in metres"
        )
    unit = dataset.units[0]
    if unit is not None and unit.strip().casefold() not in {
        "m",
        "metre",
        "metres",
        "meter",
        "meters",
    }:
        raise ValueError(f"{dataset.name}: band unit {unit!r} must be metres")


def cropped_window(dataset, bounds):
    if bounds is None:
        return Window(0, 0, dataset.width, dataset.height)
    left, bottom, right, top = dataset.bounds
    left, bottom = max(left, bounds[0]), max(bottom, bounds[1])
    right, top = min(right, bounds[2]), min(top, bounds[3])
    if left >= right or bottom >= top:
        return None
    window = from_bounds(left, bottom, right, top, dataset.transform)
    column, row = max(0, math.floor(window.col_off)), max(0, math.floor(window.row_off))
    end_column = min(dataset.width, math.ceil(window.col_off + window.width))
    end_row = min(dataset.height, math.ceil(window.row_off + window.height))
    return Window(column, row, end_column - column, end_row - row)


def write_grid(connection, dataset, grid_id, window):
    width, height = int(window.width), int(window.height)
    for row in range(0, height, BLOCK_SIZE):
        for column in range(0, width, BLOCK_SIZE):
            block_width = min(BLOCK_SIZE, width - column)
            block_height = min(BLOCK_SIZE, height - row)
            heights = dataset.read(
                1,
                window=Window(
                    window.col_off + column,
                    window.row_off + row,
                    block_width,
                    block_height,
                ),
                masked=True,
                out_dtype="float32",
            ).filled(np.nan)
            samples = np.full((BLOCK_SIZE, BLOCK_SIZE), np.nan, dtype="<f4")
            np.copyto(
                samples[:block_height, :block_width],
                heights,
                where=np.isfinite(heights),
            )
            connection.execute(
                "INSERT INTO elevation_blocks VALUES (?, ?, ?, ?)",
                (grid_id, row // BLOCK_SIZE, column // BLOCK_SIZE, samples.tobytes()),
            )


def _publish_package(staged_database, staged_checksum, output, checksum):
    backup_directory = None
    backup = None
    preserve_backup = False
    try:
        if output.exists():
            backup_directory = tempfile.TemporaryDirectory(
                prefix="elevation-recovery-", dir=output.parent, delete=False
            )
            backup = Path(backup_directory.name) / output.name
            shutil.copy2(output, backup)
        staged_database.replace(output)
        try:
            staged_checksum.replace(checksum)
        except OSError as publication_error:
            try:
                if backup is None:
                    output.unlink()
                else:
                    backup.replace(output)
            except OSError as rollback_error:
                message = (
                    f"Checksum publication failed ({publication_error}). "
                    f"Database rollback failed ({rollback_error})."
                )
                recovery_path = output
                if backup is not None:
                    preserve_backup = True
                    recovery_path = backup
                    message += " Previous database retained at the reported path."
                raise OSError(
                    rollback_error.errno, message, str(recovery_path)
                ) from rollback_error
            raise
    finally:
        if backup_directory is not None and not preserve_backup:
            backup_directory.cleanup()


def build_package(output, inputs, source_version, bounds):
    if bounds is not None:
        validate_bounds(bounds)
    if not source_version.strip():
        raise ValueError("source_version must identify the Copernicus DEM release")

    checksum = output.with_name(output.name + ".sha256")
    paths = [path.resolve() for path in inputs]
    if len(set(paths)) != len(paths):
        raise ValueError("each DEM input path must be unique")
    if {output.resolve(), checksum.resolve()}.intersection(paths):
        raise ValueError("output and checksum paths must differ from every DEM input")
    output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(dir=output.parent) as temporary:
        staged = Path(temporary) / "elevation.sqlite"
        staged_checksum = Path(temporary) / "elevation.sqlite.sha256"
        with closing(sqlite3.connect(staged)) as connection, connection:
            connection.executescript(SCHEMA)
            grids = []
            for path in paths:
                with rasterio.open(path) as dataset:
                    validate_dataset(dataset)
                    window = cropped_window(dataset, bounds)
                    if window is None:
                        continue
                    grid = {
                        "id": len(grids) + 1,
                        "width": int(window.width),
                        "height": int(window.height),
                        "affine": tuple(dataset.window_transform(window))[:6],
                        "input_file": path.name,
                        "input_sha256": sha256(path),
                    }
                    write_grid(connection, dataset, grid["id"], window)
                    grids.append(grid)
            if not grids:
                raise ValueError(f"bounds {bounds} do not intersect the supplied DEMs")
            metadata = {
                "schema_version": 1,
                "coordinate_system": COORDINATE_SYSTEM,
                "block_size": BLOCK_SIZE,
                "source_name": "Copernicus DEM GLO-30",
                "source_version": source_version,
                "vertical_datum": "EGM2008",
                "resolution_metres": 30,
                "attribution": ATTRIBUTION,
                "license_url": LICENSE_URL,
                "grids": grids,
            }
            connection.execute(
                "INSERT INTO metadata VALUES (1, ?)", (json.dumps(metadata),)
            )
        staged_checksum.write_text(sha256(staged) + "\n", encoding="ascii")
        _publish_package(staged, staged_checksum, output, checksum)
    return len(grids)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dem", type=Path, nargs="+", required=True)
    parser.add_argument(
        "--output", type=Path, required=True, help="elevation.sqlite path"
    )
    parser.add_argument(
        "--source-version", required=True, help="Copernicus source release identifier"
    )
    parser.add_argument(
        "--bounds",
        nargs=4,
        type=float,
        metavar=("WEST", "SOUTH", "EAST", "NORTH"),
        help="optional crop to the intersecting native pixels in EPSG:4326",
    )
    args = parser.parse_args()
    try:
        count = build_package(args.output, args.dem, args.source_version, args.bounds)
    except (OSError, ValueError, RasterioError, sqlite3.Error) as error:
        parser.error(str(error))
    print(f"Wrote {args.output} and its SHA-256 for {count} native DEM grid(s)")


if __name__ == "__main__":
    main()
