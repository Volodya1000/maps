"""Create bounded, independently compressed Android assets from a country package."""

import argparse
import gzip
import json
import re
import shutil
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

COMPRESSION_LEVELS = range(1, 10)
WORKER_COUNTS = range(1, 5)
DEFAULT_COMPRESSION_LEVEL = 6
DEFAULT_WORKERS = 4


def split_package(
    source: Path,
    destination: Path,
    chunk_bytes: int = 64 * 1024 * 1024,
    *,
    compresslevel: int = DEFAULT_COMPRESSION_LEVEL,
    workers: int = DEFAULT_WORKERS,
) -> None:
    if chunk_bytes <= 0:
        raise ValueError(f"Chunk size {chunk_bytes} must be positive")
    if compresslevel not in COMPRESSION_LEVELS:
        raise ValueError(
            f"Compression level {compresslevel} must be between "
            f"{COMPRESSION_LEVELS.start} and {COMPRESSION_LEVELS.stop - 1}"
        )
    if workers not in WORKER_COUNTS:
        raise ValueError(
            f"Compression workers {workers} must be between "
            f"{WORKER_COUNTS.start} and {WORKER_COUNTS.stop - 1}"
        )
    if destination.exists():
        raise ValueError("Destination must not exist; stale map chunks are unsafe")
    elevation_files = ("elevation.sqlite", "elevation.sqlite.sha256")
    if (source / elevation_files[0]).exists() != (source / elevation_files[1]).exists():
        raise ValueError("Elevation database and checksum must be supplied together")
    destination.mkdir(parents=True)
    for name in ("manifest.json", "NOTICE.txt", "licenses", *elevation_files):
        item = source / name
        if item.is_dir():
            shutil.copytree(item, destination / name)
        elif item.exists():
            shutil.copyfile(item, destination / name)
    elevation = source / "elevation"
    if elevation.exists():
        catalog = json.loads((elevation / "index.json").read_text(encoding="utf-8"))
        target = destination / "elevation"
        target.mkdir()
        shutil.copyfile(elevation / "index.json", target / "index.json")
        if (elevation / "NOTICE.txt").exists():
            shutil.copyfile(elevation / "NOTICE.txt", target / "NOTICE.txt")
        for tile in catalog["tiles"]:
            directory = tile["directory"]
            if (
                not re.fullmatch(r"[A-Za-z0-9_-]+", directory)
                or (elevation / directory).resolve().parent != elevation.resolve()
            ):
                raise ValueError("Country DEM directories must stay inside the package")
            shutil.copytree(elevation / directory, target / directory)

    def compress(block: bytes, index: int) -> None:
        with (destination / f"geodata.{index:04d}.gz").open("wb") as target:
            with gzip.GzipFile(
                fileobj=target, mode="wb", compresslevel=compresslevel, mtime=0
            ) as compressed:
                compressed.write(block)

    with gzip.open(source / "geodata.sqlite.gz", "rb") as stream:
        if workers == 1:
            for index, block in enumerate(iter(lambda: stream.read(chunk_bytes), b"")):
                compress(block, index)
        else:
            # zlib releases the GIL. Threads avoid copying 64 MiB blocks between
            # processes; at most `workers` blocks can be in flight at once.
            with ThreadPoolExecutor(max_workers=workers) as executor:
                pending = deque()
                index = 0
                while block := stream.read(chunk_bytes):
                    pending.append(executor.submit(compress, block, index))
                    index += 1
                    if len(pending) == workers:
                        pending.popleft().result()
                for future in pending:
                    future.result()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument(
        "--compresslevel",
        type=int,
        choices=COMPRESSION_LEVELS,
        default=DEFAULT_COMPRESSION_LEVEL,
    )
    parser.add_argument(
        "--workers", type=int, choices=WORKER_COUNTS, default=DEFAULT_WORKERS
    )
    args = parser.parse_args()
    split_package(
        args.source,
        args.destination,
        compresslevel=args.compresslevel,
        workers=args.workers,
    )
