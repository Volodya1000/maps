"""Create bounded, independently compressed Android assets from a country package."""

import argparse
import gzip
import json
import re
import shutil
from pathlib import Path


def split_package(
    source: Path, destination: Path, chunk_bytes: int = 64 * 1024 * 1024
) -> None:
    if chunk_bytes <= 0:
        raise ValueError("Chunk size must be positive")
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
    with gzip.open(source / "geodata.sqlite.gz", "rb") as stream:
        index = 0
        while block := stream.read(chunk_bytes):
            with (destination / f"geodata.{index:04d}.gz").open("wb") as target:
                with gzip.GzipFile(fileobj=target, mode="wb", mtime=0) as compressed:
                    compressed.write(block)
            index += 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    split_package(args.source, args.destination)
