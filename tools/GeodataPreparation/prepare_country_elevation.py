"""Prepare locked Copernicus GLO-30 coverage for the whole Belarus rectangle.

Downloads are preparation-only. Published applications read local SQLite shards.
Country samples are rounded to 0.1 m, retaining native horizontal sample
locations and EGM2008 vertical datum. Ideal rounding error is at most 0.05 m;
storage adds float32 representation error.
"""

import argparse
import hashlib
import json
import math
import shutil
import sqlite3
import tempfile
import zlib
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, closing
from pathlib import Path
from urllib.request import urlopen

import numpy as np
import rasterio
from rasterio.merge import merge

try:
    from .prepare_elevation import (
        ATTRIBUTION,
        BLOCK_SIZE,
        LICENSE_URL,
        SCHEMA,
        sha256,
        validate_bounds,
        validate_dataset,
    )
except ImportError:
    from prepare_elevation import (
        ATTRIBUTION,
        BLOCK_SIZE,
        LICENSE_URL,
        SCHEMA,
        sha256,
        validate_bounds,
        validate_dataset,
    )

LOCK_PATH = Path(__file__).with_name("belarus-elevation.lock.json")
QUANTIZATION_METRES = 0.1
SOURCE_VERSION_SUFFIX = f"; {QUANTIZATION_METRES:g} m vertical quantization"
NOTICE = (
    ATTRIBUTION + "\n" + LICENSE_URL + "\n"
    "Copernicus DEM GLO-30 AWS 2021 release; EGM2008 heights in metres.\n"
    "Native horizontal sample positions are retained. Vertical samples are rounded\n"
    f"to {QUANTIZATION_METRES:g} m for distribution (ideal rounding error at most\n"
    f"{QUANTIZATION_METRES / 2:g} m, plus float32 representation error). This is a surface\n"
    "model, including vegetation and buildings, not a bare-earth survey.\n"
)


def ownership_tiles(bounds, shard_degrees=0.25):
    """Return a complete rectangle partition with shared edges and flat safe names."""
    validate_bounds(bounds)
    if not math.isfinite(shard_degrees) or not 0 < shard_degrees <= 1:
        raise ValueError("shard_degrees must be finite and in (0, 1]")
    west, south, east, north = bounds
    return [
        (
            f"n{row:04d}-e{column:04d}",
            [
                max(west, column * shard_degrees),
                max(south, row * shard_degrees),
                min(east, (column + 1) * shard_degrees),
                min(north, (row + 1) * shard_degrees),
            ],
        )
        for row in range(
            math.floor(south / shard_degrees), math.ceil(north / shard_degrees)
        )
        for column in range(
            math.floor(west / shard_degrees), math.ceil(east / shard_degrees)
        )
    ]


def download_source(source, cache):
    """Reuse a hash-verified cache entry, or atomically install a verified download."""
    filename = source["file"]
    if Path(filename).name != filename or "/" in filename or "\\" in filename:
        raise ValueError("source file must be a flat filename")
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / filename
    if path.exists() and sha256(path) == source["sha256"]:
        return path
    with tempfile.TemporaryDirectory(prefix="dem-download-", dir=cache) as temporary:
        staged = Path(temporary) / filename
        with (
            urlopen(source["url"], timeout=90) as response,
            staged.open("wb") as target,
        ):
            shutil.copyfileobj(response, target, 1024 * 1024)
        if sha256(staged) != source["sha256"]:
            raise ValueError(
                f"{filename}: downloaded SHA-256 does not match source lock"
            )
        staged.replace(path)
    return path


