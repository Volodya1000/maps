"""Check that a distributable APK contains complete, intact country DEM assets."""

import argparse
import hashlib
import json
import math
import re
import zipfile
from pathlib import Path


def check_coverage(tiles, bounds):
    west, south, east, north = bounds
    cuts = sorted(
        {west, east}
        | {x for tile in tiles for x in (tile[0], tile[2]) if west < x < east}
    )
    for left, right in zip(cuts, cuts[1:], strict=False):
        middle = (left + right) / 2
        intervals = sorted(
            (tile[1], tile[3]) for tile in tiles if tile[0] <= middle <= tile[2]
        )
        covered = south
        for bottom, top in intervals:
            if bottom > covered:
                break
            covered = max(covered, top)
        if covered < north:
            raise ValueError(f"DEM coverage gap in longitude interval {left}, {right}")


def verify_apk(path, bounds=None):
    prefix = "assets/Data/elevation/"
    with zipfile.ZipFile(path) as archive:
        catalog = json.loads(archive.read(prefix + "index.json"))
        if catalog.get("schema_version") != 1 or not catalog.get("tiles"):
            raise ValueError("Country DEM requires a nonempty version 1 catalog")
        seen = set()
        rectangles = []
        for tile in catalog["tiles"]:
            directory = tile["directory"]
            if not re.fullmatch(r"[A-Za-z0-9_-]+", directory) or directory in seen:
                raise ValueError(f"Invalid or duplicate DEM directory: {directory}")
            seen.add(directory)
            rectangle = tile["bounds"]
            if (
                len(rectangle) != 4
                or not all(map(math.isfinite, rectangle))
                or not -180 <= rectangle[0] < rectangle[2] <= 180
                or not -90 <= rectangle[1] < rectangle[3] <= 90
            ):
                raise ValueError(f"Invalid DEM bounds: {rectangle}")
            rectangles.append(rectangle)
            database = prefix + directory + "/elevation.sqlite"
            expected = archive.read(database + ".sha256").decode("ascii").strip()
            with archive.open(database) as stream:
                actual = hashlib.file_digest(stream, "sha256").hexdigest()
            if actual != expected:
                raise ValueError(f"DEM checksum mismatch: {directory}")
        if bounds is not None:
            check_coverage(rectangles, bounds)
        return len(seen)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path)
    parser.add_argument("--country", action="store_true")
    args = parser.parse_args()
    lock = (
        Path(__file__).resolve().parent
        / "GeodataPreparation/belarus-elevation.lock.json"
    )
    bounds = json.loads(lock.read_text())["bounds"] if args.country else None
    count = verify_apk(args.apk, bounds)
    print(f"APK country DEM: PASS ({count} verified tiles)")


if __name__ == "__main__":
    main()
