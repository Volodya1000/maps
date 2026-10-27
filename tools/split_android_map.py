"""Create bounded, independently compressed Android assets from a country package."""

import argparse
import gzip
import shutil
from pathlib import Path


def split_package(
    source: Path, destination: Path, chunk_bytes: int = 64 * 1024 * 1024
) -> None:
    if chunk_bytes <= 0:
        raise ValueError("Chunk size must be positive")
    if destination.exists():
        raise ValueError("Destination must not exist; stale map chunks are unsafe")
    destination.mkdir(parents=True)
    for name in ("manifest.json", "NOTICE.txt", "licenses"):
        item = source / name
        if item.is_dir():
            shutil.copytree(item, destination / name)
        elif item.exists():
            shutil.copyfile(item, destination / name)
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