def _write_shard(output, datasets, bounds, source_version, inputs):
    xres, yres = datasets[0].res
    if any(dataset.res != (xres, yres) for dataset in datasets):
        raise ValueError("native grid resolutions differ; resampling is not permitted")
    origin_x = min(dataset.bounds.left for dataset in datasets)
    origin_y = max(dataset.bounds.top for dataset in datasets)
    west, south, east, north = bounds
    padded = (
        origin_x + (math.floor((west - origin_x) / xres) - 1) * xres,
        origin_y - (math.ceil((origin_y - south) / yres) + 1) * yres,
        origin_x + (math.ceil((east - origin_x) / xres) + 1) * xres,
        origin_y - (math.floor((origin_y - north) / yres) - 1) * yres,
    )
    selected = [
        dataset
        for dataset in datasets
        if dataset.bounds.left < padded[2]
        and dataset.bounds.right > padded[0]
        and dataset.bounds.bottom < padded[3]
        and dataset.bounds.top > padded[1]
    ]
    if not selected:
        raise ValueError(f"ownership bounds {bounds} have no DEM source")
    samples, transform = merge(
        selected, bounds=padded, res=(xres, yres), nodata=np.nan, dtype="float32"
    )
    finite = np.isfinite(samples)
    # Float64 arithmetic avoids extra float32 rounding before final storage.
    samples[finite] = (
        np.rint(samples[finite].astype("float64") / QUANTIZATION_METRES)
        * QUANTIZATION_METRES
    ).astype("float32")
    output.parent.mkdir(parents=True)
    height, width = samples.shape[1:]
    with closing(sqlite3.connect(output)) as connection, connection:
        connection.executescript(SCHEMA)
        for row in range(0, height, BLOCK_SIZE):
            for column in range(0, width, BLOCK_SIZE):
                block = np.full((BLOCK_SIZE, BLOCK_SIZE), np.nan, dtype="<f4")
                source = samples[
                    0, row : row + BLOCK_SIZE, column : column + BLOCK_SIZE
                ]
                np.copyto(
                    block[: source.shape[0], : source.shape[1]],
                    source,
                    where=np.isfinite(source),
                )
                connection.execute(
                    "INSERT INTO elevation_blocks VALUES (1, ?, ?, ?)",
                    (
                        row // BLOCK_SIZE,
                        column // BLOCK_SIZE,
                        zlib.compress(block.tobytes()),
                    ),
                )
        metadata = {
            "schema_version": 2,
            "block_encoding": "zlib-float32-le",
            "coordinate_system": "EPSG:4326",
            "block_size": BLOCK_SIZE,
            "source_name": "Copernicus DEM GLO-30",
            "source_version": source_version,
            "vertical_datum": "EGM2008",
            "resolution_metres": 30,
            "vertical_quantization_metres": QUANTIZATION_METRES,
            "attribution": ATTRIBUTION,
            "license_url": LICENSE_URL,
            "source_inputs": [inputs[Path(item.name).name] for item in selected],
            "grids": [
                dict(
                    id=1,
                    width=width,
                    height=height,
                    affine=tuple(transform)[:6],
                    input_file="native-source-mosaic",
                    input_sha256=hashlib.sha256(
                        json.dumps(
                            [inputs[Path(item.name).name] for item in selected],
                            sort_keys=True,
                        ).encode()
                    ).hexdigest(),
                )
            ],
        }
        connection.execute("INSERT INTO metadata VALUES (1,?)", (json.dumps(metadata),))
    output.with_name(output.name + ".sha256").write_text(
        sha256(output) + "\n", encoding="ascii"
    )


def build_country_package(output, cache, lock, *, shard_degrees=0.25):
    """Build all shards privately, then atomically publish the catalog last.

    Source files are verified exactly once before opening, never once per shard.
    Content-addressed generation names leave the prior catalog usable throughout.
    """
    tiles = ownership_tiles(lock["bounds"], shard_degrees)
    source_version = lock["source_version"] + SOURCE_VERSION_SUFFIX
    generation = hashlib.sha256(
        json.dumps(
            [lock, shard_degrees, QUANTIZATION_METRES, 2, "zlib-float32-le"],
            sort_keys=True,
        ).encode()
    ).hexdigest()[:16]
    output.mkdir(parents=True, exist_ok=True)
    published = output / "elevation"
    with (
        tempfile.TemporaryDirectory(
            prefix="elevation-staging-", dir=output
        ) as temporary,
        ExitStack() as stack,
    ):
        datasets = []
        inputs = {}
        for source in lock["sources"]:
            path = cache / source["file"]
            if sha256(path) != source["sha256"]:
                raise ValueError(f"{path}: SHA-256 does not match source lock")
            dataset = stack.enter_context(rasterio.open(path))
            validate_dataset(dataset)
            datasets.append(dataset)
            inputs[path.name] = dict(file=path.name, sha256=source["sha256"])
        staging = Path(temporary)
        entries = []
        for name, bounds in tiles:
            directory = name + "-" + generation
            _write_shard(
                staging / directory / "elevation.sqlite",
                datasets,
                bounds,
                source_version,
                inputs,
            )
            entries.append(dict(directory=directory, bounds=bounds))
        index = dict(
            schema_version=1,
            source=dict(
                name="Copernicus DEM GLO-30",
                version=source_version,
                vertical_datum="EGM2008",
                resolution_metres=30,
                attribution=ATTRIBUTION,
                license_url=LICENSE_URL,
            ),
            tiles=entries,
        )
        staged_index = staging / "index.json"
        staged_index.write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
        staged_notice = staging / "NOTICE.txt"
        staged_notice.write_text(NOTICE, encoding="utf-8")
        published.mkdir(exist_ok=True)
        installed = []
        try:
            for entry in entries:
                directory = entry["directory"]
                target = published / directory
                if target.exists():
                    if sha256(target / "elevation.sqlite") != sha256(
                        staging / directory / "elevation.sqlite"
                    ):
                        raise ValueError(
                            f"{target}: existing content-addressed shard differs"
                        )
                    continue
                (staging / directory).replace(target)
                installed.append(target)
            staged_notice.replace(published / "NOTICE.txt")
            staged_index.replace(published / "index.json")
        except (OSError, ValueError):
            for target in installed:
                shutil.rmtree(target)
            raise
    return index


