"""Verify that the distributable APK reconstructs the exact verified country map."""

import argparse
import gzip
import hashlib
import json
import re
import zipfile
from pathlib import Path

PREFIX = "assets/Data/"


def verify(apk: Path, manifest: Path) -> dict:
    expected_manifest = manifest.read_bytes()
    metadata = json.loads(expected_manifest)
    expected = metadata.get("database_sha256") if isinstance(metadata, dict) else None
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
        raise ValueError(
            f"Manifest database_sha256={expected!r} must be a lowercase SHA-256"
        )
    digest, database_bytes = hashlib.sha256(), 0
    with zipfile.ZipFile(apk) as archive:
        entries = archive.infolist()
        if sum(info.filename == PREFIX + "manifest.json" for info in entries) != 1:
            raise ValueError("APK must contain exactly one map manifest")
        actual_manifest = archive.read(PREFIX + "manifest.json")
        if actual_manifest != expected_manifest:
            raise ValueError(
                f"APK manifest SHA-256 {hashlib.sha256(actual_manifest).hexdigest()} "
                f"differs from verified source {hashlib.sha256(expected_manifest).hexdigest()}"
            )
        # Match MainActivity's direct AssetManager.List("Data") candidates,
        # including nonnumeric names and directory names ending in .gz. Ignoring
        # either would validate different bytes from those Android consumes.
        candidates = set()
        for info in entries:
            if not info.filename.startswith(PREFIX):
                continue
            child = info.filename[len(PREFIX) :].split("/", 1)[0]
            if (
                child.startswith("geodata.")
                and child.endswith(".gz")
                and child != "geodata.sqlite.gz"
            ):
                candidates.add(PREFIX + child)
        names = sorted(candidates)
        if not names:
            raise ValueError("APK map chunks=[] must contain at least geodata.0000.gz")
        chunks = []
        for name in names:
            matching = [info for info in entries if info.filename == name]
            if len(matching) != 1 or matching[0].is_dir():
                raise ValueError(
                    f"APK map chunk {name!r} has {len(matching)} file entries; exactly one is required"
                )
            chunks.append(matching[0])
        for index, name in enumerate(names):
            canonical = PREFIX + f"geodata.{index:04d}.gz"
            if name != canonical:
                raise ValueError(
                    f"APK map chunk {index}={name!r} must be {canonical!r}"
                )
            with archive.open(name) as packed, gzip.GzipFile(fileobj=packed) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    database_bytes += len(block)
                    digest.update(block)
    actual = digest.hexdigest()
    if actual != expected:
        raise ValueError(
            f"APK database_sha256={actual} must equal verified manifest {expected}"
        )
    with apk.open("rb") as stream:
        apk_sha256 = hashlib.file_digest(stream, "sha256").hexdigest()
    return dict(
        apk_sha256=apk_sha256,
        apk_bytes=apk.stat().st_size,
        manifest_sha256=hashlib.sha256(expected_manifest).hexdigest(),
        database_sha256=actual,
        database_bytes=database_bytes,
        chunks=len(names),
        gzip_bytes=sum(info.file_size for info in chunks),
        apk_map_bytes=sum(info.compress_size for info in chunks),
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        report = verify(args.apk, args.manifest)
    except (OSError, ValueError, EOFError, zipfile.BadZipFile) as error:
        parser.exit(1, f"APK map verification failed: {error}\n")
    encoded = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded, encoding="utf-8")
    print(encoded, end="")


if __name__ == "__main__":
    main()
