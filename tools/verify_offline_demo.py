"""Verify/extract final offline demo downloads against their single producer package."""

import argparse
import gzip
import hashlib
import json
import re
import shutil
import zipfile
from pathlib import Path, PurePosixPath

LICENSE_FILES = (
    "NOTICE.txt",
    "licenses/NOTICE.txt",
    "licenses/ODbL-1.0.html",
    "licenses/DbCL-1.0.html",
)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_archive(path, reference, destination, *, windows):
    expected_manifest = (reference / "manifest.json").read_bytes()
    manifest = json.loads(expected_manifest)
    if (
        manifest.get("region_id") != "belarus"
        or manifest.get("schema_version") != 3
        or manifest.get("coordinate_system") != "EPSG:4326"
        or not re.fullmatch(r"[a-f0-9]{64}", manifest.get("database_sha256", ""))
    ):
        raise ValueError("Producer is not a supported full Belarus package")
    prefix = "Data/" if windows else "assets/Data/"
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive entries")
        if windows and len(names) != len({name.casefold() for name in names}):
            raise ValueError("Case-insensitive Windows archive path collision")
        for name in names:
            parts = PurePosixPath(name).parts
            if (
                not parts
                or name.startswith("/")
                or name.rstrip("/") != PurePosixPath(name).as_posix()
                or ".." in parts
                or re.search(r"[\\:]", name)
            ):
                raise ValueError(f"Unsafe archive entry: {name}")
        if archive.read(prefix + "manifest.json") != expected_manifest:
            raise ValueError("Final archive manifest differs from the producer")
        if windows:
            for name in ("CableRoutePlanner.Desktop.exe", "hostfxr.dll", "coreclr.dll"):
                if not archive.getinfo(name).file_size:
                    raise ValueError(f"Missing self-contained application file: {name}")
        required = (*LICENSE_FILES, "elevation/index.json", "elevation/NOTICE.txt")
        for name in required:
            if not (reference / name).is_file():
                raise ValueError(f"Producer package lacks required file: {name}")
        metadata = [
            item
            for item in reference.rglob("*")
            if item.is_file()
            and (
                item.relative_to(reference).parts[0] in ("licenses", "elevation")
                or item.relative_to(reference).as_posix()
                in (
                    "manifest.json",
                    "NOTICE.txt",
                    "elevation.sqlite",
                    "elevation.sqlite.sha256",
                )
            )
        ]
        # Compare every DEM shard, checksum and notice, not just their filenames.
        for item in metadata:
            name = item.relative_to(reference).as_posix()
            with archive.open(prefix + name) as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != digest(item):
                    raise ValueError(f"Final archive differs from producer: {name}")
        destination.mkdir(parents=True, exist_ok=False)
        if windows:
            archive.extractall(destination)
        else:
            data = destination / "Data"
            data.mkdir()
            for item in metadata:
                relative = item.relative_to(reference)
                output = data / relative
                output.parent.mkdir(parents=True, exist_ok=True)
                with (
                    archive.open(prefix + relative.as_posix()) as source,
                    output.open("wb") as target,
                ):
                    shutil.copyfileobj(source, target)
            chunks = sorted(
                name
                for name in names
                if re.fullmatch(prefix + r"geodata\.\d{4}\.gz", name)
            )
            if not chunks or chunks != [
                prefix + f"geodata.{index:04d}.gz" for index in range(len(chunks))
            ]:
                raise ValueError("Missing or noncontiguous Android map chunks")
            with (data / "geodata.sqlite").open("wb") as output:
                for name in chunks:
                    with (
                        archive.open(name) as compressed,
                        gzip.GzipFile(fileobj=compressed) as stream,
                    ):
                        shutil.copyfileobj(stream, output)
        if (
            digest(destination / "Data/geodata.sqlite")
            != manifest["database_sha256"].lower()
        ):
            raise ValueError("Final archive database SHA-256 differs from manifest")


def pack(directory, output):
    output.parent.mkdir(parents=True, exist_ok=True)
    # ZIP64 handles full-country SQLite files larger than Compress-Archive's limit.
    with zipfile.ZipFile(
        output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6
    ) as archive:
        for item in sorted(directory.rglob("*")):
            if item.is_file():
                archive.write(item, item.relative_to(directory).as_posix())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("archive", type=Path)
    verify.add_argument("--reference", type=Path, required=True)
    verify.add_argument("--destination", type=Path, required=True)
    verify.add_argument("--windows", action="store_true")
    package = commands.add_parser("pack")
    package.add_argument("directory", type=Path)
    package.add_argument("output", type=Path)
    record = commands.add_parser("record")
    record.add_argument("file", type=Path)
    record.add_argument("--sha", required=True)
    record.add_argument("--url", required=True)
    record.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "verify":
        verify_archive(
            args.archive, args.reference, args.destination, windows=args.windows
        )
        print(f"Verified exact producer data in {args.archive}")
    elif args.command == "pack":
        pack(args.directory, args.output)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(
                {
                    "file": args.file.name,
                    "source_sha": args.sha,
                    "bytes": args.file.stat().st_size,
                    "sha256": digest(args.file),
                    "url": args.url,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )


if __name__ == "__main__":
    main()
