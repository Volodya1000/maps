"""Verified, versioned country inputs and packages. No network access on import."""

import argparse
import gzip
import hashlib
import http.client
import json
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / "tools/GeodataPreparation/belarus.lock.json"
# Include all country conversion inputs, but not unrelated tooling/development deps.
INPUTS = (
    "tools/GeodataPreparation/country_data.py",
    "tools/GeodataPreparation/prepare_country.py",
    "tools/GeodataPreparation/prepare.py",
    "tools/GeodataPreparation/display_lod.py",
    "tools/GeodataPreparation/feature_classification.py",
    "tools/GeodataPreparation/pipeline_crossings.py",
    "tools/GeodataPreparation/pyproject.toml",
    "tools/GeodataPreparation/uv.lock",
    "tools/GeodataPreparation/COUNTRY-NOTICE.txt",
)
LICENSES = "samples/real-geodata-fixtures/minsk/licenses"


def stream_sha256(stream):
    digest = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(block)
    return digest.hexdigest()


def sha256(path):
    with path.open("rb") as stream:
        return stream_sha256(stream)


def cache_key(lock, root=ROOT):
    digest = hashlib.sha256()
    digest.update(
        json.dumps(
            {
                "source": {
                    name: lock["source"][name] for name in ("version", "sha256")
                },
                "runtime": lock["runtime"],
            },
            sort_keys=True,
        ).encode()
    )
    paths = [root / name for name in INPUTS]
    paths.extend(sorted((root / LICENSES).rglob("*")))
    for path in paths:
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode() + b"\0")
            digest.update(bytes.fromhex(sha256(path)))
    return "belarus-v1-" + digest.hexdigest()


def download(spec, destination):
    """Every source must yield exactly the reviewed bytes; publish atomically."""
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.is_file() and sha256(destination) == spec["sha256"]:
        print(f"Verified local file: {destination}", flush=True)
        return
    failures = []
    for url in spec["urls"]:
        if not url.startswith("https://"):
            raise ValueError(f"Only HTTPS sources are allowed: {url}")
        for attempt in range(3):
            try:
                with tempfile.NamedTemporaryFile(
                    dir=destination.parent, delete=False, suffix=".partial"
                ) as target:
                    temporary = Path(target.name)
                try:
                    with temporary.open("wb") as target:
                        with urllib.request.urlopen(url, timeout=60) as response:
                            if not response.url.startswith("https://"):
                                raise ValueError("Source redirected away from HTTPS")
                            shutil.copyfileobj(response, target, 1024 * 1024)
                    actual = sha256(temporary)
                    if actual != spec["sha256"]:
                        raise ValueError(
                            f"SHA-256 mismatch: expected {spec['sha256']}, got {actual}"
                        )
                except BaseException:
                    temporary.unlink()
                    raise
                temporary.replace(destination)
                print(f"Downloaded and verified {url}", flush=True)
                return
            except (OSError, ValueError, http.client.HTTPException) as error:
                failures.append(f"{url} (attempt {attempt + 1}): {error}")
                if isinstance(error, ValueError) or (
                    isinstance(error, urllib.error.HTTPError) and error.code < 500
                ):
                    break
    raise RuntimeError(
        "No verified geodata source available. No latest substitution was made.\n"
        + "\n".join(failures)
        + "\nSee docs/ci-geodata.md for recovery."
    )


def package_files(directory):
    return sorted(
        path
        for path in directory.rglob("*")
        if path.is_file() and path.name != "package.json"
    )


def validate(lock, directory, key):
    receipt = json.loads((directory / "package.json").read_text())
    if receipt["key"] != key or receipt["source_sha256"] != lock["source"]["sha256"]:
        raise ValueError("Country package provenance differs from requested inputs")
    prepared = lock.get("prepared", {})
    if prepared.get("key") == key:
        for name, spec in prepared["files"].items():
            if sha256(directory / name) != spec["sha256"]:
                raise ValueError(f"Pinned package SHA-256 mismatch: {name}")
    expected = receipt["files"]
    required = {
        "geodata.sqlite.gz",
        "manifest.json",
        "NOTICE.txt",
        "licenses/NOTICE.txt",
    }
    if not required.issubset(expected):
        raise ValueError("Country package is missing required files")
    actual = {
        p.relative_to(directory).as_posix(): sha256(p) for p in package_files(directory)
    }
    if expected != actual:
        raise ValueError("Country package is incomplete or has a SHA-256 mismatch")
    manifest = json.loads((directory / "manifest.json").read_text())
    if manifest["sources"][0]["sha256"] != lock["source"]["sha256"]:
        raise ValueError("Country manifest source SHA-256 differs from lock")