def verify_country_package(output, lock, *, shard_degrees=0.25):
    """Verify cached package ownership, provenance and every database checksum."""
    elevation = output / "elevation"
    index = json.loads((elevation / "index.json").read_text(encoding="utf-8"))
    expected_bounds = [
        bounds for _, bounds in ownership_tiles(lock["bounds"], shard_degrees)
    ]
    if (
        index.get("schema_version") != 1
        or [tile["bounds"] for tile in index["tiles"]] != expected_bounds
    ):
        raise ValueError(
            "catalog ownership does not cover the locked country partition"
        )
    version = lock["source_version"] + SOURCE_VERSION_SUFFIX
    expected_source = dict(
        name=lock.get("source_name", "Copernicus DEM GLO-30"),
        version=version,
        vertical_datum="EGM2008",
        resolution_metres=30,
        attribution=ATTRIBUTION,
        license_url=LICENSE_URL,
    )
    if index["source"] != expected_source:
        raise ValueError("catalog source metadata does not match the lock")
    if (elevation / "NOTICE.txt").read_text(encoding="utf-8") != NOTICE:
        raise ValueError("elevation attribution notice is missing or differs")
    for tile in index["tiles"]:
        directory = tile["directory"]
        if (
            not directory.isascii()
            or Path(directory).name != directory
            or not all(
                character.isalnum() or character in "-_" for character in directory
            )
        ):
            raise ValueError("catalog directory must be a safe flat ASCII name")
        database = elevation / directory / "elevation.sqlite"
        if (
            sha256(database)
            != database.with_name(database.name + ".sha256")
            .read_text(encoding="ascii")
            .strip()
        ):
            raise ValueError(f"{database}: SHA-256 checksum mismatch")
        with closing(
            sqlite3.connect(f"file:{database.as_posix()}?mode=ro", uri=True)
        ) as connection:
            metadata = json.loads(
                connection.execute("SELECT json FROM metadata").fetchone()[0]
            )
        expected_metadata = dict(
            schema_version=2,
            block_encoding="zlib-float32-le",
            coordinate_system="EPSG:4326",
            block_size=BLOCK_SIZE,
            source_name=expected_source["name"],
            source_version=version,
            vertical_datum=expected_source["vertical_datum"],
            resolution_metres=expected_source["resolution_metres"],
            attribution=ATTRIBUTION,
            license_url=LICENSE_URL,
            vertical_quantization_metres=QUANTIZATION_METRES,
        )
        source_hashes = {source["file"]: source["sha256"] for source in lock["sources"]}
        if (
            any(metadata.get(key) != value for key, value in expected_metadata.items())
            or not metadata.get("source_inputs")
            or any(
                source_hashes.get(source["file"]) != source["sha256"]
                for source in metadata["source_inputs"]
            )
        ):
            raise ValueError(f"{database}: source metadata does not match the lock")
        if len(metadata["grids"]) != 1:
            raise ValueError(
                f"{database}: country shard must have one stitched native grid"
            )
        grid = metadata["grids"][0]
        a, b, c, d, e, f = grid["affine"]
        west, south, east, north = tile["bounds"]
        if not (
            a > 0
            and e < 0
            and b == d == 0
            and c + 0.5 * a < west
            and c + (grid["width"] - 0.5) * a > east
            and f + 0.5 * e > north
            and f + (grid["height"] - 0.5) * e < south
        ):
            raise ValueError(
                f"{database}: native sample padding does not cover ownership bounds"
            )
    return index


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, required=True, help="root offline OSM package directory"
    )
    parser.add_argument("--cache", type=Path, default=Path("artifacts/dem/country"))
    parser.add_argument("--lock", type=Path, default=LOCK_PATH)
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="verify prepared cache without network or rebuilding",
    )
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    if args.verify_only:
        index = verify_country_package(args.output, lock)
        print(f"Verified {len(index['tiles'])} complete country elevation shards")
        return
    with ThreadPoolExecutor(max_workers=4) as executor:
        list(
            executor.map(
                lambda source: download_source(source, args.cache), lock["sources"]
            )
        )
    index = build_country_package(args.output, args.cache, lock)
    print(
        f"Published {len(index['tiles'])} elevation shards: {args.output / 'elevation/index.json'}"
    )


if __name__ == "__main__":
    main()