def ensure(lock, directory, root=ROOT):
    key = cache_key(lock, root)
    if directory.exists():
        # Fail closed on a damaged cache; do not silently use or overwrite it.
        validate(lock, directory, key)
        print(f"Verified prepared cache: {key}")
        return
    prepared = lock.get("prepared")
    if prepared and prepared["key"] == key:
        # Each file has its own committed digest. No archive extraction is needed.
        directory.mkdir(parents=True)
        try:
            for name, spec in prepared["files"].items():
                relative = Path(name)
                if relative.is_absolute() or ".." in relative.parts:
                    raise ValueError(f"Unsafe package path: {name}")
                download(spec, directory / relative)
            validate(lock, directory, key)
        except BaseException:
            shutil.rmtree(directory)
            raise
    else:
        # Only a deliberate snapshot/converter change can reach conversion.
        prepare(lock, directory, root)


def prepare(lock, directory, root=ROOT):
    uv_version = subprocess.check_output(["uv", "--version"], text=True).split()[1]
    if uv_version != lock["runtime"]["uv"]:
        raise ValueError(f"Install uv {lock['runtime']['uv']} before preparing geodata")
    source = root / "artifacts/osm" / (lock["source"]["sha256"] + ".osm.pbf")
    download(lock["source"], source)
    subprocess.run(
        [
            "uv",
            "sync",
            "--project",
            str(root / "tools/GeodataPreparation"),
            "--locked",
            "--python",
            lock["runtime"]["python"],
        ],
        check=True,
    )
    with tempfile.TemporaryDirectory(dir=directory.parent, prefix="country-") as stage:
        staged = Path(stage) / "data"
        subprocess.run(
            [
                "uv",
                "run",
                "--python",
                lock["runtime"]["python"],
                "--project",
                str(root / "tools/GeodataPreparation"),
                "--locked",
                "python",
                str(root / "tools/GeodataPreparation/prepare_country.py"),
                "--pbf",
                str(source),
                "--expected-sha256",
                lock["source"]["sha256"],
                "--output",
                str(staged),
                "--compress",
            ],
            check=True,
        )
        seal(lock, staged, root)
        staged.rename(directory)


def seal(lock, directory, root=ROOT):
    """Finalize converter output and write a checksum receipt for shared consumers."""
    shutil.copytree(root / LICENSES, directory / "licenses", dirs_exist_ok=True)
    notice = root / "tools/GeodataPreparation/COUNTRY-NOTICE.txt"
    shutil.copyfile(notice, directory / "NOTICE.txt")
    shutil.copyfile(notice, directory / "licenses/NOTICE.txt")
    manifest = json.loads((directory / "manifest.json").read_text())
    with gzip.open(directory / "geodata.sqlite.gz", "rb") as stream:
        actual = stream_sha256(stream)
    if actual != manifest["database_sha256"]:
        raise ValueError("Compressed database SHA-256 differs from converter manifest")
    (directory / "geodata.sqlite").unlink(missing_ok=True)
    receipt = {
        "key": cache_key(lock, root),
        "source_sha256": lock["source"]["sha256"],
        "files": {
            p.relative_to(directory).as_posix(): sha256(p)
            for p in package_files(directory)
        },
    }
    (directory / "package.json").write_text(json.dumps(receipt, indent=2) + "\n")
    validate(lock, directory, receipt["key"])


def pin(lock, directory, release_url):
    """Record published package files; upload them before committing this lock."""
    validate(lock, directory, cache_key(lock))
    files = {}
    for path in [*package_files(directory), directory / "package.json"]:
        name = path.relative_to(directory).as_posix()
        # Flat release assets have unique names, including license files.
        asset = name.replace("/", "--")
        files[name] = {"sha256": sha256(path), "urls": [release_url + "/" + asset]}
    lock["prepared"] = {"key": cache_key(lock), "files": files}
    LOCK.write_text(json.dumps(lock, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "command",
        choices=("key", "source", "ensure", "prepare", "validate", "seal", "pin"),
    )
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/belarus")
    parser.add_argument("--release-url")
    args = parser.parse_args()
    lock = json.loads(LOCK.read_text())
    if args.command == "key":
        print(cache_key(lock))
    elif args.command == "source":
        download(lock["source"], args.output)
    elif args.command == "pin":
        if not args.release_url or not args.release_url.startswith(
            "https://github.com/Volodya1000/maps/releases/download/"
        ):
            parser.error("pin requires the repository release download URL")
        pin(lock, args.output, args.release_url.rstrip("/"))
    elif args.command == "validate":
        validate(lock, args.output, cache_key(lock))
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        {"ensure": ensure, "prepare": prepare, "seal": seal}[args.command](
            lock, args.output
        )


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Geodata error: {error}", file=sys.stderr)
        sys.exit(1)
